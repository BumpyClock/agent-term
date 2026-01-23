//! Action handlers for AgentTermApp.

use std::collections::HashSet;
use std::sync::Arc;

use gpui::{
    div, prelude::*, px, size, AnyWindowHandle, Bounds, Context, Focusable, IntoElement,
    ParentElement, Styled, Window, WindowBounds, WindowOptions,
};

use super::window_registry::WindowRegistry;
use super::{
    create_new_window, create_new_window_with_session, create_window_from_layout, LayoutManager,
    MoveSessionToWindow, OpenSessionInNewWindow, TerminalPool,
};
use agentterm_session::DEFAULT_WORKSPACE_ID;
use gpui_component::input::InputState as GpuiInputState;

use crate::dialogs::{
    AddWorkspaceDialog, McpManagerDialog, SessionEditorDialog, TabPickerDialog,
    WorkspaceEditorDialog,
};
use crate::settings_dialog::SettingsDialog;
use crate::ui::{v_flex, ActiveTheme, Button, ButtonVariants, WindowExt};

use super::actions::*;
use super::command_palette_provider::{AgentTermProvider, CommandPalettePayload};
use super::search_manager;
use super::state::AgentTermApp;
use gpui_component::command_palette::{CommandPalette, CommandPaletteEvent};
// TODO: Import UpdateState when update_state() helper is added
// use crate::updater::UpdateState;

impl AgentTermApp {
    // Window action handlers
    pub fn toggle_sidebar(
        &mut self,
        _: &ToggleSidebar,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.sidebar_visible = !self.sidebar_visible;
        cx.notify();
    }

    /// Toggle the command palette visibility.
    pub fn toggle_command_palette(
        &mut self,
        _: &ToggleCommandPalette,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.command_palette.is_some() {
            self.close_command_palette(window, cx);
        } else {
            self.open_command_palette(window, cx);
        }
    }

    /// Open the command palette.
    fn open_command_palette(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        // Ensure search indexing starts in the background (non-blocking)
        search_manager::warm_search_manager_async(std::time::Duration::from_millis(0));

        // Create the provider with current app state
        let provider = Arc::new(AgentTermProvider::new(
            self.sessions.clone(),
            self.workspaces.clone(),
        ));

        // Open the palette using the new gpui-component CommandPalette
        let handle = CommandPalette::open(window, cx, provider);

        // Subscribe to palette events
        cx.subscribe_in(
            handle.state(),
            window,
            |this, _, event: &CommandPaletteEvent, window, cx| match event {
                CommandPaletteEvent::Selected { item } => {
                    this.handle_command_palette_select(item, window, cx);
                }
                CommandPaletteEvent::Dismissed => {
                    this.close_command_palette(window, cx);
                }
            },
        )
        .detach();

        self.command_palette = Some(handle.state().clone());
        cx.notify();
    }

    /// Close the command palette.
    fn close_command_palette(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.command_palette = None;
        window.close_dialog(cx);
        cx.notify();
    }

    /// Handle selection from the command palette.
    fn handle_command_palette_select(
        &mut self,
        item: &gpui_component::command_palette::CommandPaletteItem,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        // Close the palette first
        self.close_command_palette(window, cx);

        // Extract payload and handle based on type
        if let Some(payload) = &item.payload {
            if let Some(payload) = payload.downcast_ref::<CommandPalettePayload>() {
                match payload {
                    CommandPalettePayload::Session { id } => {
                        self.set_active_session_id(id.clone(), window, cx);
                    }
                    CommandPalettePayload::Workspace { id } => {
                        self.activate_workspace_from_palette(id.clone(), window, cx);
                    }
                    CommandPalettePayload::ClaudeConversation {
                        session_id,
                        workspace,
                        ..
                    } => {
                        // Resume the past Claude conversation with claude --resume
                        let title = format!("Resume: {}", workspace);
                        let command = "claude".to_string();
                        let args = vec!["--resume".to_string(), session_id.clone()];

                        self.create_session_from_tool(
                            agentterm_session::SessionTool::Claude,
                            title,
                            command,
                            args,
                            None,
                            window,
                            cx,
                        );
                    }
                    CommandPalettePayload::CodexConversation {
                        session_id,
                        workspace,
                        ..
                    } => {
                        // Resume the past Codex conversation with codex --resume
                        let title = format!("Resume: {}", workspace);
                        let command = "codex".to_string();
                        let args = vec!["--resume".to_string(), session_id.clone()];

                        self.create_session_from_tool(
                            agentterm_session::SessionTool::Codex,
                            title,
                            command,
                            args,
                            None,
                            window,
                            cx,
                        );
                    }
                    CommandPalettePayload::Action { action_id } => {
                        // Execute the action
                        match action_id.as_str() {
                            "new_tab" => {
                                self.create_default_shell_tab(window, cx);
                            }
                            "new_window" => {
                                // New window is handled via the global NewWindow action
                                window.dispatch_action(Box::new(NewWindow), cx);
                            }
                            "settings" => {
                                self.open_settings(&OpenSettings, window, cx);
                            }
                            "toggle_sidebar" => {
                                self.toggle_sidebar(&ToggleSidebar, window, cx);
                            }
                            _ => {}
                        }
                    }
                }
            }
        }
    }

