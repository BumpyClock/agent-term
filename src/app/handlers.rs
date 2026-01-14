//! Action handlers for AgentTermApp.

use std::collections::HashSet;

use gpui::{
    div, prelude::*, px, size, AnyWindowHandle, Bounds, Context, Focusable, IntoElement,
    ParentElement, Styled, Window, WindowBounds, WindowOptions,
};

use super::window_registry::WindowRegistry;
use super::{create_new_window, MoveSessionToWindow, OpenSessionInNewWindow};
use gpui_component::input::InputState as GpuiInputState;

use crate::dialogs::{
    AddProjectDialog, McpManagerDialog, ProjectEditorDialog, SessionEditorDialog, TabPickerDialog,
};
use crate::settings_dialog::SettingsDialog;
use crate::ui::{v_flex, ActiveTheme, Button, ButtonVariants, Sizable, WindowExt};

use super::actions::*;
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
        let _ = self.session_store.move_session(&session_id, section_id);
        self.close_session_menu(cx);
        self.reload_from_store(cx);
        self.ensure_active_terminal(window, cx);
    }

    pub fn move_session_order(&mut self, session_id: String, delta: isize, cx: &mut Context<Self>) {
        let Some(session) = self.sessions.iter().find(|s| s.id == session_id).cloned() else {
            return;
        };
        let section_id = session.section_id.clone();

        let mut ordered: Vec<agentterm_session::SessionRecord> = self
            .sessions
            .iter()
            .filter(|s| s.section_id == section_id)
            .cloned()
            .collect();
        ordered.sort_by(|a, b| {
            a.tab_order
                .unwrap_or(u32::MAX)
                .cmp(&b.tab_order.unwrap_or(u32::MAX))
                .then_with(|| a.created_at.cmp(&b.created_at))
        });

        let idx = ordered.iter().position(|s| s.id == session_id);
        let Some(idx) = idx else { return };
        let new_idx = (idx as isize + delta).clamp(0, ordered.len().saturating_sub(1) as isize);
        if new_idx as usize == idx {
            return;
        }

        let item = ordered.remove(idx);
        ordered.insert(new_idx as usize, item);
        let ids: Vec<String> = ordered.into_iter().map(|s| s.id).collect();
        let _ = self
            .session_store
            .reorder_sessions_in_section(&section_id, &ids);
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

    /// Moves a session's view to another window.
    /// The terminal stays running in the global pool.
    pub fn move_session_to_window(
        &mut self,
        session_id: String,
        target_window: AnyWindowHandle,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.terminal_views.remove(&session_id);

        if self.active_session_id.as_deref() == Some(&session_id) {
            self.active_session_id = self
                .sessions
                .iter()
                .find(|s| s.id != session_id)
                .map(|s| s.id.clone());
            self.ensure_active_terminal(window, cx);
        }

        let session_id_for_target = session_id.clone();
        let _ = cx.update_window(target_window, move |_root, window, cx| {
            if let Some(weak_app) = WindowRegistry::global().get_app(&target_window) {
                if let Some(app) = weak_app.upgrade() {
                    app.update(cx, |app, cx| {
                        app.set_active_session_id(session_id_for_target, window, cx);
                    });
                }
            }
        });

        cx.notify();
    }

    /// Opens a session in a new window.
    /// Creates the new window and moves the session there.
    pub fn open_session_in_new_window(
        &mut self,
        session_id: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.terminal_views.remove(&session_id);

        if let Some(new_window) = create_new_window(cx) {
            let session_id_for_target = session_id.clone();
            let _ = cx.update_window(new_window, move |_root, window, cx| {
                if let Some(weak_app) = WindowRegistry::global().get_app(&new_window) {
                    if let Some(app) = weak_app.upgrade() {
                        app.update(cx, |app, cx| {
                            app.set_active_session_id(session_id_for_target, window, cx);
                        });
                    }
                }
            });
        }

        if self.active_session_id.as_deref() == Some(&session_id) {
            self.active_session_id = self
                .sessions
                .iter()
                .find(|s| s.id != session_id)
                .map(|s| s.id.clone());
            self.ensure_active_terminal(window, cx);
        }

        cx.notify();
    }
}
