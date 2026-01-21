# Learnings

## 2026-01-21: Hiding CMD Windows on Windows App Launch

**Context:** When Agent Term launches on Windows, a CMD window briefly appears showing "mcp pipe" text.

**What we tried:**
- Investigated socket_proxy.rs - already had `CREATE_NO_WINDOW` for MCP server processes
- Found shell detection code (`where.exe`, `wsl.exe` commands) running without the hidden flag
- Found hyperlink opening in terminal_view.rs also spawning CMD without hiding

**Outcome:** Fixed by:
1. Added `#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]` to main.rs
2. Created `hidden_command()` helper function in agentterm_tools/src/lib.rs
3. Updated all shell detection calls to use `hidden_command()` with `CREATE_NO_WINDOW`
4. Fixed hyperlink opening in terminal_view.rs to use `CREATE_NO_WINDOW`

**Next time:**
- When spawning processes on Windows that don't need a visible console, always use `CREATE_NO_WINDOW` (0x08000000)
- Use `windows_subsystem = "windows"` attribute for GUI apps to prevent console window
- Consider creating a helper function for hidden process spawning to keep code DRY

## 2026-01-20: Command Palette Shortcut Changed to Alt+K

**Context:** Changing the command palette keyboard shortcut from `Cmd/Ctrl+P` to `Alt/Option+K`.

**What we tried:**
- Initially considered `Cmd/Ctrl+K`, but that conflicts with the terminal Clear action (`cmd-k` bound to `Clear` in Terminal context).

**Outcome:** Switched to `Alt+K` to avoid the conflict. Works on all platforms.

**Next time:**
- When choosing global shortcuts, check for conflicts with context-specific bindings (e.g., Terminal context).
- `alt-k` is a safe choice for global actions that shouldn't conflict with common terminal shortcuts.
