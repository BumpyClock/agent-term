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

## 2026-01-23: GPUI Dialog Footer API

**Context:** Implementing dialogs with custom footer buttons in GPUI.

**What we tried:** Used dialog `.footer(|footer, _window, _cx| ...)` closure with 3 args.

**Outcome:** The dialog footer API takes 4 arguments: `|_ok, cancel, window, cx|` and returns a `Vec<AnyElement>`. The `cancel` parameter is a function that creates the Cancel button.

**Next time:** Dialog footer callbacks take `(_ok, cancel, window, cx)` and return `Vec<impl IntoElement>`.

## 2026-01-23: Custom Tools in MCP vs Settings

**Context:** Implementing custom tool management UI.

**What we tried:** Custom tools are stored in both `AppSettings.custom_tools` and MCP config (`UserConfig.tools`).

**Outcome:** The settings.custom_tools field exists for persistence, while MCP config has its own ToolDef structure. For the UI, working with settings.custom_tools is simpler. The two stores should stay in sync.

**Next time:** When managing custom tools, decide which is the source of truth and ensure both stores are kept in sync. AppSettings is simpler for UI binding.

## 2026-01-23: Session Status Tracking Architecture

**Context:** Adding visual status indicators for terminal sessions.

**What we tried:** The plan called for subscribing to terminal events and updating SessionStatus.

**Outcome:** SessionStatus enum already exists (Running, Waiting, Idle, Error, Starting). Status dots can display based on session.status. Full event-driven updates require subscribing to Terminal events and updating the session store - this needs careful integration with async terminal spawning.

**Next time:** Status tracking requires:
1. Initial status = Starting when session created
2. Subscribe to Terminal events (Wakeup -> Running, CloseTerminal -> Idle/Error)
3. Update session status in store, call cx.notify()

## 2026-01-23: GPUI Drag and Drop Implementation

**Context:** Implementing drag-and-drop reordering for session tabs in sidebar.

**What we tried:** GPUI has no built-in drag handlers like React's @dnd-kit, so implemented manual drag state tracking.

**Outcome:** Implemented using:
- `DraggingSession` struct and `DropTarget` enum for state tracking
- Mouse event handlers: `on_mouse_down`, `on_mouse_move`, `on_mouse_up`, `on_mouse_up_out`
- Visual feedback via conditional border styling when hovering over drop targets
- Uses `cx.listener()` pattern for all event handlers that update state
- `on_mouse_up_out` is critical - handles case when mouse is released outside the row to cancel drag

**Next time:**
- For drag-and-drop in GPUI, use manual state tracking with DraggingX struct and DropTarget enum
- Always include `on_mouse_up_out` handler to cancel stuck drags
- Use `.when()` for conditional styling based on drop target state
- Session store has `move_session()` and `reorder_sessions_in_workspace()` methods for persistence

## 2026-01-23: GPUI Mouse Event Position Bug

**Context:** Drag and drop reordering wasn't working - drop targets were never correctly identified.

**What we tried:** Originally used `event.position.y` in `on_mouse_move` and compared against a fixed offset (12px) to determine if cursor was in top/bottom half of row.

**Outcome:** `event.position` in GPUI mouse events returns window-absolute coordinates, not element-relative coordinates. Comparing absolute Y against a fixed 12px offset fails for any row not at the top of the window.

**Fix:** Simplified logic to just use the fact that each row's handler knows which row it's attached to. When hovering over a row (and dragging), that row becomes the drop target - no position math needed.

**Next time:**
- GPUI `MouseMoveEvent.position` is window-absolute, not element-relative
- Don't try to calculate "which half of an element" using absolute coordinates
- For drag-and-drop, simply use the fact that each element's handler knows its own identity
- If precise element-relative hit testing is needed, use GPUI's bounds APIs or store element bounds in state

## 2026-01-25: Sidebar Drag Preview Rendering

**Context:** Adding a drag ghost and inline drop preview for sidebar session reordering.

**What we tried:**
- Track drag state at the window-level mouse move hook instead of per-row handlers
- Cache sidebar and row bounds using `ElementExt::on_prepaint`
- Render a floating ghost row using absolute positioning and cached bounds
- Insert an inline placeholder row at the computed drop target

**Outcome:** Smooth drag feedback with both a cursor-following ghost and a static inline preview. Drop target selection uses cached bounds and is resilient to hover gaps.

**Next time:**
- Use window-level mouse events for fluid drag tracking
- Store bounds via `on_prepaint` for accurate hit testing
- Keep drag previews non-interactive and render them as the last child for layering
