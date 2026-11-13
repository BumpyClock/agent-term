# Terminal frame budget.

This page is for maintainers changing terminal rendering or refresh scheduling. It explains how a visible pane turns shell output into pixels, what it costs when the shell is silent or the pane is hidden, and which numbers have been measured.

**Claim boundary.** All timings come from the native fixture in [tests/AgentTerm.Renderer.Perf](../tests/AgentTerm.Renderer.Perf/README.md). The fixture runs the production `TerminalSession` and `TerminalSurface` but omits tabs, agent timelines, task cards and window chrome. Its frame-work metric is UI-thread callback time. It excludes Win2D EndDraw, GPU work and presentation. Displayed 60 FPS has not been measured, because PresentMon was denied ETW access. The dense full-screen workload exceeds 16.6 ms.

## Scheduling.

`MainPage` requests terminal frames when output arrives, an input or view action changes local state, or a synchronized-output deadline expires.

1. The ConPTY reader thread parses output under the session lock. After releasing the lock it raises `TerminalSession.OutputAvailable`.
2. The handler enqueues at most one UI wake per burst.
3. If no snapshot was taken since the last `CompositionTarget.Rendering` tick, the UI wake reads a snapshot and calls `TerminalSurface.SetFrame` at once. `CanvasControl.Invalidate` registers Win2D's one-shot Rendering hook, so the draw lands on the upcoming tick.
4. Every snapshot adds a one-shot Rendering handler that marks the tick boundary. A wake that arrives before that tick only marks a refresh pending.
5. At the tick the handler removes itself. If a refresh is pending it queues one after the Rendering dispatch, and that snapshot draws on the following tick. A flood therefore costs at most one snapshot per composition frame.
6. When the snapshot is held by synchronized output (`ESC[?2026h`), `TerminalSession.PendingFrameRefreshDelay` is the time left on the 1,000 ms hold. A nonrepeating timer requests one more refresh at that time, so a writer that never releases the hold still appears. The timer is stopped on every refresh and whenever the pane hides or the agent surface opens.

The previous schedule always waited for a Rendering tick before the snapshot, so isolated output drew one frame later. The fixture keeps it as `--schedule deferred`.

Keystrokes do not read a snapshot. Process echo arrives through step 3. Input refreshes synchronously only when the presented frame has a selection or is scrolled into history, because sending input clears both locally. Selection checks read the presented frame instead of a fresh snapshot.

A silent visible pane therefore makes no `ReadFrame` calls and keeps no Rendering subscription. Chrome and directory polling run every 250 ms. The cursor blink timer runs every 528 ms only while a visible blinking cursor is focused. Hidden, unloaded and stopped panes stop all of these. While the agent surface covers a pane, terminal refresh and cursor blink stop and the terminal raster stays suspended across tab switches. Accepted application mouse presses refresh local selection changes without waiting for process output.

At 5606a46 production polled `ReadFrame` every 33 ms. The fixture still offers that timer to compare code at matched cadence.

## Snapshots.

Ghostty tracks dirty rows. The adapter copies only dirty rows into a new owned cell array and reuses unchanged cells and ASCII strings. Published snapshots are immutable. A cursor-only change shares the previous cell array. Selection, search, scroll, resize and theme changes recapture everything, because libghostty-vt marks rows dirty only when a selection is removed.

## Retained raster.

Each surface keeps one `CanvasRenderTarget` with the drawn grid. Each draw compares the new snapshot with the last painted one. Changed cells become spans with 2 columns of margin, merged with identical spans on the next row. Each span is snapped to device pixels, cleared with a copy blend and redrawn with 1 more column on each side.

Cell backgrounds skip cells that match the cleared default background. Adjacent cells in a row with the same fill, selection included, share one aliased `FillRectangle`. Text is still drawn per cell. An offscreen comparison of the HEAD and current `DrawFrame` matched byte for byte in 360 cases (opaque and transparent, selection, reverse video, 96 to 192 DPI, fractional cell width and grid top).

## Cursor overlay.

The cursor is drawn by a second, cell-sized `CanvasControl` above the main canvas. Its origin and size are multiples of 4 DIPs, so its pixels align with the main canvas at 25% scale steps. Blink toggles only its opacity. A cursor move or shape change redraws only the overlay. The main canvas invalidates only when cells, grid size, top inset, default background or selection colors change. A cursor-only snapshot shares the cell array, so the painted frame advances without a main draw. The overlay follows the main canvas lifetime: created per active attachment, released on suspend and unload.

Measured with Windows.Graphics.Capture at 150% scale against HEAD: bar, underline, hollow, wide-cell block and colored block cursors matched byte for byte. Two block cursors over text differed by at most 1 per channel on 2 and 9 glyph pixels. The cause is inferred, not proven: glyph pixels with alpha just under 255 now blend over the main canvas instead of the window backdrop.

A change in surface size, DPI, cell size, font, transparency, grid top, top inset, default background or selection colors repaints everything. Device loss and DPI changes recreate resources and repaint everything.

## Hidden panes.

`SetActive(false)`, `Page_Unloaded` and opening the agent surface call `TerminalSurface.SuspendRendering`. It releases the canvas, retained raster and cached glyph layouts. `ResumeRendering` creates a canvas once the surface is loaded, and the next draw repaints everything because no painted frame remains. Each canvas belongs to one active attachment. This also prevents old native load bookkeeping from crossing tab attachments. See [canvas lifetime](terminal-startup.md#canvas-lifetime).

Computed, not measured. At the fixture geometry of 3840 by 2054 device pixels, one retained raster is 3840 × 2054 × 4 B, about 31.5 MB. Ten hidden maximized tabs no longer keep about 315 MB of these rasters. Suspension releases the pane's canvas reference and event handlers as well. Actual GPU residency and collection of retired framework objects were not measured.

## Glyph layout cache.

Each surface caches clipped `CanvasTextLayout` objects keyed by text, width, bold and italic. Capacity is twice the grid's cell count, clamped to 1,024..65,536. Eviction is first in, first out. Shrinking the grid immediately disposes entries above its new capacity, including when every glyph on the smaller screen is a cache hit.

Twice the cell count leaves room for the visible glyph set and recent replacements in the measured grids. Text that keeps changing, such as streamed CJK, evicts older entries. Memory stays bounded at 65,536 layouts per visible surface. Grids with more distinct glyph keys than this cap can still churn. Suspended surfaces hold none.

## Measurement and checks.

The 2026-11-12 native-fixture comparison used five alternating trials per side, 720 updates at 60 Hz over 12 seconds, and a 284 by 76 grid at 144 DPI with FiraCode Nerd Font at 14 DIP. Optimized snapshot-plus-draw callback p95 ranged from 9.88 to 11.43 ms, with a median of 10.66 ms. All ten final rasters matched, and optimized panes made zero terminal reads during two seconds of silence. Code and scheduling changed together. These measurements cover the fixture and the claim boundary above, not displayed FPS.

The [fixture README](../tests/AgentTerm.Renderer.Perf/README.md) documents reproducible workloads, comparisons and validation gates. Dated trial dumps and investigation notes are retained in [PR #10](https://github.com/BumpyClock/agent-term/pull/10)'s review history rather than the source tree.

Raster parity is checked by `--verify-transitions` in the fixture and runs from `scripts/check.ps1`. Startup and focus regressions run through [check-startup.ps1](../scripts/check-startup.ps1).
