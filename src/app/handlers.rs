//! Action handlers for AgentTermApp.

use std::collections::HashSet;

use gpui::{
    AnyWindowHandle, Bounds, Context, Focusable, IntoElement, ParentElement, Styled, Window,
    WindowBounds, WindowOptions, div, prelude::*, px, size,
};

use super::window_registry::WindowRegistry;
use super::{
    LayoutManager, MoveSessionToWindow, OpenSessionInNewWindow, create_new_window,
    create_new_window_with_session, create_window_from_layout,
};
use agentterm_session::DEFAULT_SECTION_ID;
use gpui_component::input::{Input as GpuiInput, InputState as GpuiInputState};

use crate::dialogs::{
    AddProjectDialog, McpManagerDialog, ProjectEditorDialog, SessionEditorDialog, TabPickerDialog,
};
use crate::settings_dialog::SettingsDialog;
use crate::ui::{ActiveTheme, Button, ButtonVariants, WindowExt, v_flex};

use super::actions::*;
use super::command_palette::{
    CommandPalette, CommandPaletteDismissEvent, CommandPaletteSelectEvent, CommandResult,
    default_actions,
};
use super::search_manager::global_search_manager;
use super::state::AgentTermApp;

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
            self.close_command_palette(cx);
        } else {
            self.open_command_palette(window, cx);
        }
    }

    /// Open the command palette.
    fn open_command_palette(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        // Get the global search manager for past conversation search
        let search_manager = global_search_manager();

        // Clone data for use in closure
        let sessions = self.sessions.clone();
        let layout_store = self.layout_store.clone();

        let palette = cx.new(|cx| {
            let mut palette = CommandPalette::new(window, cx);

            // Set the search manager for past conversation search
            palette.set_search_manager(search_manager);

            // Populate with default actions and current sessions
            let mut results = default_actions();

            // Add current sessions to results
            for session in &sessions {
                results.push(CommandResult::Session {
                    id: session.id.clone(),
                    title: session.title.clone(),
                    project: format!("{:?}", session.tool),
                });
            }

            // Add saved workspaces to results
            for workspace in layout_store.list_workspaces() {
                results.push(CommandResult::Workspace {
                    id: workspace.id,
                    name: workspace.name,
                    created_at: workspace.created_at,
                });
            }

            palette.set_results(results, cx);
            palette
        });

        // Subscribe to palette events
        cx.subscribe_in(&palette, window, |this, _, event: &CommandPaletteSelectEvent, window, cx| {
            this.handle_command_palette_select(&event.0, window, cx);
        }).detach();

        cx.subscribe_in(&palette, window, |this, _, _: &CommandPaletteDismissEvent, _window, cx| {
            this.close_command_palette(cx);
        }).detach();

        // Focus the palette
        palette.update(cx, |palette, cx| {
            palette.focus(window, cx);
        });

        self.command_palette = Some(palette);
        cx.notify();
    }

    /// Close the command palette.
    fn close_command_palette(&mut self, cx: &mut Context<Self>) {
        self.command_palette = None;
        cx.notify();
    }

    /// Handle selection from the command palette.
    fn handle_command_palette_select(
        &mut self,
        result: &CommandResult,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        match result {
            CommandResult::Session { id, .. } => {
                // Switch to the selected session
                self.active_session_id = Some(id.clone());
                self.close_command_palette(cx);
                cx.notify();
            }
            CommandResult::Workspace { id, .. } => {
                // Load the saved workspace
                // TODO: Implement workspace loading
                eprintln!("Load workspace: {}", id);
                self.close_command_palette(cx);
            }
            CommandResult::PastConversation {
                session_id,
                project,
                ..
            } => {
                // Resume the past conversation with claude --resume
                self.close_command_palette(cx);

                // Create a new Claude session with the --resume flag
                let title = format!("Resume: {}", project);
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
            CommandResult::Action { action_id, .. } => {
                self.close_command_palette(cx);
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

    fn recent_project_paths(&self) -> Vec<String> {
        let mut paths = Vec::new();
        let mut seen = HashSet::new();
        for section in &self.sections {
            if section.is_default {
                continue;
            }
            let path = section.section.path.trim();
            if path.is_empty() {
                continue;
            }
            if seen.insert(path.to_string()) {
                paths.push(path.to_string());
            }
        }
        paths
    }

    // Add project dialog
    pub fn open_add_project_dialog(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let view = cx.entity().clone();
        let recent_paths = self.recent_project_paths();

        let name_input = cx.new(|cx| GpuiInputState::new(window, cx).placeholder("Project name"));
        let path_input = cx.new(|cx| GpuiInputState::new(window, cx).placeholder("Project path"));
        let name_focus = name_input.read(cx).focus_handle(cx);

        let dialog_entity = cx.new(|_cx| {
            AddProjectDialog::new(
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
                .title("Add Project")
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

    // Project editor dialog
    pub fn open_project_editor(
        &mut self,
        section_id: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(section) = self
            .sections
            .iter()
            .find(|s| s.section.id == section_id)
            .map(|s| s.section.clone())
        else {
            return;
        };

        let view = cx.entity().clone();
        let recent_paths = self.recent_project_paths();

        let name_input = cx.new(|cx| {
            GpuiInputState::new(window, cx)
                .placeholder("Project name")
                .default_value(section.name.clone())
        });
        let path_input = cx.new(|cx| {
            GpuiInputState::new(window, cx)
                .placeholder("Project path")
                .default_value(section.path.clone())
        });

        let name_focus = name_input.read(cx).focus_handle(cx);

        let dialog_entity = cx.new(|_cx| {
            ProjectEditorDialog::new(
                view.clone(),
                section_id.clone(),
                name_input.clone(),
                path_input.clone(),
                section.icon.clone(),
                recent_paths.clone(),
            )
        });

        dialog_entity.update(cx, |dialog, cx| {
            dialog.setup_path_input_subscriptions(window, cx);
        });

        window.open_dialog(cx, move |dialog, _window, _cx| {
            dialog
                .title("Edit Project")
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

    // Session menu handlers
    pub fn open_session_menu(&mut self, session_id: String, cx: &mut Context<Self>) {
        self.session_menu_open = true;
        self.session_menu_session_id = Some(session_id);
        cx.notify();
    }

    pub fn close_session_menu(&mut self, cx: &mut Context<Self>) {
        self.session_menu_open = false;
        self.session_menu_session_id = None;
        cx.notify();
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
        self.session_menu_open = false;

        let view = cx.entity().clone();

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

    // Session ordering methods
    pub fn move_session_to_section(
        &mut self,
        session_id: String,
        section_id: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let _ = self
            .session_store
            .move_session(&session_id, section_id.clone());
        self.layout_store
            .update_window(&self.layout_window_id, |layout| {
                if let Some(tab) = layout
                    .tabs
                    .iter_mut()
                    .find(|tab| tab.session_id == session_id)
                {
                    tab.section_id = section_id.clone();
                }
                if !layout.section_order.contains(&section_id) {
                    layout.section_order.push(section_id.clone());
                }
            });
        self.close_session_menu(cx);
        self.reload_from_store(cx);
        self.ensure_active_terminal(window, cx);
    }

    pub fn move_session_order(&mut self, session_id: String, delta: isize, cx: &mut Context<Self>) {
        let Some(session) = self.sessions.iter().find(|s| s.id == session_id).cloned() else {
            return;
        };
        let section_id = session.section_id.clone();
        let Some(window_layout) = self.window_layout() else {
            return;
        };

        let mut ordered: Vec<String> = window_layout
            .tabs
            .iter()
            .filter(|tab| tab.section_id == section_id)
            .map(|tab| tab.session_id.clone())
            .collect();

        let idx = ordered.iter().position(|id| id == &session_id);
        let Some(idx) = idx else {
            return;
        };
        let new_idx = (idx as isize + delta).clamp(0, ordered.len().saturating_sub(1) as isize);
        if new_idx as usize == idx {
            return;
        }

        let item = ordered.remove(idx);
        ordered.insert(new_idx as usize, item);

        self.layout_store
            .update_window(&self.layout_window_id, |layout| {
                for (index, id) in ordered.iter().enumerate() {
                    if let Some(tab) = layout.tabs.iter_mut().find(|tab| tab.session_id == *id) {
                        tab.order = index as u32;
                    }
                }
            });

        self.reload_from_store(cx);
    }

    // MCP Manager dialog
    pub fn open_mcp_manager(
        &mut self,
        _: &ToggleMcpManager,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.session_menu_open = false;

        let tokio = self.tokio.clone();
        let mcp_manager = self.mcp_manager.clone();

        let session_title = self
            .active_session()
            .map(|s| s.title.clone())
            .unwrap_or_default();
        let project_path = self.active_session().and_then(|s| {
            if s.project_path.is_empty() {
                None
            } else {
                Some(s.project_path.clone())
            }
        });

        let dialog_entity = cx.new(|_cx| {
            let mut dialog = McpManagerDialog::new(tokio, mcp_manager, session_title, project_path);
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
        self.session_menu_open = false;

        let view = cx.entity().clone();
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
        self.close_session(action.0.clone(), window, cx);
    }

    pub fn handle_restart_session(
        &mut self,
        action: &RestartSessionAction,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.restart_session(action.0.clone(), window, cx);
    }

    pub fn handle_edit_section(
        &mut self,
        action: &EditSection,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.open_project_editor(action.0.clone(), window, cx);
    }

    pub fn handle_remove_section(
        &mut self,
        action: &RemoveSection,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let section_id = action.0.clone();

        // Prevent removing the default section
        if section_id == agentterm_session::DEFAULT_SECTION_ID {
            return;
        }

        // Find the section
        let Some(section) = self
            .sections
            .iter()
            .find(|s| s.section.id == section_id)
            .map(|s| s.section.clone())
        else {
            return;
        };

        // Count sessions in this section
        let session_count = self
            .sessions
            .iter()
            .filter(|s| s.section_id == section_id)
            .count();

        // Get session titles for display (max 5)
        let session_titles: Vec<String> = self
            .sessions
            .iter()
            .filter(|s| s.section_id == section_id)
            .take(5)
            .map(|s| s.title.clone())
            .collect();

        let view = cx.entity().clone();
        let section_id_for_delete = section_id.clone();
        let section_name = section.name.clone();

        window.open_dialog(cx, move |dialog, _window, cx| {
            let mut content = v_flex().gap(px(12.));

            // Confirmation message
            content = content.child(div().text_sm().child(format!(
                "Are you sure you want to remove \"{}\"?",
                section_name
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
                                        .child(format!("This project has {}", tabs_text)),
                                )
                                .child(
                                    div()
                                        .text_sm()
                                        .text_color(cx.theme().muted_foreground)
                                        .child("These tabs will be moved to the Default section."),
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
                .title("Remove Project")
                .w(px(400.))
                .child(content)
                .footer({
                    let view = view.clone();
                    let section_id = section_id_for_delete.clone();
                    move |_ok, cancel, window, cx| {
                        vec![
                            cancel(window, cx),
                            Button::new("remove")
                                .danger()
                                .label("Remove")
                                .on_click({
                                    let view = view.clone();
                                    let section_id = section_id.clone();
                                    move |_, window, cx| {
                                        window.close_dialog(cx);
                                        view.update(cx, |app, cx| {
                                            let _ = app.session_store.delete_section(&section_id);
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

    /// Handles the MoveSectionToWindow action.
    pub fn handle_move_section_to_window(
        &mut self,
        action: &MoveSectionToWindow,
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

        self.move_section_to_window(action.section_id.clone(), target_window, window, cx);
    }

    pub fn move_section_to_window(
        &mut self,
        section_id: String,
        target_window: AnyWindowHandle,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(window_layout) = self.window_layout() else {
            return;
        };

        let mut moved_tabs: Vec<String> = window_layout
            .tabs
            .iter()
            .filter(|tab| tab.section_id == section_id)
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
                layout.tabs.retain(|tab| tab.section_id != section_id);
                layout.section_order.retain(|id| id != &section_id);
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

        let section_id_for_target = section_id.clone();
        let _ = cx.update_window(target_window, move |_root, window, cx| {
            if let Some(weak_app) = WindowRegistry::global().get_app(&target_window) {
                if let Some(app) = weak_app.upgrade() {
                    app.update(cx, |app, cx| {
                        app.layout_store
                            .update_window(&app.layout_window_id, |layout| {
                                if !layout.section_order.contains(&section_id_for_target) {
                                    layout.section_order.push(section_id_for_target.clone());
                                }
                                for session_id in &moved_tabs {
                                    layout.append_tab(
                                        session_id.clone(),
                                        section_id_for_target.clone(),
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
        let section_id = self
            .sessions
            .iter()
            .find(|s| s.id == session_id)
            .map(|s| s.section_id.clone())
            .unwrap_or_else(|| DEFAULT_SECTION_ID.to_string());

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
        let section_id_for_target = section_id.clone();
        let _ = cx.update_window(target_window, move |_root, window, cx| {
            if let Some(weak_app) = WindowRegistry::global().get_app(&target_window) {
                if let Some(app) = weak_app.upgrade() {
                    app.update(cx, |app, cx| {
                        app.layout_store
                            .update_window(&app.layout_window_id, |layout| {
                                if !layout.section_order.contains(&section_id_for_target) {
                                    layout.section_order.push(section_id_for_target.clone());
                                }
                                layout.append_tab(
                                    session_id_for_target.clone(),
                                    section_id_for_target.clone(),
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
        let section_id = self
            .sessions
            .iter()
            .find(|s| s.id == session_id)
            .map(|s| s.section_id.clone())
            .unwrap_or_else(|| DEFAULT_SECTION_ID.to_string());

        self.terminal_views.remove(&session_id);
        self.layout_store
            .update_window(&self.layout_window_id, |window_layout| {
                window_layout.remove_tab(&session_id);
                if window_layout.active_session_id.as_deref() == Some(&session_id) {
                    window_layout.active_session_id =
                        window_layout.tabs.first().map(|tab| tab.session_id.clone());
                }
            });

        let _ = create_new_window_with_session(session_id.clone(), section_id, cx);

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
                    if !layout.section_order.contains(&tab.section_id) {
                        layout.section_order.push(tab.section_id.clone());
                    }
                    layout.append_tab(tab.session_id.clone(), tab.section_id.clone());
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
                                if !layout.section_order.contains(&tab.section_id) {
                                    layout.section_order.push(tab.section_id.clone());
                                }
                                layout.append_tab(tab.session_id.clone(), tab.section_id.clone());
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

    // Workspace management handlers
    pub fn save_workspace(&mut self, _: &SaveWorkspace, window: &mut Window, cx: &mut Context<Self>) {
        let layout_store = self.layout_store.clone();

        let name_input = cx.new(|cx| {
            GpuiInputState::new(window, cx).placeholder("Workspace name")
        });
        let name_focus = name_input.read(cx).focus_handle(cx);

        window.open_dialog(cx, move |dialog, _window, _cx| {
            let name_input_for_content = name_input.clone();
            let name_input_for_footer = name_input.clone();
            let layout_store_for_save = layout_store.clone();

            dialog
                .title("Save Workspace")
                .w(px(400.))
                .child(
                    v_flex()
                        .gap_2()
                        .child(
                            div()
                                .text_sm()
                                .text_color(gpui::black().opacity(0.6))
                                .child("Enter a name for this workspace:"),
                        )
                        .child(GpuiInput::new(&name_input_for_content).cleanable(true)),
                )
                .footer(move |_ok, cancel, window, cx| {
                    let name_input = name_input_for_footer.clone();
                    let layout_store = layout_store_for_save.clone();
                    vec![
                        cancel(window, cx),
                        Button::new("save")
                            .primary()
                            .label("Save")
                            .on_click(move |_, window, cx| {
                                let name = name_input.read(cx).text().to_string();
                                if name.trim().is_empty() {
                                    return;
                                }
                                let current_session = layout_store.current_session();
                                if let Err(e) = layout_store.save_workspace(name, current_session) {
                                    eprintln!("Failed to save workspace: {}", e);
                                }
                                window.close_dialog(cx);
                            })
                            .into_any_element(),
                    ]
                })
        });

        name_focus.focus(window, cx);
    }
}
