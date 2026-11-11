# Base Windows Terminal parity

This baseline covers local interactive shells, tabs and panes, terminal rendering, selection and clipboard commands, scrollback, history search, zoom, and solid/acrylic appearance. It does not claim complete Windows Terminal compatibility.

| Area | Implementation | Verification |
| --- | --- | --- |
| ConPTY shells | Independent PowerShell and Command Prompt sessions; installed WSL distribution profiles; normal PowerShell profile loading | Native shell tests pass for launch, independent state, interrupts, resize, output draining and cleanup. WSL discovery and argument quoting pass; distro shell execution remains untested. |
| Tabs and panes | Profile menu, tab switching, nested splits, directional focus, close active pane/tab, await-all window shutdown | Live checks confirm tab creation, retained output after switching, split creation and full-height layout. |
| Rendering | ANSI/indexed/RGB colors, Unicode cells, styles, cursor shapes, alternate screen | Native and Win2D pixel tests pass; full-screen console fixture passes. |
| Scrollback | Wheel, scrollbar and keyboard navigation; retained history after stop/exit; input snaps to bottom | Native and real shell tests pass. Live wheel verification is in progress. |
| Selection and clipboard | Linear, word, line, rectangular and extended selection; autoscroll; native text formatting; paste framing; viewport pinning during output | Native and pixel tests pass. Live drag selection passes; clipboard verification is in progress. |
| Search | Previous/next history matches with wrap and exact-case option | Native search tests and live match/no-match checks pass. Case-insensitive matching folds ASCII only. |
| Zoom and settings | Per-pane zoom; saved font size, default profile, and solid/acrylic choice | Preference persistence tests and live zoom/background switching pass. |

## Known gaps

Application mouse reporting, IME composition/candidate positioning, a terminal UI Automation text provider, keyboard selection/mark mode, custom key bindings, advanced profile editing, extension APIs, and complete VT compatibility testing remain outside this baseline. Pane resizing and window/session layout restoration are not yet implemented.

Switching primary/alternate screens clears selection. Acrylic currently identifies default backgrounds by resolved RGB, so an explicit ANSI background with the same RGB is transparent too. The app uses Consolas with system font fallback.

## Comparison evidence

The user's Windows Terminal screenshots establish the compact tab-strip layout, profile dropdown, acrylic appearance, and normal PowerShell prompt behavior. Computer Use explicitly blocked Windows Terminal itself; its live behavior was not automated. AgentTerm's UI has been exercised directly for the flows listed above.

## References

- [Windows Terminal overview](https://learn.microsoft.com/en-us/windows/terminal/).
- [Actions and shortcuts](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/actions).
- [Selection behavior](https://learn.microsoft.com/en-us/windows/terminal/selection).
- [Panes](https://learn.microsoft.com/en-us/windows/terminal/panes).
- [Tips, zoom, and search](https://learn.microsoft.com/en-us/windows/terminal/tips-and-tricks).
