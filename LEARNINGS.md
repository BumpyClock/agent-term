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

## 2026-01-22: Command Palette Shortcut Not Opening

**Context:** Changing the command palette shortcut to Cmd/Ctrl+P by editing the gpui-component defaults.

**What we tried:** Updated `vendor/gpui-component/crates/ui/src/command_palette/types.rs` default shortcut.

**Outcome:** The palette still did not open because the app overrides the shortcut and does not handle the `command_palette::Open` action; invalid shortcut strings (like `cmd/ctrl+p`) are skipped by the parser.

**Next time:** Bind `ToggleCommandPalette` in the app (or handle `command_palette::Open`) and use platform-specific shortcut strings (`cmd-p` or `ctrl-p`).

## 2026-01-22: Command Palette Workspace Restore No-Op

**Context:** Selecting a workspace from the command palette did nothing, even though users expected the workspace tabs to restore or switch.

**What we tried:** Traced the selection handler and found it only set `active_session_id` without `set_active_session_id`, and it only looked at tabs already visible in the current window.

**Outcome:** Added a workspace selection helper that switches to an existing window containing the workspace (preferred), otherwise restores the workspace tabs in the current window and activates the first tab.

**Next time:** Ensure command palette selections use `set_active_session_id` and consider cross-window layout state before restoring.

## 2026-01-22: TUI Scroll Shows Blank Screen

**Context:** Scrolling in Claude Code (TUI) showed a blank screen instead of in-app scrolling.

**What we tried:** Instrumented scroll handling in `gpui_term::terminal` to log mode flags and branch selection.

**Outcome:** `ALTERNATE_SCROLL` is enabled by default, so using it without `ALT_SCREEN` made the scroll wheel send arrow keys in normal terminals. Reverted to only apply alternate scroll when `ALT_SCREEN` is active. The blank screen during scrollback was due to rendering grid line coordinates directly; scrolled-back lines have negative line indices and were being painted outside the viewport. Fix by offsetting grid lines by `display_offset` for rendering.

**Next time:** Treat `ALTERNATE_SCROLL` as meaningful only when `ALT_SCREEN` is set; it defaults to on otherwise. When rendering scrollback, always convert grid line coordinates into viewport coordinates using `display_offset`.
