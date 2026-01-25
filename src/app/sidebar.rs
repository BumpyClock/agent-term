//! Sidebar rendering and interaction methods.

use agentterm_session::{SessionRecord, SessionStatus, SessionTool, DEFAULT_WORKSPACE_ID};
use agentterm_tools::ShellType;
use gpui::{
    div, point, prelude::*, px, AnyElement, AnyWindowHandle, ClickEvent, Context, Corner, Div,
    Entity, IntoElement, MouseButton, MouseDownEvent, MouseMoveEvent, MouseUpEvent, ParentElement,
    Styled, Window,
};

use super::window_registry::WindowRegistry;
use gpui_component::{ElementExt, SidebarShell, TITLE_BAR_HEIGHT};

use super::constants::{SIDEBAR_MAX_WIDTH, SIDEBAR_MIN_WIDTH};

use crate::icons::{icon_from_string, Icon, IconName, IconSize};
use crate::ui::{
    ActiveTheme, Button, ButtonVariants, ContextMenuExt, DropdownMenu, PopupMenu, PopupMenuItem,
    WorkspaceItem,
};

use super::actions::*;
use super::constants::*;
use super::state::{AgentTermApp, DraggingSession, DropTarget};
use crate::updater::{UpdateManager, UpdateState};

#[derive(Clone, Copy)]
enum CheckForUpdatesAction {
    Check,
    Download,
    Install,
    Retry,
}

fn truncate_string(s: &str, max_len: usize) -> String {
    if s.len() <= max_len {
        s.to_string()
    } else {
        format!("{}...", &s[..max_len])
    }
}

impl AgentTermApp {
    pub fn render_sidebar_shell(
        &self,
        _window: &Window,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let entity = cx.entity();
        let entity_for_end = entity.clone();

        SidebarShell::left(px(self.sidebar_width))
            .min_width(px(SIDEBAR_MIN_WIDTH))
            .max_width(px(SIDEBAR_MAX_WIDTH))
            .inset(px(SIDEBAR_INSET))
            .blur_enabled(self.settings.blur_enabled)
            .on_resize_start(move |width, x, _window, cx| {
                entity.update(cx, |this, _cx| {
                    this.resizing_sidebar = true;
                    this.resize_start_x = x;
                    this.resize_start_width = width / px(1.0);
                });
            })
            .on_resize_end(move |_window, cx| {
                entity_for_end.update(cx, |this, _cx| {
                    this.resizing_sidebar = false;
                });
            })
            .child(self.render_sidebar_content(cx))
    }

    /// Handler for mouse move events during sidebar resize.
    /// Should be attached at the root level to capture moves outside the resizer.
    pub fn update_sidebar_resize(
        &mut self,
        event: &MouseMoveEvent,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let mut should_notify = false;
        if self.resizing_sidebar {
            let delta_x = event.position.x - self.resize_start_x;
            let new_width = (self.resize_start_width + delta_x / px(1.0))
                .clamp(SIDEBAR_MIN_WIDTH, SIDEBAR_MAX_WIDTH);

            self.sidebar_width = new_width;
            should_notify = true;
        }

        if self.update_drag_state(event) {
            should_notify = true;
        }

        if should_notify {
            cx.notify();
        }
    }

    /// Handler for mouse up events to stop sidebar resize.
    /// Should be attached at the root level.
    pub fn stop_sidebar_resize(
        &mut self,
        _event: &MouseUpEvent,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.resizing_sidebar = false;
        let mut should_notify = false;

        if let Some(dragging) = self.dragging_session.take() {
            let drop_target = self.drop_target.take();
            if dragging.has_moved {
                if let Some(target) = drop_target {
                    self.complete_drag_drop(dragging, target, cx);
                    return;
                }
            }
            should_notify = true;
        } else if self.drop_target.take().is_some() {
            should_notify = true;
        }

        if should_notify {
            cx.notify();
        }
    }

