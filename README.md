# Agent Term

A Windows terminal built with WinUI 3, .NET 10, Win2D, Windows ConPTY, and libghostty-vt 1.0.0.

Each tab or pane owns an independent shell, terminal state, and scrollback. The profile menu discovers installed PowerShell versions, Command Prompt, and individual WSL distributions. PowerShell loads your normal shell profile. Closing a pane releases its shell; switching tabs preserves it. Stopped and exited sessions keep their history available until restarted or closed.

Tabs show the installed shell icon with padded pill headers. Mixed-shell tabs follow the first remaining pane for their icon and title order. Pane headers keep split and close controls without repeating the shell name.

Child shells receive `TERM=xterm-256color`, `COLORTERM=truecolor`, and `TERM_PROGRAM=AgentTerm`. The app removes inherited `NO_COLOR` from the child environment so launching it from automation does not suppress terminal colors. Other inherited variables remain available and the parent environment is unchanged.

The renderer supports ANSI, indexed, and RGB colors, Unicode cells, text styles, cursor shapes, and alternate screens. History, search, selection, and keyboard encoding use Ghostty's native APIs. Selection pins the viewport while output continues. Typing or pasting returns to live output.

Terminal defaults follow the Windows light or dark theme. Light mode uses dark text and cursor colors with a matching ANSI palette. Switching themes preserves shell output, history, selection, and application-defined RGB or OSC colors.

Colored cell backgrounds and selection join without grid seams at fractional cell widths or display scaling. Text retains normal antialiasing.

## Controls

| Action | Input |
| --- | --- |
| New default-profile tab | Plus button or Ctrl+Shift+T |
| New profile tab | Profile dropdown |
| Next / previous tab | Ctrl+Tab / Ctrl+Shift+Tab |
| Close active pane or tab | Ctrl+Shift+W |
| Split active pane | Ctrl+Shift+D, Alt+Shift++ or Alt+Shift+- |
| Focus adjacent pane | Alt+arrow |
| Resize panes | Drag a divider or Alt+Shift+arrow |
| Scroll history | Wheel, scrollbar, Ctrl+Shift+Up/Down or PageUp/PageDown |
| Oldest / latest output | Ctrl+Shift+Home / End |
| Select text | Left drag; double-click word; triple-click line |
| Extend / rectangular selection | Shift+click / Alt+drag |
| Copy selection and dismiss | Ctrl+C, Ctrl+Shift+C, Ctrl+Insert, Enter, or right-click |
| Interrupt without a selection | Ctrl+C |
| Paste | Ctrl+V, Ctrl+Shift+V, Shift+Insert, or right-click without a selection |
| Clear selection | Escape or single click |
| Search history | Ctrl+Shift+F; Enter next; Shift+Enter previous |
| Zoom | Ctrl+wheel, Ctrl++ / Ctrl+-; Ctrl+0 resets |
| Settings | Profile menu or Ctrl+, |

Drag beyond the top or bottom to scroll while selecting. Copy joins soft-wrapped text and preserves explicit line breaks. Paste honors bracketed paste mode and sanitizes terminal control characters. Clipboard errors do not stop the shell. Screen switches clear selection. Search wraps; case-insensitive search currently folds ASCII letters only.

Mouse-enabled terminal applications receive clicks, releases, motion, and wheel events through Ghostty's native encoder. Hold **Shift** to use local selection, copy/paste, and scrollback instead. **Ctrl+wheel** still zooms. Passive mouse movement does not jump the viewport to the latest output.

IME input uses a native WinUI text input host positioned at the terminal cursor. Composition text stays local until committed, and terminal shortcuts defer to the input method during composition. Automated checks cover text routing, composition state, and cursor suppression. Live ordinary text, Unicode, physical key input, and mouse selection pass. Real composition and candidate placement still require verification with an installed IME.

## Appearance and profiles

Settings opens in its own window from the profile menu or Ctrl+,. The terminal stays usable while settings is open. **Startup** selects the default shell. **Profiles > Defaults** sets the shared font family, font size, pane background, and acrylic opacity. Each discovered shell has its own **Appearance** page under Profiles. Changes stay in a draft across pages until **Save**. **Discard changes** restores saved values; closing the window discards unsaved edits.

Each profile inherits Defaults unless a setting is overridden. Choose **Use default** to restore inheritance for a field, or reset the profile to remove all its overrides. An explicit **Automatic** font uses the app's installed-font selection independently of the font chosen in Defaults. Saving updates existing panes for that profile and applies to future tabs and splits. Overrides for temporarily unavailable profiles remain saved.

