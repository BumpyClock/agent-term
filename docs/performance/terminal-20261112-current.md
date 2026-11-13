# Current renderer and demand scheduling, 2026-11-12.

Five alternating trials per side passed the native-fixture workload and image gates at `46eb3a1`, before the subsequent review fixes. The optimized p95 snapshot-plus-draw callback work was 7.64 to 8.29 ms, with a median of 7.88 ms. Every optimized trial passed the 16.6 ms p95 callback-work gate. This is not a measurement of displayed FPS or full-application frame time. Earlier functional checks below are labelled separately.

Machine. Development desktop, 2560 × 1369.33 DIP terminal surface at 144 DPI when maximized, 1.5 rasterization scale. FiraCode Nerd Font at 14 DIP. Python 3 through real ConPTY.

## Build inputs.

Measured builds used local revision `88a4fd5` and completed with zero errors and zero warnings. Its published equivalent is `5606a46`. Only commit email metadata changed for GitHub privacy. Both revisions have the identical Git tree `fa79933ee1f88841a0a455f7cb53f9f7124b4e06`. Commands below use the published revision; the measured source files are unchanged. Baseline file hashes, SHA-256:

- `src/AgentTerm.App/TerminalSurface.cs` at 5606a46. `82B3AD34781A0272C3EC7CBB995A289048165E0101C9969978C8E219363F3B1D`.
- `src/AgentTerm.Terminal/GhosttyTerminal.Render.cs` at 5606a46. `9ED74768A3123AAB2791A09DBCEFB28C868EBFC613ECE23C577FA2B7FB2CAFCE`.

The optimized side was a frozen source snapshot equivalent to `46eb3a1`. The [trial evidence](terminal-20261112-final.json) retains raw Windows build-input hashes and adds LF-normalized source hashes. The adapter and session used CRLF in the measured checkout and LF in Git; normalized bytes match exactly. This is a newline difference, not a code difference. The baseline and optimized executables used the same native DLL hash. These timings precede the later cache-shrink and refresh review fixes.

## Final alternating comparison.

Measured on the desktop with other user applications left open. Each side generated 720 updates at 60 Hz over 12 seconds, with 2,412 nonblank cells. All ten final raster hashes matched. The baseline used its 33 ms timer; the current version used demand scheduling. This compares the combined code and scheduling change. A matched-timer comparison was not run, so the table does not isolate either change.

| Median of five trials. | Baseline at 5606a46. | Optimized. |
| --- | --- | --- |
| Snapshot callback p95. | 4.99 ms. | 4.39 ms. |
| Canvas callbacks per second. | 19.71 /s. | 56.91 /s. |
| UI heartbeat interval p95. | 60.37 ms. | 35.54 ms. |
| Process CPU per trial. | 16.62 s. | 9.41 s. |
| GC pause per trial. | 1336.67 ms. | 81.12 ms. |
| Managed allocation per trial. | 475.89 MB. | 123.22 MB. |
| Terminal reads during two seconds of silence. | 43.00 reads. | 0.00 reads. |
| Snapshot plus draw callback work p95. | Unavailable. | 7.88 ms. Range 7.64 to 8.29. |

Fixed trial p95 values, in order. 7.6996, 7.9701, 7.8849, 7.6400, 8.2904 ms. Individual frames exceeded 16.6 ms. These callback timings exclude later EndDraw, GPU work and physical presentation. The baseline did not expose draw instrumentation, so no baseline total callback-work number is reported.

## Earlier scheduler work, measured.

Each row is one run. "Idle reads" counts `ReadFrame` calls during 2 s of shell silence after the workload ends.

| Run. | Schedule. | Grid. | Draws. | ReadFrame calls. | Idle reads. | Idle draws. |
| --- | --- | --- | --- | --- | --- | --- |
| 5606a46, agent, 60 Hz, 3 s. | 33 ms timer. | 284 × 76. | 58. | 134. | 51. | 0. |
| Current, agent, 60 Hz, 3 s. | Demand. | 284 × 76. | 171. | 174. | 0. | 0. |

