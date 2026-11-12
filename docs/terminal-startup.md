# Terminal startup and tab attachment.

The blank-window regression can leave a running shell behind an empty terminal surface. A successful shell command or a populated terminal snapshot does not establish that the canvas painted.

## Reproduced trigger.

An isolated experiment restored only synchronous `TabList.UpdateLayout()` inside `ApplyTabPosition(Left)`, reached from `Tabs_Loaded`. Five of five fresh launches then had populated shell output but no initialized canvas or painted raster. Removing that call painted in five of five launches with the same probe and settings. The sidebar now queues `ScrollIntoView` after loading instead of forcing layout during loading.

This establishes reentrant layout as a sufficient trigger. The native Win2D initialization mechanism was not inspected. Raw local evidence was saved under `artifacts/startup-sol-high/`.

## Canvas ownership across tab switches.

A later Top-layout test reproduced blank output after reusing an initialized canvas. Managed event tracing observed one `Loaded`, two `Unloaded` events and another `Loaded` on the same canvas. The shell continued executing commands and supplied a populated frame, but neither normal nor diagnostic invalidation produced a draw.

Win2D's [native load bookkeeping](https://github.com/microsoft/Win2D/blob/winappsdk/main/winrt/lib/xaml/BaseControl.h) and [render callback](https://github.com/microsoft/Win2D/blob/winappsdk/main/winrt/lib/xaml/CanvasControl.cpp) explain how an excess unload can leave native rendering disabled. This is an inference from managed traces and upstream source, not a direct read of the deployed native counter. `ReadyToDraw` reports device readiness and did not establish native loaded state in the failing test. The upstream producer of the extra unload is not established.

The pane now creates a canvas for each active, loaded attachment and releases it on suspension or unloading. Terminal frames, input and shell sessions survive on the surface. Repeated activation keeps the current canvas. No failure detector, retry timer or canvas-recovery loop is used.

Five fresh Top trials and five Left trials passed the full startup sequence after this change. Every owned process exited, and the user's settings file hash stayed unchanged. The recorded matrix is local under `artifacts/review-sol/final-startup-matrix.json`.

## Focus ownership.

Automatic focus checks exposed two independent ordering defects. The page allowed `FocusTerminal()` during `Starting` while disabling the terminal in that state, so the input could not receive focus. Splitting a pane also requested focus before the new page and input were loaded. The terminal now remains enabled during startup while its input handlers reject shell input until `Running`. Each pane requests focus from its own `Loaded` event. Deferred requests still preserve a user focus change and require the same active, loaded pane.

## Regression command.

Run on an interactive Windows desktop from the repository root.

```powershell
pwsh -NoProfile -File scripts/check-startup.ps1 -Trials 5
```

The script builds an isolated startup executable and launches fresh Left and Top layout trials. It changes only the process's in-memory tab preference. It does not save user settings or close user sessions.

The checks require shell command execution, a painted marker, nonblank raster pixels and automatic input focus without an explicit focus repair. They also exercise rapid tab creation and switching, splits and pane closure, hidden raster release, idle subscription drain, output after idle, resize, and a synchronized update that must paint after its deadline with no further output.

Each process writes its report and exits. A nonzero exit or timeout fails the script. The normal Windows check script runs one trial per layout and the retained-raster parity fixture.

## Resize qualification limit.

A longer working-directory prompt exposed a CMD reflow artifact after a narrow-to-wide window resize. The new command executed and its output marker appeared at the beginning of a row, followed by stale trailing prompt text. Native `ReadText`, captured cells, supplied cells and painted cells agreed. No lost command or raster mismatch was observed. A bounded console probe passed six resize stages against both the baseline and current terminal libraries, so the GUI artifact's upstream cause and baseline status remain unproven.

The startup check requires a fresh marker at the beginning of an output row in native text, the captured frame and the painted frame. It does not assert that CMD reflow clears unrelated trailing cells. The trailing-text artifact remains a limitation, not a claimed fix.