Settings save the default profile, shared appearance defaults, and profile overrides to `%LOCALAPPDATA%\AgentTerm\settings.json`. The window uses Windows Mica with native caption controls and rounded tabs. Each terminal sits in a rounded card with quiet header actions, 4-DIP outer spacing and gutters, and a subtle native shadow. Every pane has a faint theme-aware boundary, light in dark mode and dark in light mode. Split panes add a restrained active-pane outline. Pane actions brighten on hover or keyboard focus. Settings uses the same rounded surfaces. Choose **Solid** for an opaque terminal or **Acrylic** for a transparent terminal surface over a separate in-app acrylic card. The **Background opacity** slider adjusts acrylic tint from 0% to 100%, independently for each profile or inherited from Defaults. It is disabled for Solid. Text, selection, and differently colored ANSI backgrounds remain opaque. Explicit ANSI backgrounds equal to the default background currently share its transparency behavior.

Automatic font selection prefers an installed CaskaydiaCove Nerd Font, then Cascadia Mono, then Consolas. Settings lists available terminal fonts. No fonts are downloaded or installed. WinUI's in-app acrylic blurs app content; it does not sample the desktop or the Mica system backdrop. Windows controls material fallback when transparency is unavailable.

New panes fade in over 200 ms. Closing panes fade out over 140 ms before removal, followed by a 190 ms expansion of the remaining panes. These transitions animate visual transforms without resizing ConPTY every animation frame. Divider dragging remains immediate. Turning off Windows animations disables these transitions.

Zoom applies to the current pane; Ctrl+0 restores that profile's font size. A saved font-size change updates panes whose resolved profile size changed. Saving another profile's settings preserves unrelated pane zoom. History is configured for 10 MiB and 10,000 physical lines; Ghostty retains whole pages, so these limits are approximate. WSL discovery is bounded and skips unavailable distributions. WSL itself is not installed by the app.

This is a base terminal experience, not complete Windows Terminal compatibility. Live IME compatibility validation, a terminal UI Automation text provider, advanced profile editing, and custom themes/key bindings remain open. See [the parity tracker](docs/windows-terminal-parity.md).

## Build and run

Use Windows x64, Windows 10 version 2004 or newer, a .NET 10 SDK, and PowerShell. Windows SDK/XAML tools restore through NuGet. Internet access is needed for the first build.

```powershell
./scripts/check.ps1
dotnet run --project src/AgentTerm.App -p:Platform=x64 -c Release
```

The check script downloads checksum-pinned Ghostty and Zig archives, builds the native DLL and app, and runs native VT/input/history/search tests, real shell integration tests, and Win2D pixel checks. Images are saved to `artifacts/renderer-smoke.png` and `artifacts/selection-smoke.png`. Generated files stay in ignored `.deps`, `artifacts`, `bin`, and `obj` folders. First native builds take several minutes.

The app is unpackaged and self-contained. Windows App SDK 1.8 is pinned. Packaging, signing, and additional architectures are future work.

## Code map

- `src/AgentTerm.App/MainWindow` owns the tab strip, profile menu, window backdrop, settings window lifetime, and window shutdown.
- `src/AgentTerm.App/SettingsWindow` owns settings navigation, draft edits, validation, and save/discard actions in a separate window.
- `src/AgentTerm.App/TerminalTab.cs` owns nested pane layout, resizing, transitions, and active-pane focus.
- `src/AgentTerm.App/TerminalPane` owns each card's material, header controls, and focus outline.
- `src/AgentTerm.App/MainPage` owns one terminal view, input, clipboard, search, and session presentation.
- `src/AgentTerm.App/TerminalSurface.cs` draws cells, selection, and cursors with shared font metrics.
- `src/AgentTerm.Terminal/TerminalSession.cs` serializes Ghostty state and owns one shell connection.
- `src/AgentTerm.Terminal/ConPtyConnection.cs` owns process creation, pipes, output draining, resize, and cleanup.
- `src/AgentTerm.Terminal/GhosttyTerminal.*` owns the pinned native ABI and terminal, render, selection, search, and input handles.
- `src/AgentTerm.Terminal/ShellProfile.cs` discovers installed shell profiles.
- `tests` contains real native, shell, and rendered-pixel checks.

`StopAsync` releases ConPTY while retaining Ghostty state for inspection. `DisposeAsync` also releases that terminal state. Hidden tabs pause drawing, not their shell output reader. Native Ghostty owns scrollback and tracked selection references; frame snapshots own their cell arrays and consumers must not mutate them.

## CI and verification

`scripts/check.ps1` validates Windows. `bash scripts/check-linux.sh` validates the portable wrapper on Linux x64 with .NET 10, Python 3, curl, tar, and xz. The workflow uses Boat for portable native checks and `windows-latest` for Windows builds and integration. Hosted CI requires the Boat GitHub App and a pushed branch; local Boat runs are separate validation.

## References and licenses

- [libghostty-vt](https://lib.ghostty.org/vt/).
- [Windows Terminal actions](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/actions).
- [Windows Terminal selection](https://learn.microsoft.com/en-us/windows/terminal/selection).
- [Windows Terminal profile appearance and inherited defaults](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/profile-appearance).
- [Boat CI](https://docs.boat.dev/ci).

Ghostty is MIT licensed. Keep its license and bundled dependency notices with redistributed native binaries. Generated Ghostty binaries are not committed.