    fn update_drag_state(&mut self, event: &MouseMoveEvent) -> bool {
        let (dragging_session_id, has_moved, position_changed, moved_changed) = {
            let Some(dragging) = self.dragging_session.as_mut() else {
                return false;
            };

            let previous_position = dragging.mouse_position;
            let previous_has_moved = dragging.has_moved;
            let drag_position = event.position;
            dragging.mouse_position = drag_position;

            let delta_x = (drag_position.x - dragging.start_position.x).abs();
            let delta_y = (drag_position.y - dragging.start_position.y).abs();
            let drag_threshold = px(3.0);
            if !dragging.has_moved && (delta_x > drag_threshold || delta_y > drag_threshold) {
                dragging.has_moved = true;
            }

            let position_changed = dragging.has_moved && drag_position != previous_position;
            let moved_changed = dragging.has_moved != previous_has_moved;
            (
                dragging.session_id.clone(),
                dragging.has_moved,
                position_changed,
                moved_changed,
            )
        };

        let mut should_notify = position_changed || moved_changed;
        let new_drop_target = if has_moved {
            let mut target = None;
            for (session_id, bounds) in &self.session_row_bounds {
                if session_id == &dragging_session_id {
                    continue;
                }
                let Some(session) = self
                    .sessions
                    .iter()
                    .find(|session| &session.id == session_id)
                else {
                    continue;
                };
                if self.is_workspace_collapsed(&session.workspace_id) {
                    continue;
                }
                if !self.is_session_visible(&session.id) {
                    continue;
                }
                if bounds.contains(&event.position) {
                    let midpoint = bounds.origin.y + bounds.size.height / 2.0;
                    if event.position.y < midpoint {
                        target = Some(DropTarget::BeforeSession {
                            session_id: session.id.clone(),
                            workspace_id: session.workspace_id.clone(),
                        });
                    } else {
                        target = Some(DropTarget::AfterSession {
                            session_id: session.id.clone(),
                            workspace_id: session.workspace_id.clone(),
                        });
                    }
                    break;
                }
            }
            target
        } else {
            None
        };

        if self.drop_target != new_drop_target {
            self.drop_target = new_drop_target;
            should_notify = true;
        }

        should_notify
    }

    pub fn render_sidebar_content(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let top_offset_for_title_bar = TITLE_BAR_HEIGHT - px(SIDEBAR_INSET);
        // On macOS we draw into the title bar so the sidebar can visually reach the top, but we
        // still need a bit of space so the header doesn't sit beneath the traffic lights.
        let top_offset = if cfg!(target_os = "macos") {
            px(20.0)
        } else {
            top_offset_for_title_bar
        };
        let view = cx.entity();
        let drag_preview = self.render_drag_preview(cx);
        div()
            .id("sidebar-content")
            .size_full()
            .flex()
            .flex_col()
            .relative()
            .on_prepaint(move |bounds, _, cx| {
                view.update(cx, |this, _| {
                    this.sidebar_bounds = Some(bounds);
                });
            })
            .child(div().h(top_offset).flex_shrink_0())
            .child(self.render_sidebar_header(cx))
            .child(self.render_add_workspace(cx))
            .child(self.render_workspaces_list(cx))
            .child(self.render_sidebar_footer(cx))
            .when_some(drag_preview, |this, preview| this.child(preview))
    }

