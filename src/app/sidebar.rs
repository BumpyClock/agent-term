//! Sidebar rendering and interaction methods.

use agentterm_session::{DEFAULT_SECTION_ID, SessionRecord, SessionTool};
use agentterm_tools::ShellType;
use gpui::{
    AnyWindowHandle, ClickEvent, Context, Corner, Entity, IntoElement, MouseMoveEvent,
    MouseUpEvent, ParentElement, Styled, Window, div, prelude::*, px,
};

use super::window_registry::WindowRegistry;
use gpui_component::{SidebarShell, TITLE_BAR_HEIGHT};

use super::constants::{SIDEBAR_MAX_WIDTH, SIDEBAR_MIN_WIDTH};

use crate::icons::{Icon, IconName, IconSize, icon_from_string};
use crate::ui::{
    ActiveTheme, Button, ButtonVariants, ContextMenuExt, DropdownMenu, PopupMenu, PopupMenuItem,
    SectionItem,
};

use super::actions::*;
use super::constants::*;
use super::state::AgentTermApp;

impl AgentTermApp {
    pub fn render_sidebar_shell(
        &self,
        _window: &Window,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let entity = cx.entity().clone();
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
        if !self.resizing_sidebar {
            return;
        }

        let delta_x = event.position.x - self.resize_start_x;
        let new_width = (self.resize_start_width + delta_x / px(1.0))
            .clamp(SIDEBAR_MIN_WIDTH, SIDEBAR_MAX_WIDTH);

        self.sidebar_width = new_width;
        cx.notify();
    }