    fn activate_workspace_from_palette(
        &mut self,
        workspace_id: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if let Some(window_layout) = self.window_layout() {
            if let Some(tab) = window_layout.tabs_in_workspace(&workspace_id).first() {
                self.set_active_session_id(tab.session_id.clone(), window, cx);
                return;
            }
        }

        let current_window_id = self.layout_window_id.clone();
        let mut target_layout_id = None;
        let mut target_session_id = None;

        for window_snapshot in self.layout_store.current_session().windows {
            if window_snapshot.id == current_window_id {
                continue;
            }

            let tabs = window_snapshot.tabs_in_workspace(&workspace_id);
            if tabs.is_empty() {
                continue;
            }

            if let Some(active_session_id) = window_snapshot.active_session_id.as_ref() {
                if tabs.iter().any(|tab| &tab.session_id == active_session_id) {
                    target_layout_id = Some(window_snapshot.id.clone());
                    target_session_id = Some(active_session_id.clone());
                    break;
                }
            }

            if target_layout_id.is_none() {
                target_layout_id = Some(window_snapshot.id.clone());
                target_session_id = tabs.first().map(|tab| tab.session_id.clone());
            }
        }

        if let Some(target_layout_id) = target_layout_id {
            if let Some(target_session_id) = target_session_id {
                if let Some(target_handle) = LayoutManager::global().get_handle(&target_layout_id) {
                    let _ = cx.update_window(target_handle, move |_root, window, cx| {
                        window.activate_window();
                        let target_handle = window.window_handle();
                        if let Some(weak_app) = WindowRegistry::global().get_app(&target_handle) {
                            if let Some(app) = weak_app.upgrade() {
                                app.update(cx, |app, cx| {
                                    app.set_active_session_id(target_session_id, window, cx);
                                });
                            }
                        }
                    });
                    return;
                }
            }
        }

        let mut sessions: Vec<_> = self
            .sessions
            .iter()
            .filter(|session| session.workspace_id == workspace_id)
            .collect();
        if sessions.is_empty() {
            agentterm_session::diagnostics::log(format!(
                "command_palette_workspace_restore_empty workspace_id={}",
                workspace_id
            ));
            return;
        }

        sessions.sort_by_key(|session| session.tab_order.unwrap_or(u32::MAX));

        self.layout_store
            .update_window(&self.layout_window_id, |layout| {
                if !layout.workspace_order.contains(&workspace_id) {
                    layout.workspace_order.push(workspace_id.clone());
                }
                for session in &sessions {
                    layout.append_tab(session.id.clone(), workspace_id.clone());
                }
                if let Some(first) = sessions.first() {
                    layout.active_session_id = Some(first.id.clone());
                }
            });

        if let Some(first) = sessions.first() {
            self.set_active_session_id(first.id.clone(), window, cx);
        }
    }

    pub fn minimize_window(&mut self, _: &Minimize, window: &mut Window, _cx: &mut Context<Self>) {
        window.minimize_window();
    }

    pub fn zoom_window(&mut self, _: &Zoom, window: &mut Window, _cx: &mut Context<Self>) {
        window.zoom_window();
    }

