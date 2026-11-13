# Terminal performance evidence.

This folder keeps summarized, reviewable evidence for terminal rendering changes. Raw trial JSON, PNGs and traces stay under `artifacts/`, which is gitignored. Each entry names the code it measured, the exact command and what the numbers do not show.

- [Performance after GitHub review, 2026-11-12](terminal-20261112-review.md).
- [Renderer and demand scheduling before GitHub review, 2026-11-12](terminal-20261112-current.md).
- [Intermediate composition comparison, 2026-11-12. Historical](#intermediate-composition-comparison-2026-11-12-historical).

## Intermediate composition comparison, 2026-11-12. Historical.

This comparison was the source of the "7.72 to 11.59 ms" figures in earlier drafts. Read it with these limits.

- **Baseline was not HEAD.** The "previous" side was an uncommitted intermediate build. It already had the cached glyph layouts and used a saved adapter snapshot (`artifacts/perf60/TerminalSurface.baseline.cs`, `GhosttyTerminal.Render.baseline.cs`). Those files differ from 5606a46.
- **Schedule and code changed together.** The baseline polled on a 33 ms timer. The optimized side refreshed after every composition frame. Neither is the shipped demand scheduler. Differences mix cadence with code.
- **Fixture only.** UI-thread callback time in the native fixture. No tabs, timelines, task cards or chrome. No presented-frame data.

Setup, measured. 60 Hz agent workload, 12 s, 5 alternating trials per side, 284 × 76 grid, 2560 × 1369.33 DIP at 144 DPI, FiraCode Nerd Font 14 DIP. All 10 final raster hashes matched.

| Median of 5 trials. | Intermediate baseline, 33 ms timer. | Optimized, continuous composition. |
| --- | --- | --- |
| Snapshot callback p95. | 12.47 ms. | 5.22 ms. |
| Snapshot plus draw callback p95. | Not instrumented. | 11.15 ms (range 7.72 to 11.59). |
| Draw callback p95. | Not instrumented. | 6.83 ms. |
| Canvas callbacks per second. | 21.7. | 56.8. |
| UI heartbeat p95. | 52.3 ms. | 36.0 ms. |
| Process CPU per trial. | 14.4 s. | 10.3 s. |
| GC pause per trial. | 203 ms. | 99 ms. |
| Allocation per trial. | 289 MB. | 122 MB. |

Other single runs from the same session, measured. A 120 Hz agent workload gave 12.40 ms frame-work p95. A 60 Hz dense workload gave 48.02 ms frame-work p95, which fails the 16.6 ms budget. Retained rasters help sparse updates; they do not make a fully new screen cheap.

A Windows.Graphics.Capture run observed 624 compositor frames and 620 distinct terminal counter images in about 12 s. Capture can coalesce frames, so this proves changing pixels reached the compositor. It does not prove 60 scanouts per second.

These results cannot be rebuilt from the repository, because the intermediate sources exist only under `artifacts/perf60/`. Current comparisons build the baseline from 5606a46 instead.
