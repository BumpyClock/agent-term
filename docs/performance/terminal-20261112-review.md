# Performance after GitHub review, 2026-11-12.

Five alternating trials per side passed workload, raster equality and the optimized p95 callback-work gate after the review fixes. Optimized p95 values ranged from 9.88 to 11.43 ms, with a median of 10.66 ms. These are native-fixture callback timings, not displayed FPS or full-application frame time.

## Inputs and scope.

The optimized source is `18714f8e0fea1f7f464c586cbc6d624b7e7fc131`. The build began with a clean working tree. Baseline is `5606a460c7dbca60db3c2f78805c29d63c6802cc`. Both sides were rebuilt with the same fixture and native library. [Trial evidence](terminal-20261112-review.json) records raw Windows build-input hashes, LF-normalized hashes matching Git blobs, geometry and all ten trials. Source equivalence was checked before publication. Only documentation changed after the measured code commit.

Each trial generated 720 updates at 60 Hz over 12 seconds, with 2,412 nonblank cells on a 284 by 76 grid. The surface was 2560 by 1369.33 DIP at 144 DPI, using FiraCode Nerd Font at 14 DIP. Other user applications remained open. Tests and other fixture processes did not run concurrently. All ten final rasters had the same hash `F84AF40D6D743638BAC6212195F4CB40EC66C4321AB992BBE3718394F2C0E89C`.

Baseline used its production 33 ms timer. Optimized used demand scheduling. Code and scheduling changed together, so this comparison does not isolate either effect. A matched-timer comparison was not run.

## Measured medians.

| Median of five trials. | Baseline. | Optimized. |
| --- | --- | --- |
| Snapshot callback p95. | 7.92 ms. | 5.65 ms. |
| Canvas callbacks per second. | 14.67 /s. | 56.41 /s. |
| UI heartbeat interval p95. | 86.53 ms. | 31.34 ms. |
| Process CPU per trial. | 17.75 s. | 10.09 s. |
| GC pause per trial. | 1169.94 ms. | 100.09 ms. |
| Managed allocation per trial. | 355.47 MB. | 122.18 MB. |
| Terminal reads during two seconds of silence. | 53.00 reads. | 0.00 reads. |
| Snapshot plus draw callback work p95. | Unavailable. | 10.66 ms. Range 9.88 to 11.43. |

Optimized trial p95 values, in order. 10.9136, 10.4169, 9.8766, 11.4317, 10.6565 ms. Individual frames still exceeded 16.6 ms. The callback metric excludes later EndDraw, GPU execution and presentation. Baseline total callback work is unavailable because its renderer lacks draw instrumentation. Earlier measurements remain in [the pre-review report](terminal-20261112-current.md).

## Review regression checks.

Both Left and Top application startup runs passed click and wheel selection clearing without process echo, invisible blinking-cursor idle behavior, covered-tab activation without terminal resources, and focusable read-only startup input. The retained-raster suite passed 35 exact transitions. The new cache-shrink case failed against the previous renderer, retaining 43,168 layouts above its new 1,024 bound.

The supported 60-second comparison completed both 3,600-update trials with equal geometry and final pixels. Actual measured intervals were 60.0246 and 60.0345 seconds. This validates timeout overhead, not performance qualification. A separate three-second capture smoke returned 102 valid compositor samples without errors. Compiled capture-contract probes rejected errors and zero samples. Capture delivery is not physical display presentation.

## Reproduce.

Build the native DLL first, as described in the fixture README. Use fresh output directories.

```powershell
pwsh -NoProfile -File scripts/build-terminal-perf.ps1 -BaselineRevision 5606a46 -OutputDirectory artifacts/review-perf-final
$built = Get-Content artifacts/review-perf-final/build-inputs.json -Raw | ConvertFrom-Json
pwsh -NoProfile -File scripts/measure-terminal-perf.ps1 -BaselineExe $built.baselineExe -FixedExe $built.currentExe `
    -OutputDirectory artifacts/review-perf-final/agent-shipped -Hz 60 -Pattern agent -BaselineSchedule timer -FixedSchedule demand -RequireFrameBudget
```

Displayed 60 FPS, full-application timing, monitor-to-monitor DPI changes, forced device loss and GPU residency remain unverified. Dense full-screen rendering remains outside the budget. Other UI and CMD resize limits remain in [review resolution](review-resolution.md#remaining-limits).