    // Settings dialog
    pub fn open_settings(&mut self, _: &OpenSettings, window: &mut Window, cx: &mut Context<Self>) {
        let settings = self.settings.clone();
        let app_entity = cx.entity().downgrade();
        let main_window_handle = window.window_handle();

        // Compute bounds before opening window to avoid borrow conflict
        let window_bounds =
            WindowBounds::Windowed(Bounds::centered(None, size(px(600.0), px(700.0)), cx));

        let _ = cx.open_window(
            WindowOptions {
                titlebar: Some(gpui::TitlebarOptions {
                    title: Some("Settings".into()),
                    appears_transparent: false,
                    ..Default::default()
                }),
                window_bounds: Some(window_bounds),
                kind: gpui::WindowKind::Normal,
                is_resizable: true,
                is_movable: true,
                focus: true,
                show: true,
                ..Default::default()
            },
            move |settings_window, cx| {
                // Create the SettingsDialog entity
                let dialog = cx.new(|cx| {
                    SettingsDialog::new(settings.clone(), settings_window, cx)
                        .on_change({
                            let app_entity = app_entity.clone();
                            move |new_settings, _window, cx| {
                                // Live preview: update settings in the main app
                                let _ = cx.update_window(main_window_handle, |_, window, cx| {
                                    if let Some(app) = app_entity.upgrade() {
                                        app.update(cx, |app, cx| {
                                            app.update_settings(new_settings.clone(), window, cx);
                                        });
                                    }
                                });
                            }
                        })
                        .on_save({
                            let app_entity = app_entity.clone();
                            move |new_settings, _window, cx| {
                                // Final save: update settings in the main app
                                let _ = cx.update_window(main_window_handle, |_, window, cx| {
                                    if let Some(app) = app_entity.upgrade() {
                                        app.update(cx, |app, cx| {
                                            app.update_settings(new_settings.clone(), window, cx);
                                        });
                                    }
                                });
                            }
                        })
                        .on_close({
                            move |window, _cx| {
                                window.remove_window();
                            }
                        })
                });
                // Wrap in Root to provide theme context for gpui-component elements
                cx.new(|cx| gpui_component::Root::new(dialog, settings_window, cx))
            },
        );
    }

    fn recent_workspace_paths(&self) -> Vec<String> {
        let mut paths = Vec::new();
        let mut seen = HashSet::new();
        for workspace in &self.workspaces {
            if workspace.is_default {
                continue;
            }
            let path = workspace.workspace.path.trim();
            if path.is_empty() {
                continue;
            }
            if seen.insert(path.to_string()) {
                paths.push(path.to_string());
            }
        }
        paths
    }

    // Add workspace dialog
    pub fn open_add_workspace_dialog(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let view = cx.entity();
        let recent_paths = self.recent_workspace_paths();

        let name_input = cx.new(|cx| GpuiInputState::new(window, cx).placeholder("Workspace name"));
        let path_input = cx.new(|cx| GpuiInputState::new(window, cx).placeholder("Workspace path"));
        let name_focus = name_input.read(cx).focus_handle(cx);

        let dialog_entity = cx.new(|_cx| {
            AddWorkspaceDialog::new(
                view.clone(),
                name_input.clone(),
                path_input.clone(),
                recent_paths.clone(),
            )
        });

        dialog_entity.update(cx, |dialog, cx| {
            dialog.setup_path_input_subscriptions(window, cx);
        });

        window.open_dialog(cx, move |dialog, _window, _cx| {
            dialog
                .title("Add Workspace")
                .w(px(400.))
                .child(dialog_entity.clone())
                .footer({
                    let dialog_entity = dialog_entity.clone();
                    move |_ok, cancel, window, cx| {
                        vec![
                            cancel(window, cx),
                            Button::new("save")
                                .primary()
                                .label("Save")
                                .on_click({
                                    let dialog_entity = dialog_entity.clone();
                                    move |_, window, cx| {
                                        dialog_entity.update(cx, |dialog, cx| {
                                            dialog.save(window, cx);
                                        });
                                    }
                                })
                                .into_any_element(),
                        ]
                    }
                })
        });

        name_focus.focus(window, cx);
    }

    // Workspace editor dialog
    pub fn open_workspace_editor(
        &mut self,
        workspace_id: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(workspace) = self
            .workspaces
            .iter()
            .find(|s| s.workspace.id == workspace_id)
            .map(|s| s.workspace.clone())
        else {
            return;
        };

        let view = cx.entity();
        let recent_paths = self.recent_workspace_paths();

        let name_input = cx.new(|cx| {
            GpuiInputState::new(window, cx)
                .placeholder("Workspace name")
                .default_value(workspace.name.clone())
        });
        let path_input = cx.new(|cx| {
            GpuiInputState::new(window, cx)
                .placeholder("Workspace path")
                .default_value(workspace.path.clone())
        });

        let name_focus = name_input.read(cx).focus_handle(cx);

        let dialog_entity = cx.new(|_cx| {
            WorkspaceEditorDialog::new(
                view.clone(),
                workspace_id.clone(),
                name_input.clone(),
                path_input.clone(),
                workspace.icon.clone(),
                recent_paths.clone(),
            )
        });

        dialog_entity.update(cx, |dialog, cx| {
            dialog.setup_path_input_subscriptions(window, cx);
        });

        window.open_dialog(cx, move |dialog, _window, _cx| {
            dialog
                .title("Edit Workspace")
                .w(px(400.))
                .child(dialog_entity.clone())
                .footer({
                    let dialog_entity = dialog_entity.clone();
                    move |_ok, cancel, window, cx| {
                        vec![
                            cancel(window, cx),
                            Button::new("save")
                                .primary()
                                .label("Save")
                                .on_click({
                                    let dialog_entity = dialog_entity.clone();
                                    move |_, window, cx| {
                                        dialog_entity.update(cx, |dialog, cx| {
                                            dialog.save(window, cx);
                                        });
                                    }
                                })
                                .into_any_element(),
                        ]
                    }
                })
        });

        name_focus.focus(window, cx);
    }

