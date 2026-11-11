# Base Windows Terminal parity

This baseline covers local interactive shells, tabs and panes, terminal rendering, selection and clipboard commands, scrollback, history search, zoom, and solid/acrylic appearance. It does not claim complete Windows Terminal compatibility.

| Area | Implementation | Verification |
| --- | --- | --- |
| ConPTY shells | Independent PowerShell and Command Prompt sessions; installed WSL distribution profiles; normal PowerShell profile loading | Native shell tests pass for launch, independent state, interrupts, resize, output draining and cleanup. WSL discovery and argument quoting pass; distro shell execution remains untested. |
| Tabs and panes | Profile menu, tab switching, nested splits, directional focus, mouse/keyboard resizing, close active pane/tab, await-all window shutdown | Live checks confirm tab creation, retained output after switching, full-height layout, both divider orientations, keyboard resizing, and nested collapse retaining the original session. |
| Rendering | ANSI/indexed/RGB colors, Unicode cells, styles, cursor shapes, alternate screen; theme-aware default text, cursor and ANSI palette | Native and Win2D pixel tests pass; full-screen console fixture passes. Windows and Boat theme checks cover live changes, preserved content/history/selection, application RGB/OSC overrides, and color-scheme queries. Light single and split panes were visually checked. |
| Application mouse | Native mouse-mode encoding for buttons, releases, motion, wheel, and SGR pixels; Shift local override; Ctrl-wheel zoom | Windows and Boat protocol checks pass for tracking modes, coordinates, modifiers, resize and DPI. A real ConPTY fixture verifies press/release/wheel, Shift bypass, and tracking disable. Full mouse-enabled TUI interaction through the WinUI surface remains unverified. |
| Scrollback | Wheel, scrollbar and keyboard navigation; retained history after stop/exit; input snaps to bottom | Native and real shell tests pass. Live wheel navigation reveals older lines; Ctrl+Shift+End returns to the latest output. |
| Selection and clipboard | Linear, word, line, rectangular and extended selection; autoscroll; native text formatting; paste framing; viewport pinning during output | Native and pixel tests pass. Live drag, Ctrl+C/Ctrl+Shift+C copy, Ctrl+V/Shift+Insert/right-click paste, and selection retention during output pass. |
| Search | Previous/next history matches with wrap and exact-case option | Native search tests and live match/no-match checks pass. Case-insensitive matching folds ASCII only. |
| Zoom and settings | Per-pane zoom; shared appearance defaults with independent per-shell font family/size and solid/acrylic overrides | Windows and Boat tests cover inheritance, persistence, explicit Automatic/Solid values, legacy settings, and unavailable profiles. Live checks confirm independent appearance updates in PowerShell and Command Prompt panes. |

## Known gaps

IME composition/candidate positioning, a terminal UI Automation text provider, keyboard selection/mark mode, custom key bindings, advanced profile editing, extension APIs, and complete VT compatibility testing remain outside this baseline. Window/session layout restoration is not yet implemented.

Native tab dragging and reordering are enabled, but automated drag checks did not change tab order. The cause remains unresolved. Tab switching and creation pass; drag reordering is not verified.

The next core priorities are IME text entry and candidate positioning, accessible terminal text, then keyboard mark mode and configurable actions. These affect input and access before broader profile and session-management features.

Switching primary/alternate screens clears selection. Acrylic currently identifies default backgrounds by resolved RGB, so an explicit ANSI background with the same RGB is transparent too. Settings offers installed terminal fonts; automatic selection prefers an installed CaskaydiaCove Nerd Font before Cascadia Mono and Consolas.

The card layout follows the user's later pane reference. The window uses Mica and Windows caption controls, with solid or in-app acrylic pane surfaces. In-app acrylic does not blur the desktop or sample Mica's external compositor content. This follows the [WinUI material model](https://learn.microsoft.com/en-us/windows/apps/develop/ui/in-app-acrylic).

Live checks observed intermediate entrance and exit frames, completed pane removal, and repeated split/close without stranded panes. Closing the window immediately after closing a pane exited the app and all six observed shell/conhost child processes. The reduced-motion branch was reviewed but not runtime-tested; no Windows animation setting was changed.

## Comparison evidence

The user's Windows Terminal screenshots establish the compact tab-strip layout, profile dropdown, acrylic appearance, and normal PowerShell prompt behavior. Computer Use explicitly blocked Windows Terminal itself; its live behavior was not automated. AgentTerm's UI has been exercised directly for the flows listed above.

Live clipboard checks compared copied text with the exact selected text and pasted distinct commands through each tested shortcut. Ctrl+C without selection interrupted a 30-second sleep. A selected history region remained at the same viewport offset through 20 timed output lines and remained copyable afterward. Solid/acrylic changes and per-pane zoom were visually verified. These checks supplement automated native, ConPTY, and rendered-pixel tests.

Settings opens in a separate window with Startup, Profile defaults, and each shell's Appearance page. Live checks confirmed terminal input while Settings was open, unchanged window bounds during page changes and validation errors, cross-page draft validation, Save without closing, Discard, unsaved draft removal on close, reuse after minimization, and settings-window cleanup when its terminal window closes.

Per-profile live checks confirmed that Command Prompt overrides update existing and newly split Command Prompt panes while PowerShell panes remain unchanged. Saving an unrelated profile preserved a zoomed PowerShell pane. Changing Defaults updated inheriting panes while keeping explicit overrides; Reset restored inheritance for both panes of the same profile. The original settings file was restored and its hash checked after testing.

A Command Prompt font-family override changed its live pane without changing the adjacent PowerShell font. Saving an invalid profile draft from another profile now navigates to the invalid field and keeps the error visible on the first Save. Independent 6.1-terra-high UX review accepted the main and minimum-width settings layouts and prompted full font-value tooltips and visible inherited values.

The chrome refinement keeps 4-DIP gutters, removes the single-pane border, and uses native shadows with quieter pane actions and rounded pill tabs. Live checks confirmed hover emphasis, keyboard focus and activation, and profile opacity Save/Discard/inheritance. Solid mode disables the opacity slider. Original preferences were restored and their hash verified. Independent 6.1-terra-high visual review found no blocker in the supplied light/dark captures. An independent 6.1-sol-high judge rated the implemented chrome/settings/theme visuals 9.1/10. This is a subjective screenshot assessment, not a claim of full terminal parity or accessibility conformance.

## References

- [Windows Terminal overview](https://learn.microsoft.com/en-us/windows/terminal/).
- [Actions and shortcuts](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/actions).
- [Selection behavior](https://learn.microsoft.com/en-us/windows/terminal/selection).
- [Panes](https://learn.microsoft.com/en-us/windows/terminal/panes).
- [Tips, zoom, and search](https://learn.microsoft.com/en-us/windows/terminal/tips-and-tricks).