The 5606a46 timer kept polling a silent shell about 25 times per second. Demand scheduling made no terminal reads while silent. With 180 updates, demand scheduling drew 171 times and the 33 ms timer drew 58 times. Both final rasters hashed to `619CD79FF7DD2D80EA1B7F0D4B040D3F899BD27D21DF6AE5933DCEEA2C42F563`, so both sides ended on identical pixels.

The fixture ran with an animated ProgressRing, so composition frames continued during the idle window. That is the case where a permanent `CompositionTarget.Rendering` subscriber would have done terminal work every frame.

## Synchronized-output hold, measured.

The `held` pattern starts `ESC[?2026h`, writes a marker and stays silent without releasing it. Under demand scheduling the marker reached a Canvas draw after 1,059.8 ms. One hold-timer refresh fired. No `ReadFrame` call happened in the following 2 s of silence. This is the fixture regression for the stranded-hold defect. The production application test is separate.

## Glyph cache under CJK, measured.

The `cjk` pattern changes every CJK cell on every update, sliding each row by 13 code points. Runs used `--windowed` so the grid was small enough for the stream to exceed the cache.

| Run. | Grid. | Capacity. | Largest count. | Layouts created. | Draws. | Private bytes, start to end. |
| --- | --- | --- | --- | --- | --- | --- |
| 12 s, 60 Hz. | 75 × 26. | 3,900. | 3,900. | 10,046. | 447. | 130.3 to 216.9 MB. |
| 36 s, 60 Hz. | 75 × 26. | 3,900. | 3,900. | 28,840. | 1,864. | 129.4 to 214.8 MB. |

The cache filled to its capacity and stayed there while creating 2.6 and 7.4 times its capacity. Layout creation averaged about 22 and 15 per draw. That is close to the glyphs entering the screen per update, so on-screen glyphs were not being recreated every frame. Private bytes grew by 86.6 MB and 85.4 MB although the 36 s run created 2.9 times as many layouts, so memory plateaued. Guessed cause of the one-time growth: DirectWrite loading CJK fallback fonts and the managed heap reaching steady state. Not traced.

Every timed run also checked that the retained raster equals a full redraw at the end.

## Raster parity, measured.

`--verify-transitions` passed 34 steps twice on the current renderer at 144 DPI. The steps are listed in the [fixture README](../../tests/AgentTerm.Renderer.Perf/README.md#raster-parity). The CJK steps created 64,746 layouts against a capacity of 43,168 without exceeding it. `SuspendRendering` released the raster and every layout, and the first draw after `ResumeRendering` matched a full redraw.

## Reproduce the comparison.

Use fresh output directories. The shipped agent comparison below was run. The matched-timer and CJK comparisons remain optional follow-up commands and were not run in the final timing phase.

```powershell
pwsh -NoProfile -File scripts/build-terminal-perf.ps1 -BaselineRevision 5606a46 -OutputDirectory artifacts/perf-final
$built = Get-Content artifacts/perf-final/build-inputs.json -Raw | ConvertFrom-Json
pwsh -NoProfile -File scripts/measure-terminal-perf.ps1 -BaselineExe $built.baselineExe -FixedExe $built.currentExe `
    -OutputDirectory artifacts/perf-final/agent-matched-timer -Hz 60 -Pattern agent -BaselineSchedule timer -FixedSchedule timer
pwsh -NoProfile -File scripts/measure-terminal-perf.ps1 -BaselineExe $built.baselineExe -FixedExe $built.currentExe `
    -OutputDirectory artifacts/perf-final/agent-shipped -Hz 60 -Pattern agent -BaselineSchedule timer -FixedSchedule demand
pwsh -NoProfile -File scripts/measure-terminal-perf.ps1 -BaselineExe $built.baselineExe -FixedExe $built.currentExe `
    -OutputDirectory artifacts/perf-final/cjk-shipped -Hz 60 -Pattern cjk -BaselineSchedule timer -FixedSchedule demand
```

## Not shown.

- Displayed frames per second. PresentMon lacked ETW access.
- Full-application timing with tabs, timelines and task cards.
- GPU memory. The retained-raster saving for hidden tabs is computed in [the frame budget](../terminal-frame-budget.md#hidden-panes), not measured.
- Dense full-screen cost under the current renderer. The last dense measurement failed the 16.6 ms budget.
