# Learnings

## Windows
**CMD window flashing on launch** (2026-01-21)
- Use `windows_subsystem = "windows"` for GUI apps
- Spawn hidden processes with `CREATE_NO_WINDOW` (0x08000000)
- Helper pattern: `hidden_command()` in `agentterm_tools/src/lib.rs`

## Command Palette
**Shortcut conflicts** (2026-01-20, 2026-01-22)
- `cmd-k` conflicts with terminal Clear → use `alt-k` for global shortcuts
- Bind `ToggleCommandPalette` action; use platform-specific strings (`cmd-p` or `ctrl-p`, not `cmd/ctrl+p`)
**Workspace restore** (2026-01-22)
- Use `set_active_session_id`, not just `active_session_id`
- Check cross-window layout before restoring

## Terminal/Scrolling
**Alternate scroll** (2026-01-22)
- `ALTERNATE_SCROLL` only meaningful with `ALT_SCREEN` set
- Render scrollback using `display_offset` to convert grid → viewport coords

## GPUI
**Dialog footer** (2026-01-23): `|_ok, cancel, window, cx| -> Vec<impl IntoElement>`

**Drag-and-drop** (2026-01-23, 2026-01-25)
- Manual state: `DraggingSession` struct + `DropTarget` enum
- Events: `on_mouse_down`, `on_mouse_move`, `on_mouse_up`, `on_mouse_up_out` (critical for cancel)
- `cx.listener()` for state updates
- `event.position` is window-absolute, not element-relative
- Use `on_prepaint` to cache bounds for hit testing
- Snapshot row bounds at drag start to avoid oscillation from inline placeholders
- Session store: `move_session()`, `reorder_sessions_in_workspace()`

**Paint recursion** (2026-02-10)
- Prefer "no animation" over near-zero duration for reduced-motion
- Avoid static `.id("...")` for components with multiple simultaneous instances

## Theming/Animation
**Fluent tokens + reduced motion** (2026-02-10)
- Centralize in `ThemeMotion`, `ThemeElevation`, `ThemeMaterial` domains
- Wire app-level `reduce_motion` setting → `WindowShell` → component context
**Easing limits** (2026-02-10)
- Keep GPUI easing Y control points within `[0, 1]` unless engine supports overshoot
**Asset loading** (2026-02-10)
- Add filename alias/fallback when component expects root paths but app embeds under subfolders (`noise/{name}`)
- Check icon paths in `icon.rs` vs app assets after gpui-component upgrades

## Architecture
**Custom tools sync** (2026-01-23): `AppSettings.custom_tools` (persistence) ↔ `UserConfig.tools` (MCP)

**Session status** (2026-01-23)
- `SessionStatus` enum: Running, Waiting, Idle, Error, Starting
- Pattern: create=Starting → subscribe Terminal events (Wakeup→Running, CloseTerminal→Idle/Error) → update store + `cx.notify()`