    pub fn render_sidebar_footer(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let update_state = self.update_manager.read(cx).state().clone();
        let border_color = cx.theme().border.alpha(BORDER_SOFT_ALPHA);
        let muted_fg = cx.theme().muted_foreground;
        let foreground = cx.theme().foreground;
        let accent = cx.theme().accent;
        let success = cx.theme().success;
        let danger = cx.theme().danger;

        let current_version = UpdateManager::current_version();
        let version_text = format!("v{}", current_version);

        let (icon_element, status_text, is_clickable, click_action, has_notes) = match &update_state
        {
            UpdateState::Idle => (
                Icon::new(IconName::RefreshCw)
                    .size(IconSize::Small)
                    .color(muted_fg)
                    .into_any_element(),
                version_text,
                true,
                Some(CheckForUpdatesAction::Check),
                false,
            ),
            UpdateState::Checking => (
                Icon::new(IconName::RefreshCw)
                    .size(IconSize::Small)
                    .color(accent)
                    .into_any_element(),
                "Checking...".to_string(),
                false,
                None,
                false,
            ),
            UpdateState::UpToDate => (
                Icon::new(IconName::Check)
                    .size(IconSize::Small)
                    .color(success)
                    .into_any_element(),
                format!("{} · Up to date", version_text),
                false,
                None,
                false,
            ),
            UpdateState::Available(info) => (
                Icon::new(IconName::Download)
                    .size(IconSize::Small)
                    .color(accent)
                    .into_any_element(),
                format!("v{} available", info.version),
                true,
                Some(CheckForUpdatesAction::Download),
                true,
            ),
            UpdateState::Downloading { progress, .. } => (
                Icon::new(IconName::RefreshCw)
                    .size(IconSize::Small)
                    .color(accent)
                    .into_any_element(),
                format!("Downloading... {:.0}%", progress * 100.0),
                false,
                None,
                true,
            ),
            UpdateState::ReadyToInstall(_) => (
                Icon::new(IconName::RefreshCw)
                    .size(IconSize::Small)
                    .color(success)
                    .into_any_element(),
                "Restart to update".to_string(),
                true,
                Some(CheckForUpdatesAction::Install),
                true,
            ),
            UpdateState::Installing(_) => (
                Icon::new(IconName::RefreshCw)
                    .size(IconSize::Small)
                    .color(accent)
                    .into_any_element(),
                "Installing...".to_string(),
                false,
                None,
                true,
            ),
            UpdateState::Error(msg) => (
                Icon::new(IconName::X)
                    .size(IconSize::Small)
                    .color(danger)
                    .into_any_element(),
                format!("Error: {}", truncate_string(msg, 20)),
                true,
                Some(CheckForUpdatesAction::Retry),
                false,
            ),
        };

        let progress_bar = if let UpdateState::Downloading { progress, .. } = &update_state {
            let progress_width = px(progress * 200.0);
            Some(
                div()
                    .absolute()
                    .left_0()
                    .bottom_0()
                    .h(px(2.0))
                    .w(progress_width)
                    .bg(accent),
            )
        } else {
            None
        };

        let notes_button = if has_notes {
            Some(
                div()
                    .id("view-release-notes")
                    .text_xs()
                    .text_color(accent)
                    .cursor_pointer()
                    .hover(|s| s.underline())
                    .on_click(cx.listener(|this, _: &ClickEvent, window, cx| {
                        this.open_release_notes(window, cx);
                    }))
                    .child("Notes"),
            )
        } else {
            None
        };

        let footer = div()
            .id("sidebar-footer")
            .relative()
            .h(px(40.0))
            .flex_shrink_0()
            .border_t_1()
            .border_color(border_color)
            .px(px(12.0))
            .flex()
            .items_center()
            .gap(px(8.0))
            .text_sm()
            .child(icon_element)
            .child(
                div()
                    .flex_1()
                    .truncate()
                    .text_color(foreground)
                    .child(status_text),
            )
            .children(notes_button);

        let footer = if is_clickable {
            let hover_bg = cx.theme().list_hover;
            footer
                .cursor_pointer()
                .hover(move |s| s.bg(hover_bg))
                .on_click(cx.listener(move |this, _: &ClickEvent, window, cx| {
                    if let Some(action) = &click_action {
                        match action {
                            CheckForUpdatesAction::Check | CheckForUpdatesAction::Retry => {
                                this.handle_check_for_updates(&CheckForUpdates, window, cx);
                            }
                            CheckForUpdatesAction::Download => {
                                this.handle_download_update(&DownloadUpdate, window, cx);
                            }
                            CheckForUpdatesAction::Install => {
                                this.handle_install_update(&InstallUpdate, window, cx);
                            }
                        }
                    }
                }))
        } else {
            footer
        };

        footer.children(progress_bar)
    }