    // Session rename dialog
    pub fn open_session_rename(
        &mut self,
        session_id: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(session) = self.sessions.iter().find(|s| s.id == session_id).cloned() else {
            return;
        };

        let view = cx.entity();

        // Create inputs with current values
        let name_input = cx.new(|cx| {
            GpuiInputState::new(window, cx)
                .placeholder("Tab name")
                .default_value(session.title.clone())
        });
        let command_input = cx.new(|cx| {
            GpuiInputState::new(window, cx)
                .placeholder("Command (e.g., /bin/zsh)")
                .default_value(session.command.clone())
        });
        let name_focus = name_input.read(cx).focus_handle(cx);

        let dialog_entity = cx.new(|_cx| {
            SessionEditorDialog::new(
                view.clone(),
                session_id.clone(),
                name_input.clone(),
                command_input.clone(),
                session.icon.clone(),
            )
        });

        window.open_dialog(cx, move |dialog, _window, _cx| {
            dialog
                .title("Edit Tab")
                .w(px(400.))
                .child(dialog_entity.clone())
                .footer({
                    let dialog_entity = dialog_entity.clone();
                    move |_ok, cancel, window, cx| {
                        vec![
                            cancel(window, cx),
                            Button::new("save")
                                .primary()
                                .label("Save")
                                .on_click({
                                    let dialog_entity = dialog_entity.clone();
                                    move |_, window, cx| {
                                        dialog_entity.update(cx, |dialog, cx| {
                                            dialog.save(window, cx);
                                        });
                                    }
                                })
                                .into_any_element(),
                        ]
                    }
                })
        });

        name_focus.focus(window, cx);
        cx.notify();
    }

    // MCP Manager dialog
    pub fn open_mcp_manager(
        &mut self,
        _: &ToggleMcpManager,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let tokio = self.tokio.clone();
        let mcp_manager = self.mcp_manager.clone();

        let session_title = self
            .active_session()
            .map(|s| s.title.clone())
            .unwrap_or_default();
        let workspace_path = self.active_session().and_then(|s| {
            if s.workspace_path.is_empty() {
                None
            } else {
                Some(s.workspace_path.clone())
            }
        });

        let dialog_entity = cx.new(|_cx| {
            let mut dialog =
                McpManagerDialog::new(tokio, mcp_manager, session_title, workspace_path);
            dialog.load_data();
            dialog
        });

        window.open_dialog(cx, move |dialog, _window, _cx| {
            dialog
                .title("MCP Manager")
                .w(px(720.))
                .close_button(true)
                .child(dialog_entity.clone())
        });

        cx.notify();
    }

    // New shell tab dialog
    pub fn new_shell_tab(&mut self, _: &NewShellTab, window: &mut Window, cx: &mut Context<Self>) {
        let view = cx.entity();
        let tokio = self.tokio.clone();
        let mcp_manager = self.mcp_manager.clone();

        // Create dialog entity with its own state
        let dialog_entity = cx.new(|_cx| {
            let mut dialog = TabPickerDialog::new(view, tokio, mcp_manager);
            dialog.load_data();
            dialog
        });

        window.open_dialog(cx, move |dialog, _window, _cx| {
            dialog
                .title("Create tab")
                .w(px(280.))
                .max_h(px(540.))
                .close_button(true)
                .child(dialog_entity.clone())
        });

        self.ensure_active_terminal(window, cx);
        cx.notify();
    }

    // Action struct handlers for context menu items
    pub fn handle_rename_session(
        &mut self,
        action: &RenameSession,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.open_session_rename(action.0.clone(), window, cx);
    }

    pub fn handle_close_session(
        &mut self,
        action: &CloseSessionAction,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let session_id = action.0.clone();

        // Check if the session has a running child process
        if self.session_has_running_process(&session_id, cx) {
            // Show confirmation dialog
            let view = cx.entity();
            let session_id_for_close = session_id;

            window.open_dialog(cx, move |dialog, _window, _cx| {
                dialog
                    .title("Close Tab")
                    .w(px(400.))
                    .child(div().text_sm().child(
                        "This tab has a running process. Are you sure you want to close it?",
                    ))
                    .footer({
                        let view = view.clone();
                        let session_id = session_id_for_close.clone();
                        move |_ok, cancel, window, cx| {
                            vec![
                                cancel(window, cx),
                                Button::new("close")
                                    .danger()
                                    .label("Close")
                                    .on_click({
                                        let view = view.clone();
                                        let session_id = session_id.clone();
                                        move |_, window, cx| {
                                            window.close_dialog(cx);
                                            view.update(cx, |app, cx| {
                                                app.close_session(session_id.clone(), window, cx);
                                            });
                                        }
                                    })
                                    .into_any_element(),
                            ]
                        }
                    })
            });
        } else {
            self.close_session(session_id, window, cx);
        }
    }

