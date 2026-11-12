# Terminal rendering investigation, 2026-11-12. Historical.

This page records the first investigation into terminal UI stalls. Its numbers describe intermediate code, not the current renderer or scheduler. For the current design, measurement procedure and evidence, read [the frame budget](terminal-frame-budget.md) and [docs/performance](performance/README.md).

## Problem.

Users saw UI stalls during dense terminal output. Dense redraws created thousands of WinRT clipping-layer objects per frame and shaped each character again on the UI thread.

The first fix drew each glyph through a cached, self-clipping `CanvasTextLayout` instead of a clipping layer. At that time the cache held at most 1,024 layouts per surface. The current cache is sized from the grid (see [the frame budget](terminal-frame-budget.md#glyph-layout-cache)).

## Evidence from the affected application.

These captures came from a running Release build. Measured values follow.

- A 25-second WinApp ETW capture recorded UI Frame scopes with a mean of 267.6 ms, p95 373.5 ms and maximum 416.1 ms. No events were lost. These scopes measure UI elapsed time, not presented frames.
- A sampled thread-time capture of about 20 seconds put `TerminalSurface.DrawFrame` on the UI thread stack for about 18.6 seconds. This is inclusive wall time, not CPU percentage.
- Runtime counters over nine one-second intervals recorded 6 to 9 generation-2 collections and 471 to 546 ms of GC pause per second.
- The ETW capture recorded 178 background generation-2 collections with reason 7, "induced but not forced as blocking" in [Microsoft's GC event definitions](https://github.com/dotnet/docs/blob/main/docs/fundamentals/diagnostics/runtime-garbage-collection-events.md).

Inferred cause. CsWinRT's object-reference constructor adds native memory pressure for each wrapped clipping layer ([source](https://raw.githubusercontent.com/microsoft/CsWinRT/master/src/WinRT.Runtime/ObjectReference.cs)). The matched experiment below supports this mechanism. It does not prove that every collection came from that one API.

## Matched experiment, clipping layers against cached layouts.

Five alternating trials per side ran the native fixture with a 30 Hz agent workload, a 33 ms refresh timer on both sides, a 284 by 76 grid and FiraCode Nerd Font at 14 DIP. All ten final raster hashes matched. Medians follow, with ranges.

| Metric. | Clipping layers. | Cached layouts. |
| --- | --- | --- |
| UI heartbeat p95 interval. | 99.34 ms. 84.78 to 114.70. | 48.31 ms. 40.99 to 59.66. |
| Canvas callback p95 interval. | 95.57 ms. 84.94 to 116.00. | 63.86 ms. 55.93 to 68.94. |
| Process CPU per trial. | 13.89 s. | 8.39 s. |
| GC pause per trial. | 1,440 ms. | 257 ms. |
| Generation-2 collections. | 267. | 13. |

The schedule was the same on both sides, so this table isolates the drawing change. The fixture omits production tabs, agent timelines and window chrome. It does not show 60 FPS or presented-frame timing.

## Corrections to earlier statements.

- Production no longer uses a 33 ms timer. It refreshes on terminal output through a one-shot composition hook. See [the frame budget](terminal-frame-budget.md#scheduling).
- The acrylic smoke check failure (#FF4C4C4C expected, #FF4D4D4D observed) is resolved. `AcrylicRecipeChecks` now allows one RGB byte of GPU quantization and keeps alpha exact. The verification machine reports an NVIDIA GeForce RTX 3090 with driver 32.0.16.2101.
- Blank terminal screenshots of the full application had a startup cause unrelated to drawing speed. [Terminal startup](terminal-startup.md) records the reproduced trigger and the startup check.

Raw traces stay local under `artifacts/perf-investigation-20261112/`, which is gitignored.