    pub fn render_sidebar_header(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let border_color = cx.theme().border.alpha(BORDER_SOFT_ALPHA);
        div()
            .h(px(44.0))
            .pl(px(SIDEBAR_HEADER_LEFT_PADDING))
            .pr(px(12.0))
            .flex()
            .items_center()
            .justify_between()
            .border_b_1()
            .border_color(border_color)
            .when(cfg!(target_os = "macos"), |el| el.items_end().pb(px(6.0)))
            .child(
                div()
                    .text_sm()
                    .font_weight(gpui::FontWeight::BOLD)
                    .text_color(cx.theme().foreground)
                    .child("AGENT TERM"),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap(px(4.0))
                    .child(
                        Button::new("sidebar-search")
                            .child(
                                Icon::new(IconName::Search)
                                    .size(IconSize::Small)
                                    .color(cx.theme().muted_foreground),
                            )
                            .ghost()
                            .compact()
                            .on_click(cx.listener(|this, _: &ClickEvent, window, cx| {
                                this.toggle_command_palette(&ToggleCommandPalette, window, cx);
                            })),
                    )
                    .child(
                        Button::new("sidebar-settings")
                            .child(
                                Icon::new(IconName::Settings)
                                    .size(IconSize::Small)
                                    .color(cx.theme().muted_foreground),
                            )
                            .ghost()
                            .compact()
                            .on_click(cx.listener(|this, _: &ClickEvent, window, cx| {
                                this.open_settings(&OpenSettings, window, cx);
                            })),
                    )
                    .child(
                        Button::new("sidebar-mcp")
                            // Tool SVGs use `currentColor`; set a color explicitly so it isn't invisible on dark themes.
                            .child(
                                Icon::asset("tool-icons/mcp.svg")
                                    .size(IconSize::Small)
                                    .color(cx.theme().muted_foreground),
                            )
                            .ghost()
                            .compact()
                            .on_click(cx.listener(|this, _: &ClickEvent, window, cx| {
                                this.open_mcp_manager(&ToggleMcpManager, window, cx);
                            })),
                    ),
            )
    }

    pub fn render_add_workspace(&self, cx: &mut Context<Self>) -> impl IntoElement {
        div().px(px(16.0)).py(px(12.0)).child(
            div()
                .id("sidebar-add-workspace")
                .text_sm()
                .text_color(cx.theme().muted_foreground)
                .cursor_pointer()
                .hover(|s| s.text_color(cx.theme().foreground))
                .on_click(cx.listener(|this, _: &ClickEvent, window, cx| {
                    this.open_add_workspace_dialog(window, cx);
                }))
                .child("+ Add Workspace"),
        )
    }

    pub fn render_workspaces_list(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let mut list = div()
            .id("sidebar-workspaces-scroll")
            .flex_1()
            .overflow_y_scroll()
            .px(px(8.0));
        for workspace in self.ordered_workspaces() {
            list = list.child(self.render_workspace(&workspace, cx));
        }
        list
    }