    pub fn handle_close_tab(&mut self, _: &CloseTab, window: &mut Window, cx: &mut Context<Self>) {
        let Some(active_id) = self.active_session_id.clone() else {
            return;
        };
        self.handle_close_session(&CloseSessionAction(active_id), window, cx);
    }

    /// Helper method to check if a session has a running child process.
    fn session_has_running_process(&self, session_id: &str, cx: &Context<Self>) -> bool {
        let pool = TerminalPool::global();
        if let Some(terminal) = pool.get(session_id) {
            return terminal.read(cx).has_running_child_process();
        }
        false
    }

    /// Handle the CloseWindow action.
    /// Shows confirmation if any tabs have running processes.
    pub fn handle_close_window(
        &mut self,
        _: &CloseWindow,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        // Count tabs with running processes in this window
        let running_count = self.count_running_sessions(cx);

        if running_count > 0 {
            // Show confirmation dialog
            let tabs_text = if running_count == 1 {
                "1 tab has a running process".to_string()
            } else {
                format!("{} tabs have running processes", running_count)
            };

            window.open_dialog(cx, move |dialog, _window, _cx| {
                dialog
                    .title("Close Window")
                    .w(px(400.))
                    .child(
                        div()
                            .text_sm()
                            .child(format!("{}. Close window anyway?", tabs_text)),
                    )
                    .footer(move |_ok, cancel, window, cx| {
                        vec![
                            cancel(window, cx),
                            Button::new("close")
                                .danger()
                                .label("Close Window")
                                .on_click(move |_, window, cx| {
                                    window.close_dialog(cx);
                                    window.remove_window();
                                })
                                .into_any_element(),
                        ]
                    })
            });
        } else {
            // No running processes, close immediately
            window.remove_window();
        }
    }

    /// Count the number of sessions in this window that have running child processes.
    fn count_running_sessions(&self, cx: &Context<Self>) -> usize {
        let Some(window_layout) = self.window_layout() else {
            return 0;
        };

        let pool = TerminalPool::global();
        let mut count = 0;

        for tab in &window_layout.tabs {
            if let Some(terminal) = pool.get(&tab.session_id) {
                if terminal.read(cx).has_running_child_process() {
                    count += 1;
                }
            }
        }

        count
    }

    pub fn handle_restart_session(
        &mut self,
        action: &RestartSessionAction,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.restart_session(action.0.clone(), window, cx);
    }

    pub fn handle_edit_workspace(
        &mut self,
        action: &EditWorkspace,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.open_workspace_editor(action.0.clone(), window, cx);
    }

