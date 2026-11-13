# Native terminal performance fixture.

This WinUI window runs the production `TerminalSurface` and `TerminalSession` with Python output through real ConPTY. It uses the application's PerMonitorV2 manifest, FiraCode Nerd Font at 14 DIP, bottom anchoring, a transparent terminal background and an animated ProgressRing. It omits production tabs, agent timelines, task cards and window chrome. Its numbers therefore measure the terminal path only, not the full application.

Run every command below from the repository root in PowerShell 7. Use a fresh output directory for every build and trial.

## Build.

Use Windows with the .NET 10 SDK, Python 3, and PowerShell 7. Build the pinned native dependency once with `pwsh -NoProfile -File scripts/build-native.ps1`. The comparison script requires its `artifacts/native/win-x64/bin/ghostty-vt.dll` output.

The build script exports the baseline revision's terminal library and renderer with `git archive` into the output directory and builds both fixtures. It never changes the Git index or working tree.

```powershell
pwsh -NoProfile -File scripts/build-terminal-perf.ps1 -BaselineRevision 5606a46 -OutputDirectory artifacts/perf-comparison
```

The baseline side compiles `src/AgentTerm.Terminal`, `src/AgentTerm.Agents` and `src/AgentTerm.App/TerminalSurface.cs` exactly as they were at that revision. The current side compiles the working tree. `artifacts/perf-comparison/build-inputs.json` records the revision, file hashes, whether the working tree was dirty, and both executable paths. The fixture project accepts `-p:TerminalProject=<csproj>` and `-p:TerminalSurfaceSource=<file>` for other baselines.

To build only the current fixture:

```powershell
dotnet build tests/AgentTerm.Renderer.Perf/AgentTerm.Renderer.Perf.csproj -c Release --artifacts-path artifacts/perf-current
```

## Schedules.

- `--schedule demand` is the default and mirrors production `MainPage`. `TerminalSession.OutputAvailable` wakes the UI thread once per burst. The UI thread adds a one-shot `CompositionTarget.Rendering` handler. That handler removes itself and queues one refresh after the Rendering dispatch. After each refresh, a nonrepeating timer is armed from `TerminalSession.PendingFrameRefreshDelay` so a synchronized-output hold is recaptured without new output. No terminal work runs while the shell is silent.
- `--schedule timer --refresh-ms 33` polls `ReadFrame` on a fixed timer. This is the production schedule at 5606a46. Use it on both sides to compare code at matched cadence.
- `--schedule composition` refreshes after every composition frame. No released production version used it. It is kept only to reproduce intermediate results.

Demand scheduling uses reflection to find `OutputAvailable` and `PendingFrameRefreshDelay`, so the fixture still compiles against older libraries. Requesting `demand` from a library without them fails with a clear error.

## Patterns.

- `agent` updates counters and a spinner on a mostly stable 36-row screen.
- `dense` changes every payload character on every update.
- `cjk` writes 26 wide CJK glyphs per row that slide through 20,000 code points, so the distinct glyph count exceeds both one screen and the glyph cache.
- `held` starts a synchronized update (`ESC[?2026h`), writes a marker, never ends the update and then stays silent. The run fails unless the marker reaches a Canvas draw between 900 and 2,500 ms later. This is the regression check for the 1,000 ms hold timeout under demand scheduling.

## Run a trial.

```powershell
$python = python -c 'import sys; print(sys.executable)'
$fixture = 'artifacts/perf-comparison/current/bin/AgentTerm.Renderer.Perf/release_win-x64/AgentTerm.Renderer.Perf.exe'
& $fixture $python scripts/terminal-perf-workload.py artifacts/perf-trials/agent-1.json --hz 60 --pattern agent --schedule demand
& $fixture $python scripts/terminal-perf-workload.py artifacts/perf-trials/cjk-1.json --hz 60 --pattern cjk --schedule demand
& $fixture $python scripts/terminal-perf-workload.py artifacts/perf-trials/held-1.json --pattern held --schedule demand
```

Other options are `--seconds 12`, `--idle-seconds 2`, `--windowed`, `--capture-frames` and `--presentmon <executable>`. Keep the window visible and do not run other UI tests at the same time.

