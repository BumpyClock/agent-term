# Kitty graphics

AgentTerm draws images that programs send with the [Kitty graphics protocol](https://sw.kovidgoyal.net/kitty/graphics-protocol/). Tools such as `kitten icat`, `chafa`, `timg` and image-capable TUIs show inline pictures in a pane. No setting is needed. Images scroll with output, stay in history, and follow the alternate screen.

## What works.

The table lists protocol features and their state.

| Feature | State |
| --- | --- |
| Transmission | Direct (`t=d`) payloads, chunked with `m=1`. Raw RGB (`f=24`), RGBA (`f=32`) and PNG (`f=100`). Zlib compression (`o=z`). |
| Placement | Fixed placements at the cursor or by `a=p`, relative placements (`P=`), and virtual placements with Unicode placeholders (`U=1`). |
| Geometry | Source rectangles (`x,y,w,h`), cell sizing (`c,r`), cell offsets (`X,Y`) and aspect-preserving scaling. |
| Layers | Negative z below `-1073741824` draws under cell backgrounds. Other negative z draws over backgrounds and under text. Zero and above draws over text. z orders images within a layer. |
| Lifetime | Replacement, delete by id or placement (`a=d`), full reset (`ESC c`), and separate primary and alternate screen storage. |
| Animation | Frames (`a=f`) and playback control (`a=a`). libghostty-vt also handles composition (`a=c`), which is untested here. Frames advance only while the pane draws. |
| Replies | Queries (`a=q`) report support. Errors report the reason, for example an unsupported medium. |

File, temporary-file and shared-memory media (`t=f`, `t=t`, `t=s`) are disabled. The terminal replies with an error, so programs fall back to direct transmission. ConPTY passes the APC sequences through unchanged. A headless ConPTY check verifies raw and chunked PNG transmissions from PowerShell.

## Memory.

Each screen stores up to 64 MiB of decoded images (`TerminalSession.DefaultImageStorageLimit`). libghostty-vt evicts older images above that. The library default is 10 MB, which one full-HD RGBA image fills. Pixels exist in native storage, owned capture data, and device bitmaps. Retained frames can extend the lifetime of old pixel arrays.

1. libghostty-vt storage holds decoded pixels for the active screen, up to the storage limit.
2. `GhosttyTerminal` holds an immutable raw capture and one premultiplied RGBA copy of each image that has a placement on the active screen. The copy is dropped when the image is replaced, deleted, or its screen is no longer active. `ImageCacheBytes` reports the premultiplied cache, excluding raw captures and retained frames.
3. `TerminalSurface` holds device bitmaps. The cache admits at most 64 MiB, evicting oldest entries including current-frame crops before an upload. Pending drawing commands are flushed before eviction. A single crop above 64 MiB is transient and released after its draw is flushed, so peak bitmap ownership is bounded by the larger of 64 MiB and one device-sized crop, independent of placement count. A hidden pane, a released canvas and a lost device free all bitmaps.

## How a frame gets its images.

`TerminalSession.ReadFrame` captures images in the same two phases as text.

1. `BeginFrame` runs under the session lock. It advances animations, begins the render update, and copies pixels of placed images whose generation is new. Pixels in libghostty-vt storage stay valid only until the next terminal write, so they are never read outside the lock.
Render-hold callbacks also freeze an independent capture of image pixels at the hold boundary. An in-flight frame and a pending handoff never share mutable capture data.

2. `CompleteFrame` runs without the session lock. It reads positioned placements from the render state, converts copied pixels to premultiplied RGBA, and publishes `TerminalFrame.Images`.

`TerminalFrame.Images` is a `TerminalImages` record. Placements are sorted in draw order. Each placement references an immutable `TerminalImageData` whose `Generation` uniquely names its pixels. An unchanged image list keeps its instance, so a frame whose images changed without text changes still gets a new frame with the same cell array.

PNG decoding is a process-wide libghostty-vt callback. `PngDecoder` is a bounded managed decoder for every PNG 1.2 color type, bit depth and Adam7 interlacing. It validates the exact inflated length with an 8 KiB scratch buffer before allocating an IHDR-sized managed buffer. Valid input is decompressed again to decode pixels. The native callback decodes before requesting native output memory, so impossible dimensions or a short inflated stream never request a large native allocation. The library still enforces its storage limit when native output is allocated.

## Pixel geometry.

libghostty-vt positions images only when it knows the cell size in device pixels. `MainPage` supplies it through the shared `TerminalSession.Resize` geometry path from the font cell size and the display scale. It updates on font, grid and DPI changes. Size reports and image placement use the same device-pixel dimensions. The surface maps native pixels to drawn cells by the ratio of the drawn cell size to that value, so an image that spans n cells draws over exactly n cells.

## Drawing.

`TerminalSurface` draws each layer at its place in the cell paint order. A cropped placement gets a bitmap of just its source rectangle, so filtered scaling does not blend pixels from outside the crop. Visible and overscan cells share one ordered composition with images at the original frame coordinates. Fractional scrolling clips images to the surface viewport after applying the fractional origin. Images larger than the device bitmap limit are resampled to fit. The cursor overlay draws above images.

## Tests.

1. `tests/AgentTerm.Terminal.Smoke/KittyImageChecks.cs` covers placement, ownership, replacement, deletion, RGB, zlib, PNG, crop, scaling, offsets, layers, scrolling, history, alternate screen, reset, virtual and relative placements, replies, disabled media, the storage limit, eviction and animation. `PngDecoderChecks.cs` decodes 30 fixtures covering every color type, bit depth, filter type and Adam7, and 2000 corrupted inputs. `make_png_fixtures.py` regenerates the fixtures with an independent encoder that Pillow cross-checks. Run both alone with `--images-only`.
2. `tests/AgentTerm.Renderer.Smoke/KittyImageRenderChecks.cs` checks drawn pixels for each layer, alpha, crop, scale, offsets, clipping, a terminal-produced frame and the bitmap cache. Run it alone with `--images-only`.
3. `tests/AgentTerm.ConPty.Smoke/KittyImageSessionChecks.cs` sends raw and chunked PNG images from PowerShell through ConPTY. Run it alone with `--images-only`.

Images below the last text row count as content in bottom-anchored mode. Offline archive viewers set cell geometry before drawing restored images. Standalone terminals enable image storage explicitly; application sessions enable the renderer and its 64 MiB limit together.

Placement-list generations belong to a render-state instance. Retiring, adopting or recovering that state invalidates both placement and retry generations. Pixel generations retain their independent immutable identity. Only the terminal assembly constructs image data; renderer smoke tests have friend visibility for fixtures. `TerminalImages.DefaultStorageLimit` defines the shared host/session default.

Kitty U+10EEEE placeholder cells retain their text, grapheme extent, selection and native image metadata. Canonical materialization marks them hidden for drawing, which also suppresses glyphs in cursor redraws and overscan rows.

Review regressions cover repeated placement generations after scrolling handoff and allocator recovery, unsigned maximum IHDR dimensions, bounded allocation for huge IHDR with tiny compressed data, 100 current-frame near-full crops, pending-draw resource disposal, and half-row overscan image/background/text order in all three layers. Renderer regressions require the parent's consolidated raster run.