    pub fn handle_remove_workspace(
        &mut self,
        action: &RemoveWorkspace,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let workspace_id = action.0.clone();

        // Prevent removing the default workspace
        if workspace_id == agentterm_session::DEFAULT_WORKSPACE_ID {
            return;
        }

        // Find the workspace
        let Some(workspace) = self
            .workspaces
            .iter()
            .find(|s| s.workspace.id == workspace_id)
            .map(|s| s.workspace.clone())
        else {
            return;
        };

        // Count sessions in this workspace
        let session_count = self
            .sessions
            .iter()
            .filter(|s| s.workspace_id == workspace_id)
            .count();

        // Get session titles for display (max 5)
        let session_titles: Vec<String> = self
            .sessions
            .iter()
            .filter(|s| s.workspace_id == workspace_id)
            .take(5)
            .map(|s| s.title.clone())
            .collect();

        let view = cx.entity();
        let workspace_id_for_delete = workspace_id;
        let workspace_name = workspace.name;

        window.open_dialog(cx, move |dialog, _window, cx| {
            let mut content = v_flex().gap(px(12.));

            // Confirmation message
            content = content.child(div().text_sm().child(format!(
                "Are you sure you want to remove \"{}\"?",
                workspace_name
            )));

            // Session info warning
            if session_count > 0 {
                let tabs_text = if session_count == 1 {
                    "1 tab".to_string()
                } else {
                    format!("{} tabs", session_count)
                };

                content = content.child(
                    div()
                        .mt(px(8.))
                        .p(px(12.))
                        .rounded(px(6.))
                        .bg(cx.theme().warning.opacity(0.1))
                        .border_1()
                        .border_color(cx.theme().warning.opacity(0.3))
                        .child(
                            v_flex()
                                .gap(px(4.))
                                .child(
                                    div()
                                        .text_sm()
                                        .font_weight(gpui::FontWeight::MEDIUM)
                                        .text_color(cx.theme().warning)
                                        .child(format!("This workspace has {}", tabs_text)),
                                )
                                .child(
                                    div()
                                        .text_sm()
                                        .text_color(cx.theme().muted_foreground)
                                        .child(
                                            "These tabs will be moved to the Default workspace.",
                                        ),
                                ),
                        ),
                );

                // List session titles (max 5)
                if !session_titles.is_empty() {
                    let mut list = v_flex().gap(px(2.)).mt(px(8.));
                    for title in &session_titles {
                        list = list.child(
                            div()
                                .text_sm()
                                .text_color(cx.theme().muted_foreground)
                                .child(format!("• {}", title)),
                        );
                    }
                    if session_count > 5 {
                        list = list.child(
                            div()
                                .text_sm()
                                .text_color(cx.theme().muted_foreground)
                                .child(format!("...and {} more", session_count - 5)),
                        );
                    }
                    content = content.child(list);
                }
            }

            dialog
                .title("Remove Workspace")
                .w(px(400.))
                .child(content)
                .footer({
                    let view = view.clone();
                    let workspace_id = workspace_id_for_delete.clone();
                    move |_ok, cancel, window, cx| {
                        vec![
                            cancel(window, cx),
                            Button::new("remove")
                                .danger()
                                .label("Remove")
                                .on_click({
                                    let view = view.clone();
                                    let workspace_id = workspace_id.clone();
                                    move |_, window, cx| {
                                        window.close_dialog(cx);
                                        view.update(cx, |app, cx| {
                                            let _ =
                                                app.session_store.delete_workspace(&workspace_id);
                                            app.reload_from_store(cx);
                                            app.ensure_active_terminal(window, cx);
                                        });
                                    }
                                })
                                .into_any_element(),
                        ]
                    }
                })
        });
    }

    // Multi-window session transfer handlers

    /// Handles the MoveSessionToWindow action.
    /// Moves a session's view to another window while keeping the terminal running.
    pub fn handle_move_session_to_window(
        &mut self,
        action: &MoveSessionToWindow,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let session_id = action.session_id.clone();

        let target_handle = WindowRegistry::global()
            .list_windows()
            .into_iter()
            .find(|(handle, info)| {
                info.number as u64 == action.target_window_id
                    || handle.window_id().as_u64() == action.target_window_id
            })
            .map(|(handle, _)| handle);

        let Some(target_window) = target_handle else {
            return;
        };

        self.move_session_to_window(session_id, target_window, window, cx);
    }

    /// Handles the OpenSessionInNewWindow action.
    /// Creates a new window and moves the session there.
    pub fn handle_open_session_in_new_window(
        &mut self,
        action: &OpenSessionInNewWindow,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.open_session_in_new_window(action.0.clone(), window, cx);
    }

    /// Handles the MoveWorkspaceToWindow action.
    pub fn handle_move_workspace_to_window(
        &mut self,
        action: &MoveWorkspaceToWindow,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let target_handle = WindowRegistry::global()
            .list_windows()
            .into_iter()
            .find(|(handle, info)| {
                info.number as u64 == action.target_window_id
                    || handle.window_id().as_u64() == action.target_window_id
            })
            .map(|(handle, _)| handle);

        let Some(target_window) = target_handle else {
            return;
        };

        self.move_workspace_to_window(action.workspace_id.clone(), target_window, window, cx);
    }

    /// Handles the MoveWorkspaceToNewWindow action.
    pub fn handle_move_workspace_to_new_window(
        &mut self,
        action: &MoveWorkspaceToNewWindow,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(window_layout) = self.window_layout() else {
            return;
        };

        if !window_layout
            .tabs
            .iter()
            .any(|tab| tab.workspace_id == action.0)
        {
            return;
        }

        let Some(target_window) = create_new_window(cx) else {
            return;
        };

        self.move_workspace_to_window(action.0.clone(), target_window, window, cx);
    }

