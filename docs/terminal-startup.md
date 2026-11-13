# Terminal startup and tab attachment.

A running shell and a populated terminal snapshot do not establish that the canvas painted. Startup checks require executed commands, painted output, visible pixels and automatic input focus.

## Canvas lifetime.

Do not force synchronous tab-list layout during loading. Reentrant `TabList.UpdateLayout()` reproduced blank startup despite populated shell output. Sidebar scrolling is queued after loading instead.

Each active, loaded terminal attachment owns a fresh CanvasControl. Suspension or unloading releases the canvas, retained raster and glyph layouts. Terminal frames, input and shell sessions remain on the surface. Repeated activation preserves the current canvas. A tab covered by the agent surface stays suspended until the terminal becomes visible.

Reusing a canvas after a tab switch exposed an extra Unloaded event and then stopped drawing. Win2D's [native load bookkeeping](https://github.com/microsoft/Win2D/blob/winappsdk/main/winrt/lib/xaml/BaseControl.h) explains a possible mechanism. The deployed native counter and upstream producer were not established. Fresh canvas ownership prevents old attachment bookkeeping from crossing into the next attachment. ReadyToDraw alone does not establish native loaded state.

## Focus and input.

Each pane requests focus from its own Loaded event, after its input exists. Deferred requests preserve an intentional user focus change and require the same active, loaded pane.

During startup the terminal remains enabled and focusable, while its text box is read-only until Running. Input handlers also gate shell input on Running. This prevents startup text from entering an invisible editing buffer and disappearing.

## Regression checks.

Run on an interactive Windows desktop from the repository root.

```powershell
pwsh -NoProfile -File scripts/check-startup.ps1 -Trials 5
```

The script builds an isolated startup executable and launches fresh Left and Top layout trials. It changes only in-memory preferences, does not save user settings or close user sessions, and exits after writing each report. A failure or timeout fails the script.

Checks cover executed and painted markers, nonblank rasters, automatic focus, rapid tab switching, splits, pane closure, hidden resource release, idle wakeup, synchronized-output deadlines, click and wheel selection clearing without process echo, hidden-cursor idle draws, covered-tab activation, startup read-only input and resize. The normal Windows check script runs one trial per layout and retained-raster parity.

## Known resize limit.

CMD can leave stale trailing prompt text after narrow-to-wide GUI resize. Executed command markers, native text, supplied cells and painted cells agreed in the observed case. The startup check requires a fresh marker at the beginning of an output row; it does not require unrelated trailing cells to clear. The artifact's upstream cause and baseline status remain unproven.