A timed run fails unless it sees actual Canvas draws, changed snapshots, visible rows, nonblank pixels, the expected update count and workload completion within 10% of the configured duration. When the renderer has a retained raster, the run also requires it to match a full redraw byte for byte. When it has a glyph cache, the run requires the cache never to exceed its capacity. Failed runs write `<output>.error.txt` and exit nonzero. Final pixels are saved as PNG.

## Metrics.

- `refreshDurations` is the elapsed time of `ReadFrame` plus `SetFrame`.
- `drawWorkDurations` is the elapsed time of the production Draw callback. It is null when the renderer has no `DrawCompleted` event. It excludes Win2D's later EndDraw, GPU work and display presentation.
- `frameWorkDurations` adds refresh work since the previous draw to the draw callback. It is UI-thread elapsed work per drawn update, not CPU time or display latency.
- `drawIntervals` is the time between Canvas callbacks. `heartbeatIntervals` is dispatcher delay measured by a 16 ms timer.
- `readFrames`, `renderingSubscriptions`, `outputNotifications` and `holdTimerFires` count scheduler work during the whole run.
- `idle` counts `ReadFrame` calls, Canvas draws, Rendering subscriptions and process CPU during `--idle-seconds` of shell silence after the workload ends. Demand scheduling should record zero reads.
- `glyphCache` reports the cache capacity, the largest observed layout count and the layouts created during the measured interval.
- `privateBytesStart` and `privateBytesEnd` are process private bytes around the measured interval.
- `cpuSeconds`, allocations, GC counts and GC pauses cover only the fixture process.
- `measurementStartQpcMs` and `measurementEndQpcMs` locate the workload for external ETW or PresentMon captures.

## Compare versions.

[`measure-terminal-perf.ps1`](../../scripts/measure-terminal-perf.ps1) alternates baseline and current trials, five each by default. It rejects failed workloads, mismatched geometry and different final raster hashes, and writes `results.json` and `summary.json`. Two comparisons answer different questions.

```powershell
$built = Get-Content artifacts/perf-comparison/build-inputs.json -Raw | ConvertFrom-Json
# Code change at matched cadence.
pwsh -NoProfile -File scripts/measure-terminal-perf.ps1 -BaselineExe $built.baselineExe -FixedExe $built.currentExe `
    -OutputDirectory artifacts/perf-matched-timer -Hz 60 -Pattern agent -BaselineSchedule timer -FixedSchedule timer
# Shipped behavior. Code change plus demand scheduling.
pwsh -NoProfile -File scripts/measure-terminal-perf.ps1 -BaselineExe $built.baselineExe -FixedExe $built.currentExe `
    -OutputDirectory artifacts/perf-shipped -Hz 60 -Pattern agent -BaselineSchedule timer -FixedSchedule demand
```

`-RequireFrameBudget` also fails unless every current trial has instrumentation and p95 frame work below 16.6 ms. That gate does not establish 60 presented frames per second.

## Raster parity.

```powershell
& $fixture $python scripts/terminal-perf-workload.py artifacts/perf-trials/transitions.json --verify-transitions
```

This mode draws a scripted sequence through the onscreen Canvas and compares the retained raster with a full redraw after every step. Steps cover sparse edits, erase, wide and combining glyphs, a partial combining edit, selection create, move, extend during output and clear, cursor-only movement, theme, colored and transparent backgrounds, suspend and resume, history scrolling with output while scrolled, enough distinct CJK screens to overflow the glyph cache, shrinking a populated cache without a font reset, font zoom, resize, clear and reverse video. It fails on the first byte that differs and saves both images.

## Presentation evidence.

PresentMon Console is available from the `Intel.PresentMon.Console` WinGet package. `--presentmon` starts a PID-scoped collector without elevation. On the development machine the live trace failed with access denied because the token lacks ETW privileges. Displayed 60 FPS is therefore unverified.

`--capture-frames` records compositor-delivered frames of the fixture window through Windows.Graphics.Capture and hashes a counter region and a static label region. It writes `<output>.capture.ndjson` and fails if capture reports an error or supplies no frames. Capture can miss or coalesce frames, so its rate is a lower bound and cannot establish physical scanout. Do not combine it with timing comparisons.