    pub fn move_workspace_to_window(
        &mut self,
        workspace_id: String,
        target_window: AnyWindowHandle,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(window_layout) = self.window_layout() else {
            return;
        };

        let moved_tabs: Vec<String> = window_layout
            .tabs
            .iter()
            .filter(|tab| tab.workspace_id == workspace_id)
            .map(|tab| tab.session_id.clone())
            .collect();

        if moved_tabs.is_empty() {
            return;
        }

        for session_id in &moved_tabs {
            self.terminal_views.remove(session_id);
        }

        self.layout_store
            .update_window(&self.layout_window_id, |layout| {
                layout.tabs.retain(|tab| tab.workspace_id != workspace_id);
                layout.workspace_order.retain(|id| id != &workspace_id);
                if let Some(active_id) = layout.active_session_id.clone() {
                    if moved_tabs.contains(&active_id) {
                        layout.active_session_id =
                            layout.tabs.first().map(|tab| tab.session_id.clone());
                    }
                }
            });

        if let Some(active_id) = self.active_session_id.clone() {
            if moved_tabs.contains(&active_id) {
                self.active_session_id = self
                    .sessions
                    .iter()
                    .find(|s| self.is_session_visible(&s.id))
                    .map(|s| s.id.clone());
                self.ensure_active_terminal(window, cx);
            }
        }

        let workspace_id_for_target = workspace_id;
        let _ = cx.update_window(target_window, move |_root, window, cx| {
            if let Some(weak_app) = WindowRegistry::global().get_app(&target_window) {
                if let Some(app) = weak_app.upgrade() {
                    app.update(cx, |app, cx| {
                        app.layout_store
                            .update_window(&app.layout_window_id, |layout| {
                                if !layout.workspace_order.contains(&workspace_id_for_target) {
                                    layout.workspace_order.push(workspace_id_for_target.clone());
                                }
                                for session_id in &moved_tabs {
                                    layout.append_tab(
                                        session_id.clone(),
                                        workspace_id_for_target.clone(),
                                    );
                                }
                                layout.active_session_id = moved_tabs.first().cloned();
                            });
                        if let Some(first) = moved_tabs.first() {
                            let _ = app.session_store.set_active_session(Some(first.clone()));
                            app.active_session_id = Some(first.clone());
                            cx.defer_in(window, |app, window, cx| {
                                app.ensure_active_terminal(window, cx);
                            });
                        }
                        cx.notify();
                    });
                }
            }
        });

        cx.notify();
    }

    /// Moves a session's view to another window.
    /// The terminal stays running in the global pool.
    pub fn move_session_to_window(
        &mut self,
        session_id: String,
        target_window: AnyWindowHandle,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let workspace_id = self
            .sessions
            .iter()
            .find(|s| s.id == session_id)
            .map(|s| s.workspace_id.clone())
            .unwrap_or_else(|| DEFAULT_WORKSPACE_ID.to_string());

        self.terminal_views.remove(&session_id);
        self.layout_store
            .update_window(&self.layout_window_id, |window_layout| {
                window_layout.remove_tab(&session_id);
                if window_layout.active_session_id.as_deref() == Some(&session_id) {
                    window_layout.active_session_id =
                        window_layout.tabs.first().map(|tab| tab.session_id.clone());
                }
            });

        if self.active_session_id.as_deref() == Some(&session_id) {
            self.active_session_id = self
                .sessions
                .iter()
                .find(|s| s.id != session_id && self.is_session_visible(&s.id))
                .map(|s| s.id.clone());
            self.ensure_active_terminal(window, cx);
        }

        let session_id_for_target = session_id.clone();
        let workspace_id_for_target = workspace_id;
        let _ = cx.update_window(target_window, move |_root, window, cx| {
            if let Some(weak_app) = WindowRegistry::global().get_app(&target_window) {
                if let Some(app) = weak_app.upgrade() {
                    app.update(cx, |app, cx| {
                        app.layout_store
                            .update_window(&app.layout_window_id, |layout| {
                                if !layout.workspace_order.contains(&workspace_id_for_target) {
                                    layout.workspace_order.push(workspace_id_for_target.clone());
                                }
                                layout.append_tab(
                                    session_id_for_target.clone(),
                                    workspace_id_for_target.clone(),
                                );
                                layout.active_session_id = Some(session_id_for_target.clone());
                            });
                        let _ = app
                            .session_store
                            .set_active_session(Some(session_id_for_target.clone()));
                        app.active_session_id = Some(session_id_for_target);
                        cx.defer_in(window, |app, window, cx| {
                            app.ensure_active_terminal(window, cx);
                        });
                        cx.notify();
                    });
                }
            }
        });

        cx.notify();
    }

    /// Opens a session in a new window.
    /// Creates the new window with just this session visible.
    pub fn open_session_in_new_window(
        &mut self,
        session_id: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let workspace_id = self
            .sessions
            .iter()
            .find(|s| s.id == session_id)
            .map(|s| s.workspace_id.clone())
            .unwrap_or_else(|| DEFAULT_WORKSPACE_ID.to_string());

        self.terminal_views.remove(&session_id);
        self.layout_store
            .update_window(&self.layout_window_id, |window_layout| {
                window_layout.remove_tab(&session_id);
                if window_layout.active_session_id.as_deref() == Some(&session_id) {
                    window_layout.active_session_id =
                        window_layout.tabs.first().map(|tab| tab.session_id.clone());
                }
            });

        let _ = create_new_window_with_session(session_id.clone(), workspace_id, cx);

        if self.active_session_id.as_deref() == Some(&session_id) {
            self.active_session_id = self
                .sessions
                .iter()
                .find(|s| s.id != session_id && self.is_session_visible(&s.id))
                .map(|s| s.id.clone());
            self.ensure_active_terminal(window, cx);
        }

        cx.notify();
    }

