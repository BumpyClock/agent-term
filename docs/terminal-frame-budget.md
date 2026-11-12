# Terminal frame budget.

This page is for maintainers changing terminal rendering or refresh scheduling. It explains how a visible pane turns shell output into pixels, what it costs when the shell is silent or the pane is hidden, and which numbers have been measured.

**Claim boundary.** All timings come from the native fixture in [tests/AgentTerm.Renderer.Perf](../tests/AgentTerm.Renderer.Perf/README.md). The fixture runs the production `TerminalSession` and `TerminalSurface` but omits tabs, agent timelines, task cards and window chrome. Its frame-work metric is UI-thread callback time. It excludes Win2D EndDraw, GPU work and presentation. Displayed 60 FPS has not been measured, because PresentMon was denied ETW access. The dense full-screen workload exceeds 16.6 ms.

## Scheduling.

`MainPage` requests terminal frames when output arrives, an input or view action changes local state, or a synchronized-output deadline expires.

1. The ConPTY reader thread parses output under the session lock. After releasing the lock it raises `TerminalSession.OutputAvailable`.
2. The handler enqueues at most one UI wake per burst.
3. The UI wake adds a one-shot `CompositionTarget.Rendering` handler.
4. That handler removes itself and queues one refresh after the Rendering dispatch. Queuing after the dispatch lets Win2D's own one-shot draw hook schedule the next frame.
5. The refresh reads a snapshot and calls `TerminalSurface.SetFrame`.
6. When the snapshot is held by synchronized output (`ESC[?2026h`), `TerminalSession.PendingFrameRefreshDelay` is the time left on the 1,000 ms hold. A nonrepeating timer requests one more refresh at that time, so a writer that never releases the hold still appears. The timer is stopped on every refresh and whenever the pane hides or the agent surface opens.

A silent visible pane therefore makes no `ReadFrame` calls and keeps no Rendering subscription. Chrome and directory polling run every 250 ms. The cursor blink timer runs every 528 ms only while a blinking cursor is focused. Hidden, unloaded and stopped panes stop all of these. While the agent surface covers a pane, terminal refresh and cursor blink stop and the terminal raster is suspended.

At 5606a46 production polled `ReadFrame` every 33 ms. The fixture still offers that timer to compare code at matched cadence.

## Snapshots.

Ghostty tracks dirty rows. The adapter copies only dirty rows into a new owned cell array and reuses unchanged cells and ASCII strings. Published snapshots are immutable. A cursor-only change shares the previous cell array. Selection, search, scroll, resize and theme changes recapture everything, because libghostty-vt marks rows dirty only when a selection is removed.

## Retained raster.

Each surface keeps one `CanvasRenderTarget` with the drawn grid. Each draw compares the new snapshot with the last painted one. Changed cells become spans with 2 columns of margin, merged with identical spans on the next row. Each span is snapped to device pixels, cleared with a copy blend and redrawn with 1 more column on each side. The cursor is drawn on top of the copied raster every frame.

A change in surface size, DPI, cell size, font, transparency, grid top, top inset, default background or selection colors repaints everything. Device loss and DPI changes recreate resources and repaint everything.

## Hidden panes.

`SetActive(false)`, `Page_Unloaded` and opening the agent surface call `TerminalSurface.SuspendRendering`. It releases the canvas, retained raster and cached glyph layouts. `ResumeRendering` creates a canvas once the surface is loaded, and the next draw repaints everything because no painted frame remains. Each canvas belongs to one active attachment. This also prevents old native load bookkeeping from crossing tab attachments. See [the startup evidence](terminal-startup.md#canvas-ownership-across-tab-switches).

Computed, not measured. At the fixture geometry of 3840 by 2054 device pixels, one retained raster is 3840 × 2054 × 4 B, about 31.5 MB. Ten hidden maximized tabs no longer keep about 315 MB of these rasters. Suspension releases the pane's canvas reference and event handlers as well. Actual GPU residency and collection of retired framework objects were not measured.

## Glyph layout cache.

Each surface caches clipped `CanvasTextLayout` objects keyed by text, width, bold and italic. Capacity is twice the grid's cell count, clamped to 1,024..65,536. Eviction is first in, first out.

Twice the cell count leaves room for the visible glyph set and recent replacements in the measured grids. Text that keeps changing, such as streamed CJK, evicts older entries. Memory stays bounded at 65,536 layouts per visible surface. Grids with more distinct glyph keys than this cap can still churn. Suspended surfaces hold none.

## Evidence.

Current measurements, commands and build inputs are in [docs/performance](performance/README.md). Older intermediate results are kept there as history with their limits. The first investigation of the original stall is in [the 2026-11-12 investigation](terminal-performance-20261112.md).

Raster parity is checked by `--verify-transitions` in the fixture and runs from `scripts/check.ps1`. Startup and focus regressions run through [check-startup.ps1](../scripts/check-startup.ps1).