    pub fn render_workspace(
        &self,
        workspace: &WorkspaceItem,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let sessions: Vec<&SessionRecord> =
            self.ordered_sessions_for_workspace(&workspace.workspace.id);

        let workspace_id = workspace.workspace.id.clone();
        let workspace_id_for_context = workspace_id.clone();
        let is_collapsed = self.is_workspace_collapsed(&workspace_id);
        let workspace_icon = workspace.workspace.icon.clone();
        let is_default = workspace.is_default;
        let group_id = format!("workspace-group-{}", workspace.workspace.id);
        let dragging_session = self
            .dragging_session
            .as_ref()
            .filter(|dragging| dragging.has_moved)
            .and_then(|dragging| self.sessions.iter().find(|s| s.id == dragging.session_id));
        let drop_target = self.drop_target.as_ref();

        // Collect theme colors before building the add button
        let hover_bg = cx.theme().list_hover;
        let muted_fg = cx.theme().muted_foreground;
        let foreground = cx.theme().foreground;

        // Clone data needed for the add button dropdown
        let view = cx.entity();
        let shells = self.cached_shells.clone();
        let tools = self.cached_tools.clone();
        let pinned_ids = self.cached_pinned_shell_ids.clone();
        let workspace_id_for_menu = workspace_id.clone();

        let workspace_header = div()
            .id(format!("workspace-header-{}", workspace.workspace.id))
            .group(group_id.clone())
            .px(px(8.0))
            .py(px(6.0))
            .flex()
            .items_center()
            .gap(px(6.0))
            .rounded(px(6.0))
            .cursor_pointer()
            .hover(move |s| s.bg(hover_bg))
            .on_click(cx.listener({
                let workspace_id = workspace.workspace.id.clone();
                move |this, _, _, cx| {
                    this.toggle_workspace_collapsed(workspace_id.clone(), cx);
                    cx.notify();
                }
            }))
            .child(
                Icon::new(if is_collapsed {
                    IconName::ChevronRight
                } else {
                    IconName::ChevronDown
                })
                .size(IconSize::Small)
                .color(muted_fg),
            )
            .child(
                if is_default {
                    Icon::new(IconName::Terminal)
                } else {
                    workspace_icon
                        .as_ref()
                        .map(|s| icon_from_string(s))
                        .unwrap_or_else(|| Icon::new(IconName::Folder))
                }
                .size(IconSize::Medium)
                .color(if is_default { muted_fg } else { foreground }),
            )
            .child(
                div()
                    .text_sm()
                    .font_weight(gpui::FontWeight::MEDIUM)
                    .text_color(if is_default { muted_fg } else { foreground })
                    .flex_1()
                    .child(workspace.workspace.name.clone()),
            )
            .child(
                div()
                    .invisible()
                    .group_hover(group_id, |this| this.visible())
                    .child(
                        Button::new(format!("workspace-add-{}", workspace_id))
                            .label("+")
                            .ghost()
                            .compact()
                            .dropdown_menu_with_anchor(
                                Corner::TopRight,
                                move |menu, _window, _cx| {
                                    Self::build_add_menu(
                                        menu,
                                        workspace_id_for_menu.clone(),
                                        view.clone(),
                                        shells.clone(),
                                        tools.clone(),
                                        pinned_ids.clone(),
                                    )
                                },
                            ),
                    ),
            )
            .context_menu({
                move |menu, window, cx| {
                    if is_default {
                        return menu;
                    }
                    let current_handle: AnyWindowHandle = window.window_handle();
                    let other_windows = WindowRegistry::global().list_other_windows(current_handle);

                    let mut menu = menu.menu(
                        "Edit Workspace...",
                        Box::new(EditWorkspace(workspace_id_for_context.clone())),
                    );

                    menu = menu.menu(
                        "Move Workspace to New Window",
                        Box::new(MoveWorkspaceToNewWindow(workspace_id_for_context.clone())),
                    );

                    if !other_windows.is_empty() {
                        menu = menu.submenu("Move Workspace to Window", window, cx, {
                            let workspace_id = workspace_id_for_context.clone();
                            move |submenu, _window, _cx| {
                                let mut submenu = submenu;
                                for (_handle, info) in &other_windows {
                                    let workspace_id = workspace_id.clone();
                                    let window_id = info.number as u64;
                                    let title = info.title.clone();
                                    submenu = submenu.menu(
                                        &title,
                                        Box::new(MoveWorkspaceToWindow {
                                            workspace_id,
                                            target_window_id: window_id,
                                        }),
                                    );
                                }
                                submenu
                            }
                        });
                    }

                    menu.separator().menu(
                        "Remove Workspace",
                        Box::new(RemoveWorkspace(workspace_id_for_context.clone())),
                    )
                }
            });

        let mut container = div().py(px(4.0)).child(workspace_header);

        if is_collapsed {
            return container;
        }

        if sessions.is_empty() {
            container = container.child(
                div()
                    .px(px(12.0))
                    .py(px(4.0))
                    .text_sm()
                    .text_color(muted_fg)
                    .child("No terminals"),
            );
            return container;
        }

        for session in sessions {
            let insert_before = matches!(
                drop_target,
                Some(DropTarget::BeforeSession { session_id, workspace_id: target_workspace_id })
                    if session_id == &session.id && target_workspace_id == &workspace_id
            );
            if insert_before {
                if let Some(dragging_session) = dragging_session {
                    container = container.child(self.render_drop_placeholder(dragging_session, cx));
                }
            }

            container = container.child(self.render_session_row(session, cx));

            let insert_after = matches!(
                drop_target,
                Some(DropTarget::AfterSession { session_id, workspace_id: target_workspace_id })
                    if session_id == &session.id && target_workspace_id == &workspace_id
            );
            if insert_after {
                if let Some(dragging_session) = dragging_session {
                    container = container.child(self.render_drop_placeholder(dragging_session, cx));
                }
            }
        }

        container
    }