    /// Handler for mouse up events to stop sidebar resize.
    /// Should be attached at the root level.
    pub fn stop_sidebar_resize(
        &mut self,
        _event: &MouseUpEvent,
        _window: &mut Window,
        _cx: &mut Context<Self>,
    ) {
        self.resizing_sidebar = false;
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
        div()
            .id("sidebar-content")
            .size_full()
            .flex()
            .flex_col()
            .child(div().h(top_offset).flex_shrink_0())
            .child(self.render_sidebar_header(cx))
            .child(self.render_add_project(cx))
            .child(self.render_sections_list(cx))
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

    pub fn render_add_project(&self, cx: &mut Context<Self>) -> impl IntoElement {
        div().px(px(16.0)).py(px(12.0)).child(
            div()
                .id("sidebar-add-project")
                .text_sm()
                .text_color(cx.theme().muted_foreground)
                .cursor_pointer()
                .hover(|s| s.text_color(cx.theme().foreground))
                .on_click(cx.listener(|this, _: &ClickEvent, window, cx| {
                    this.open_add_project_dialog(window, cx);
                }))
                .child("+ Add Project"),
        )
    }

    pub fn render_sections_list(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let mut list = div()
            .id("sidebar-sections-scroll")
            .flex_1()
            .overflow_y_scroll()
            .px(px(8.0));
        for section in self.ordered_sections() {
            list = list.child(self.render_section(&section, cx));
        }
        list
    }

    pub fn render_section(
        &self,
        section: &SectionItem,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let sessions: Vec<&SessionRecord> = self.ordered_sessions_for_section(&section.section.id);

        let section_id = section.section.id.clone();
        let is_collapsed = self.is_section_collapsed(&section_id);
        let section_icon = section.section.icon.clone();
        let is_default = section.is_default;
        let group_id = format!("section-group-{}", section.section.id);

        // Collect theme colors before building the add button
        let hover_bg = cx.theme().list_hover;
        let muted_fg = cx.theme().muted_foreground;
        let foreground = cx.theme().foreground;

        // Clone data needed for the add button dropdown
        let view = cx.entity().clone();
        let shells = self.cached_shells.clone();
        let tools = self.cached_tools.clone();
        let pinned_ids = self.cached_pinned_shell_ids.clone();
        let section_id_for_menu = section_id.clone();

        let section_header = div()
            .id(format!("section-header-{}", section.section.id))
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
                let section_id = section.section.id.clone();
                move |this, _, _, cx| {
                    this.toggle_section_collapsed(section_id.clone(), cx);
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
                    section_icon
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
                    .child(section.section.name.clone()),
            )
            .child(
                div()
                    .invisible()
                    .group_hover(group_id.clone(), |this| this.visible())
                    .child(
                        Button::new(format!("section-add-{}", section_id))
                            .label("+")
                            .ghost()
                            .compact()
                            .dropdown_menu_with_anchor(
                                Corner::TopRight,
                                move |menu, _window, _cx| {
                                    Self::build_add_menu(
                                        menu,
                                        section_id_for_menu.clone(),
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
                let section_id = section_id.clone();
                let is_default = is_default;
                move |menu, window, cx| {
                    if is_default {
                        return menu;
                    }
                    let current_handle: AnyWindowHandle = window.window_handle().into();
                    let other_windows = WindowRegistry::global().list_other_windows(current_handle);

                    let mut menu =
                        menu.menu("Edit Project...", Box::new(EditSection(section_id.clone())));

                    if !other_windows.is_empty() {
                        menu = menu.submenu("Move Project to Window", window, cx, {
                            let section_id = section_id.clone();
                            let other_windows = other_windows.clone();
                            move |submenu, _window, _cx| {
                                let mut submenu = submenu;
                                for (_handle, info) in &other_windows {
                                    let section_id = section_id.clone();
                                    let window_id = info.number as u64;
                                    let title = info.title.clone();
                                    submenu = submenu.menu(
                                        &title,
                                        Box::new(MoveSectionToWindow {
                                            section_id,
                                            target_window_id: window_id,
                                        }),
                                    );
                                }
                                submenu
                            }
                        });
                    }

                    menu.separator()
                        .menu(
                            "Remove Project",
                            Box::new(RemoveSection(section_id.clone())),
                        )
                        .separator()
                        .menu("Save Workspace...", Box::new(SaveWorkspace))
                }
            });

        let mut container = div().py(px(4.0)).child(section_header);

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
            container = container.child(self.render_session_row(session, cx));
        }

        container
    }

    /// Build the popup menu items for adding a new tab to a section
    fn build_add_menu(
        menu: PopupMenu,
        section_id: String,
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
                let section_id = section_id.clone();
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
                            app.create_session_in_section(
                                section_id.clone(),
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

        // Add shells section
        if !native_shells.is_empty() {
            menu = menu.label("Shells");
            for shell in native_shells {
                let section_id = section_id.clone();
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
                            app.create_session_in_section(
                                section_id.clone(),
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

        // Add tools section
        let has_builtin_tools = !builtin_tools.is_empty();
        let has_custom_tools = !custom_tools.is_empty();

        if has_builtin_tools || has_custom_tools {
            menu = menu.label("Tools");

            for tool in builtin_tools {
                let section_id = section_id.clone();
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
                            app.create_session_in_section(
                                section_id.clone(),
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
                let section_id = section_id.clone();
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
                            app.create_session_in_section(
                                section_id.clone(),
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

    pub fn render_session_row(
        &self,
        session: &SessionRecord,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let is_active = self
            .active_session_id
            .as_deref()
            .is_some_and(|id| id == session.id);
        let title = if session.title.is_empty() {
            "Terminal".to_string()
        } else {
            session.title.clone()
        };
        let session_id = session.id.clone();
        let session_icon = session.icon.clone();
        let active_bg = cx.theme().accent;
        let active_fg = cx.theme().accent_foreground;
        let hover_bg = cx.theme().list_active;

        div()
            .id(format!("session-row-{}", session.id))
            .px(px(8.0))
            .py(px(4.0))
            .flex()
            .items_center()
            .gap(px(6.0))
            .rounded(px(6.0))
            .cursor_pointer()
            .when(is_active, move |s| s.bg(active_bg).text_color(active_fg))
            .hover(move |s| s.bg(hover_bg))
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
                    .child(title.clone()),
            )
            .child({
                let id = session.id.clone();
                Button::new(format!("session-close-{}", session.id))
                    .label("×")
                    .ghost()
                    .compact()
                    .on_click(cx.listener(move |this, _: &ClickEvent, window, cx| {
                        cx.stop_propagation();
                        this.close_session(id.clone(), window, cx);
                    }))
            })
            .on_click(cx.listener({
                let id = session_id.clone();
                move |this, _: &ClickEvent, window, cx| {
                    this.set_active_session_id(id.clone(), window, cx);
                }
            }))
            .context_menu({
                let session_id = session_id.clone();
                move |menu, window, cx| {
                    let current_handle: AnyWindowHandle = window.window_handle().into();
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
                            let other_windows = other_windows.clone();
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
            })
    }

    // Section management methods
    pub fn toggle_section_collapsed(&mut self, section_id: String, cx: &mut Context<Self>) {
        if section_id == DEFAULT_SECTION_ID {
            return;
        }
        let next = !self.is_section_collapsed(&section_id);
        self.layout_store
            .update_window(&self.layout_window_id, |window| {
                if next {
                    if !window.collapsed_sections.contains(&section_id) {
                        window.collapsed_sections.push(section_id.clone());
                    }
                } else {
                    window.collapsed_sections.retain(|id| id != &section_id);
                }
            });
        cx.notify();
    }

    pub fn move_section(&mut self, section_id: String, delta: isize, cx: &mut Context<Self>) {
        let Some(window) = self.window_layout() else {
            return;
        };

        let mut ordered_ids = if window.section_order.is_empty() {
            self.sections.iter().map(|s| s.section.id.clone()).collect()
        } else {
            window.section_order
        };

        let mut moveable: Vec<String> = ordered_ids
            .iter()
            .filter(|id| *id != DEFAULT_SECTION_ID)
            .cloned()
            .collect();
        let idx = moveable.iter().position(|id| id == &section_id);
        let Some(idx) = idx else {
            return;
        };
        let new_idx = (idx as isize + delta).clamp(0, moveable.len().saturating_sub(1) as isize);
        if new_idx as usize == idx {
            return;
        }
        let item = moveable.remove(idx);
        moveable.insert(new_idx as usize, item);

        ordered_ids.retain(|id| id == DEFAULT_SECTION_ID);
        ordered_ids.extend(moveable);

        self.layout_store
            .update_window(&self.layout_window_id, |window| {
                window.section_order = ordered_ids;
            });
        cx.notify();
    }
}
