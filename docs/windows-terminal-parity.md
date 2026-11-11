# Base Windows Terminal parity

This baseline covers local interactive shells, tabs and panes, terminal rendering, selection and clipboard commands, scrollback, history search, zoom, and solid/acrylic appearance. It does not claim complete Windows Terminal compatibility.

| Area | Implementation | Verification |
| --- | --- | --- |
| ConPTY shells | Independent PowerShell and Command Prompt sessions; installed WSL distribution profiles; normal PowerShell profile loading | Native shell tests pass for launch, independent state, interrupts, resize, output draining and cleanup. WSL discovery and argument quoting pass; distro shell execution remains untested. |
| Tabs and panes | Profile menu, tab switching, nested splits, directional focus, mouse/keyboard resizing, close active pane/tab, await-all window shutdown | Live checks confirm tab creation, retained output after switching, full-height layout, both divider orientations, keyboard resizing, and nested collapse retaining the original session. |
| Rendering | ANSI/indexed/RGB colors, Unicode cells, styles, cursor shapes, alternate screen | Native and Win2D pixel tests pass; full-screen console fixture passes. |
| Scrollback | Wheel, scrollbar and keyboard navigation; retained history after stop/exit; input snaps to bottom | Native and real shell tests pass. Live wheel navigation reveals older lines; Ctrl+Shift+End returns to the latest output. |
| Selection and clipboard | Linear, word, line, rectangular and extended selection; autoscroll; native text formatting; paste framing; viewport pinning during output | Native and pixel tests pass. Live drag, Ctrl+C/Ctrl+Shift+C copy, Ctrl+V/Shift+Insert/right-click paste, and selection retention during output pass. |
| Search | Previous/next history matches with wrap and exact-case option | Native search tests and live match/no-match checks pass. Case-insensitive matching folds ASCII only. |
| Zoom and settings | Per-pane zoom; saved font family/size, default profile, and solid/acrylic choice | Preference persistence tests and live zoom/font/background switching pass. Automatic Nerd Font selection renders the user's colored PowerShell prompt without the previously observed missing glyph boxes. |

## Known gaps

Application mouse reporting, IME composition/candidate positioning, a terminal UI Automation text provider, keyboard selection/mark mode, custom key bindings, advanced profile editing, extension APIs, and complete VT compatibility testing remain outside this baseline. Window/session layout restoration is not yet implemented.

Switching primary/alternate screens clears selection. Acrylic currently identifies default backgrounds by resolved RGB, so an explicit ANSI background with the same RGB is transparent too. Settings offers installed terminal fonts; automatic selection prefers an installed CaskaydiaCove Nerd Font before Cascadia Mono and Consolas.

The card layout follows the user's later pane reference. The window uses Mica and Windows caption controls, with solid or in-app acrylic pane surfaces. In-app acrylic does not blur the desktop or sample Mica's external compositor content. This follows the [WinUI material model](https://learn.microsoft.com/en-us/windows/apps/develop/ui/in-app-acrylic).

Live checks observed intermediate entrance and exit frames, completed pane removal, and repeated split/close without stranded panes. Closing the window immediately after closing a pane exited the app and all six observed shell/conhost child processes. The reduced-motion branch was reviewed but not runtime-tested; no Windows animation setting was changed.

## Comparison evidence

The user's Windows Terminal screenshots establish the compact tab-strip layout, profile dropdown, acrylic appearance, and normal PowerShell prompt behavior. Computer Use explicitly blocked Windows Terminal itself; its live behavior was not automated. AgentTerm's UI has been exercised directly for the flows listed above.

Live clipboard checks compared copied text with the exact selected text and pasted distinct commands through each tested shortcut. Ctrl+C without selection interrupted a 30-second sleep. A selected history region remained at the same viewport offset through 20 timed output lines and remained copyable afterward. Solid/acrylic changes and per-pane zoom were visually verified. These checks supplement automated native, ConPTY, and rendered-pixel tests.

Settings now opens in a separate window with Startup, Appearance, and Profile defaults pages. Live checks confirmed terminal input while Settings was open, unchanged window bounds during page changes and validation errors, cross-page draft validation, Save without closing, Discard, unsaved draft removal on close, reuse after minimization, and settings-window cleanup when its terminal window closes.

## References

- [Windows Terminal overview](https://learn.microsoft.com/en-us/windows/terminal/).
- [Actions and shortcuts](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/actions).
- [Selection behavior](https://learn.microsoft.com/en-us/windows/terminal/selection).
- [Panes](https://learn.microsoft.com/en-us/windows/terminal/panes).
- [Tips, zoom, and search](https://learn.microsoft.com/en-us/windows/terminal/tips-and-tricks).