    /// Build the popup menu items for adding a new tab to a workspace
    fn build_add_menu(
        menu: PopupMenu,
        workspace_id: String,
        view: Entity<Self>,
        shells: Vec<agentterm_tools::ShellInfo>,
        tools: Vec<agentterm_tools::ToolInfo>,
        pinned_ids: Vec<String>,
    ) -> PopupMenu {
        use std::collections::HashSet;
        let pinned_set: HashSet<&str> = pinned_ids.iter().map(|s| s.as_str()).collect();

        // Separate shells into pinned and unpinned
        let mut pinned_shells: Vec<_> = shells
            .iter()
            .filter(|s| pinned_set.contains(s.id.as_str()))
            .cloned()
            .collect();
        pinned_shells.sort_by(|a, b| a.name.cmp(&b.name));

        let mut native_shells: Vec<_> = shells
            .iter()
            .filter(|s| s.shell_type == ShellType::Native && !pinned_set.contains(s.id.as_str()))
            .cloned()
            .collect();
        native_shells.sort_by(|a, b| a.name.cmp(&b.name));

        // Separate tools into builtin and custom
        let builtin_tools: Vec<_> = tools.iter().filter(|t| t.is_builtin).cloned().collect();
        let custom_tools: Vec<_> = tools.iter().filter(|t| !t.is_builtin).cloned().collect();

        let mut menu = menu;

        // Add pinned shells first (if any)
        if !pinned_shells.is_empty() {
            menu = menu.label("Pinned");
            for shell in pinned_shells {
                let workspace_id = workspace_id.clone();
                let view = view.clone();
                let shell_clone = shell.clone();
                menu = menu.item(PopupMenuItem::new(shell.name.clone()).on_click(
                    move |_event, window, cx| {
                        let icon = if shell_clone.icon.is_empty() {
                            None
                        } else {
                            Some(shell_clone.icon.clone())
                        };
                        view.update(cx, |app, cx| {
                            app.create_session_in_workspace(
                                workspace_id.clone(),
                                SessionTool::Shell,
                                shell_clone.name.clone(),
                                shell_clone.command.clone(),
                                shell_clone.args.clone(),
                                icon,
                                window,
                                cx,
                            );
                        });
                    },
                ));
            }
            menu = menu.separator();
        }

        // Add shells workspace
        if !native_shells.is_empty() {
            menu = menu.label("Shells");
            for shell in native_shells {
                let workspace_id = workspace_id.clone();
                let view = view.clone();
                let shell_clone = shell.clone();
                menu = menu.item(PopupMenuItem::new(shell.name.clone()).on_click(
                    move |_event, window, cx| {
                        let icon = if shell_clone.icon.is_empty() {
                            None
                        } else {
                            Some(shell_clone.icon.clone())
                        };
                        view.update(cx, |app, cx| {
                            app.create_session_in_workspace(
                                workspace_id.clone(),
                                SessionTool::Shell,
                                shell_clone.name.clone(),
                                shell_clone.command.clone(),
                                shell_clone.args.clone(),
                                icon,
                                window,
                                cx,
                            );
                        });
                    },
                ));
            }
            menu = menu.separator();
        }

        // Add tools workspace
        let has_builtin_tools = !builtin_tools.is_empty();
        let has_custom_tools = !custom_tools.is_empty();

        if has_builtin_tools || has_custom_tools {
            menu = menu.label("Tools");

            for tool in builtin_tools {
                let workspace_id = workspace_id.clone();
                let view = view.clone();
                let tool_clone = tool.clone();
                menu = menu.item(PopupMenuItem::new(tool.name.clone()).on_click(
                    move |_event, window, cx| {
                        let session_tool = match tool_clone.id.as_str() {
                            "claude" => SessionTool::Claude,
                            "gemini" => SessionTool::Gemini,
                            "codex" => SessionTool::Codex,
                            "openCode" => SessionTool::OpenCode,
                            _ => SessionTool::Custom(tool_clone.id.clone()),
                        };
                        let icon = if tool_clone.icon.is_empty() {
                            None
                        } else {
                            Some(tool_clone.icon.clone())
                        };
                        view.update(cx, |app, cx| {
                            app.create_session_in_workspace(
                                workspace_id.clone(),
                                session_tool,
                                tool_clone.name.clone(),
                                tool_clone.command.clone(),
                                tool_clone.args.clone(),
                                icon,
                                window,
                                cx,
                            );
                        });
                    },
                ));
            }

            if has_custom_tools && has_builtin_tools {
                menu = menu.separator();
            }

            for tool in custom_tools {
                let workspace_id = workspace_id.clone();
                let view = view.clone();
                let tool_clone = tool.clone();
                menu = menu.item(PopupMenuItem::new(tool.name.clone()).on_click(
                    move |_event, window, cx| {
                        let session_tool = SessionTool::Custom(tool_clone.id.clone());
                        let icon = if tool_clone.icon.is_empty() {
                            None
                        } else {
                            Some(tool_clone.icon.clone())
                        };
                        view.update(cx, |app, cx| {
                            app.create_session_in_workspace(
                                workspace_id.clone(),
                                session_tool,
                                tool_clone.name.clone(),
                                tool_clone.command.clone(),
                                tool_clone.args.clone(),
                                icon,
                                window,
                                cx,
                            );
                        });
                    },
                ));
            }
        }

        menu
    }