    pub fn reopen_closed(&mut self, _: &ReopenClosed, window: &mut Window, cx: &mut Context<Self>) {
        let layout_manager = LayoutManager::global();
        let current_session = self.layout_store.current_session();
        let has_tabs = current_session
            .windows
            .iter()
            .any(|current| !current.tabs.is_empty());

        if !has_tabs {
            let Some(session) = self.layout_store.pop_closed_session() else {
                return;
            };
            self.layout_store.set_current_session(session.clone());
            let mut windows = session.windows;
            windows.sort_by_key(|window| window.order);
            let next_order = windows
                .iter()
                .map(|window| window.order)
                .max()
                .unwrap_or(0)
                .saturating_add(1);
            layout_manager.set_next_order(next_order);
            if windows.is_empty() {
                let _ = create_new_window(cx);
            } else {
                for window in windows {
                    let _ = create_window_from_layout(window.id, cx);
                }
            }
            return;
        }

        let Some(tab) = self.layout_store.pop_closed_tab() else {
            return;
        };

        let target_layout_id = if let Some(window_id) = tab.window_id.clone() {
            if layout_manager.get_handle(&window_id).is_some() {
                window_id
            } else {
                self.layout_window_id.clone()
            }
        } else {
            self.layout_window_id.clone()
        };

        if target_layout_id == self.layout_window_id {
            self.layout_store
                .update_window(&self.layout_window_id, |layout| {
                    if !layout.workspace_order.contains(&tab.workspace_id) {
                        layout.workspace_order.push(tab.workspace_id.clone());
                    }
                    layout.append_tab(tab.session_id.clone(), tab.workspace_id.clone());
                    layout.active_session_id = Some(tab.session_id.clone());
                });
            let _ = self
                .session_store
                .set_active_session(Some(tab.session_id.clone()));
            self.active_session_id = Some(tab.session_id.clone());
            self.ensure_active_terminal(window, cx);
            cx.notify();
            return;
        }

        let Some(target_handle) = layout_manager.get_handle(&target_layout_id) else {
            return;
        };

        let _ = cx.update_window(target_handle, move |_root, window, cx| {
            if let Some(weak_app) = WindowRegistry::global().get_app(&target_handle) {
                if let Some(app) = weak_app.upgrade() {
                    app.update(cx, |app, cx| {
                        app.layout_store
                            .update_window(&app.layout_window_id, |layout| {
                                if !layout.workspace_order.contains(&tab.workspace_id) {
                                    layout.workspace_order.push(tab.workspace_id.clone());
                                }
                                layout.append_tab(tab.session_id.clone(), tab.workspace_id.clone());
                                layout.active_session_id = Some(tab.session_id.clone());
                            });
                        let _ = app
                            .session_store
                            .set_active_session(Some(tab.session_id.clone()));
                        app.active_session_id = Some(tab.session_id.clone());
                        cx.defer_in(window, |app, window, cx| {
                            app.ensure_active_terminal(window, cx);
                        });
                        cx.notify();
                    });
                }
            }
        });
    }

    // Update action handlers

    /// Handle the CheckForUpdates action.
    pub fn handle_check_for_updates(
        &mut self,
        _: &CheckForUpdates,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.update_manager.update(cx, |manager, cx| {
            manager.check_for_updates(cx);
        });

        // Update last check time in settings
        self.settings.update_last_check_time();
        let _ = self.settings.save();
    }

    /// Handle the DownloadUpdate action.
    pub fn handle_download_update(
        &mut self,
        _: &DownloadUpdate,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.update_manager.update(cx, |manager, cx| {
            manager.download_update(cx);
        });
    }

    /// Handle the InstallUpdate action.
    pub fn handle_install_update(
        &mut self,
        _: &InstallUpdate,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.update_manager.update(cx, |manager, cx| {
            manager.apply_update(cx);
        });
    }

    /// Handle the DismissUpdateNotification action.
    pub fn handle_dismiss_update_notification(
        &mut self,
        _: &DismissUpdateNotification,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.update_manager.update(cx, |manager, cx| {
            manager.dismiss(cx);
        });
    }

    // TODO: Add update_state() helper when needed for parent components:
    // pub fn update_state(&self, cx: &Context<Self>) -> UpdateState
}