    fn build_session_row_content(
        &self,
        session: &SessionRecord,
        is_active: bool,
        include_close_button: bool,
        cx: &mut Context<Self>,
    ) -> Div {
        let title = if session.title.is_empty() {
            "Terminal".to_string()
        } else {
            session.title.clone()
        };
        let session_icon = session.icon.clone();
        let git_counts = self.git_diff_counts_for_session(&session.id);
        let status_color = match session.status {
            SessionStatus::Running => cx.theme().success,
            SessionStatus::Idle => cx.theme().muted_foreground,
            SessionStatus::Error => cx.theme().danger,
            SessionStatus::Starting => cx.theme().info,
            SessionStatus::Waiting => cx.theme().warning,
        };

        let mut row = div()
            .px(px(8.0))
            .py(px(4.0))
            .flex()
            .items_center()
            .gap(px(6.0))
            .rounded(px(6.0))
            .child(div().w(px(6.0)).h(px(6.0)).rounded_full().bg(status_color))
            .child(
                session_icon
                    .as_ref()
                    .map(|s| icon_from_string(s))
                    .unwrap_or_else(|| Icon::new(IconName::Terminal))
                    .size(IconSize::Small)
                    .color(if is_active {
                        cx.theme().accent_foreground
                    } else {
                        cx.theme().muted_foreground
                    }),
            )
            .child(
                div()
                    .text_sm()
                    .text_color(if is_active {
                        cx.theme().accent_foreground
                    } else {
                        cx.theme().foreground
                    })
                    .truncate()
                    .flex_1()
                    .child(title),
            );

        if let Some(counts) = git_counts {
            row = row.child(
                div()
                    .flex()
                    .items_center()
                    .gap(px(4.0))
                    .text_xs()
                    .child(
                        div()
                            .text_color(cx.theme().success)
                            .child(format!("+{}", counts.additions)),
                    )
                    .child(
                        div()
                            .text_color(cx.theme().danger)
                            .child(format!("-{}", counts.deletions)),
                    ),
            );
        }

        if include_close_button {
            row = row.child({
                let id = session.id.clone();
                Button::new(format!("session-close-{}", session.id))
                    .label("×")
                    .ghost()
                    .compact()
                    .on_click(cx.listener(move |this, _: &ClickEvent, window, cx| {
                        cx.stop_propagation();
                        this.close_session(id.clone(), window, cx);
                    }))
            });
        }

        row
    }

    fn render_drop_placeholder(&self, session: &SessionRecord, cx: &mut Context<Self>) -> Div {
        let accent = cx.theme().accent;
        self.build_session_row_content(session, false, false, cx)
            .bg(accent.alpha(0.12))
            .border_1()
            .border_color(accent)
            .opacity(0.7)
    }

    fn render_drag_preview(&self, cx: &mut Context<Self>) -> Option<AnyElement> {
        let dragging = self.dragging_session.as_ref()?;
        if !dragging.has_moved {
            return None;
        }
        let sidebar_bounds = self.sidebar_bounds?;
        let session = self
            .sessions
            .iter()
            .find(|session| session.id == dragging.session_id)?;
        let row_bounds = self.session_row_bounds.get(&session.id)?;
        let position = dragging.mouse_position - dragging.drag_offset - sidebar_bounds.origin;
        let accent = cx.theme().accent;
        let preview = self
            .build_session_row_content(session, false, false, cx)
            .bg(cx.theme().list_active)
            .border_1()
            .border_color(accent.alpha(0.4))
            .shadow_md()
            .opacity(0.9)
            .absolute()
            .left(position.x)
            .top(position.y)
            .w(row_bounds.size.width)
            .h(row_bounds.size.height);

        Some(preview.into_any_element())
    }

    pub fn render_session_row(
        &self,
        session: &SessionRecord,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let is_active = self
            .active_session_id
            .as_deref()
            .is_some_and(|id| id == session.id);
        let session_id = session.id.clone();
        let session_id_for_bounds = session_id.clone();
        let session_workspace_id = session.workspace_id.clone();
        let active_bg = cx.theme().accent;
        let active_fg = cx.theme().accent_foreground;
        let hover_bg = cx.theme().list_active;
        let is_dragging_row = self
            .dragging_session
            .as_ref()
            .is_some_and(|dragging| dragging.session_id == session.id && dragging.has_moved);
        let view = cx.entity();
        let row = self
            .build_session_row_content(session, is_active, true, cx)
            .id(format!("session-row-{}", session.id))
            .cursor_pointer()
            .when(is_active, move |s| s.bg(active_bg).text_color(active_fg))
            .hover(move |s| s.bg(hover_bg))
            .when(is_dragging_row, |row| row.opacity(0.35))
            .on_mouse_down(MouseButton::Left, {
                let session_id = session.id.clone();
                let workspace_id = session_workspace_id.clone();
                cx.listener(move |this, event: &MouseDownEvent, _window, cx| {
                    let drag_offset = this
                        .session_row_bounds
                        .get(&session_id)
                        .map(|bounds| event.position - bounds.origin)
                        .unwrap_or_else(|| point(px(0.0), px(0.0)));
                    this.dragging_session = Some(DraggingSession {
                        session_id: session_id.clone(),
                        workspace_id: workspace_id.clone(),
                        start_position: event.position,
                        mouse_position: event.position,
                        drag_offset,
                        has_moved: false,
                    });
                    this.drop_target = None;
                    cx.notify();
                })
            })
            .on_prepaint(move |bounds, _, cx| {
                view.update(cx, |this, _| {
                    this.session_row_bounds
                        .insert(session_id_for_bounds.clone(), bounds);
                });
            })
            .on_click(cx.listener({
                let id = session_id.clone();
                move |this, _: &ClickEvent, window, cx| {
                    this.set_active_session_id(id.clone(), window, cx);
                }
            }))
            .context_menu({
                move |menu, window, cx| {
                    let current_handle: AnyWindowHandle = window.window_handle();
                    let other_windows = WindowRegistry::global().list_other_windows(current_handle);

                    let mut menu = menu
                        .menu("Edit Tab...", Box::new(RenameSession(session_id.clone())))
                        .menu(
                            "Restart",
                            Box::new(RestartSessionAction(session_id.clone())),
                        )
                        .separator();

                    if !other_windows.is_empty() {
                        menu = menu.submenu("Move to Window", window, cx, {
                            let session_id = session_id.clone();
                            move |submenu, _window, _cx| {
                                let mut submenu = submenu;
                                for (_handle, info) in &other_windows {
                                    let session_id = session_id.clone();
                                    let window_id = info.number as u64;
                                    let title = info.title.clone();
                                    submenu = submenu.menu(
                                        &title,
                                        Box::new(MoveSessionToWindow {
                                            session_id,
                                            target_window_id: window_id,
                                        }),
                                    );
                                }
                                submenu
                            }
                        });
                    }

                    menu.menu(
                        "Open in New Window",
                        Box::new(OpenSessionInNewWindow(session_id.clone())),
                    )
                    .separator()
                    .menu("Close", Box::new(CloseSessionAction(session_id.clone())))
                }
            });

        row
    }

    // Workspace management methods
    pub fn toggle_workspace_collapsed(&mut self, workspace_id: String, cx: &mut Context<Self>) {
        if workspace_id == DEFAULT_WORKSPACE_ID {
            return;
        }
        let next = !self.is_workspace_collapsed(&workspace_id);
        self.layout_store
            .update_window(&self.layout_window_id, |window| {
                if next {
                    if !window.collapsed_workspaces.contains(&workspace_id) {
                        window.collapsed_workspaces.push(workspace_id.clone());
                    }
                } else {
                    window.collapsed_workspaces.retain(|id| id != &workspace_id);
                }
            });
        cx.notify();
    }
}
