//! Sidebar rendering and interaction methods.

use agentterm_session::{DEFAULT_SECTION_ID, SessionRecord};
use gpui::{
    BoxShadow, ClickEvent, Context, IntoElement, MouseButton, MouseDownEvent, MouseMoveEvent,
    MouseUpEvent, ParentElement, Pixels, Styled, Window, div, hsla, point, prelude::*, px, rgba,
};
use gpui_component::TITLE_BAR_HEIGHT;

use crate::icons::{Icon, IconName, IconSize, icon_from_string};
use crate::ui::{
    ActiveTheme, Button, ButtonVariants, ContextMenuExt, Divider, SectionItem, v_flex,
};

use super::actions::*;
use super::constants::*;
use super::state::AgentTermApp;

impl AgentTermApp {
    pub fn sidebar_shadow() -> Vec<BoxShadow> {
        vec![
            BoxShadow {
                // subtle near-edge shadow for elevation
                color: hsla(0., 0., 0., 0.18),
                offset: point(px(0.0), px(1.0)),
                blur_radius: px(6.0),
                spread_radius: px(0.0),
            },
            BoxShadow {
                color: hsla(0., 0., 0., 0.22),
                offset: point(px(0.0), px(8.0)),
                blur_radius: px(22.0),
                spread_radius: px(0.0),
            },
            BoxShadow {
                color: hsla(0., 0., 0., 0.18),
                offset: point(px(0.0), px(22.0)),
                blur_radius: px(54.0),
                spread_radius: px(0.0),
            },
        ]
    }

    pub fn render_sidebar_shell(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let resizer_hover_bg = cx.theme().foreground.alpha(0.20);
        // Apply window_transparency to sidebar background alpha with exponential curve
        // Higher transparency = lower alpha (more see-through), accelerating at higher values
        let exp_factor = (1.0 - self.settings.window_transparency).powf(2.0);
        // Clamp to minimum 15% opacity so sidebar is never fully transparent
        let bg_alpha = (SIDEBAR_GLASS_BASE_ALPHA * exp_factor).max(0.15);
        let base = rgba(rgba_u32(SURFACE_SIDEBAR, bg_alpha));

        div()
            .id("sidebar-shell")
            .absolute()
            .left(px(SIDEBAR_INSET))
            .top(px(SIDEBAR_INSET))
            .bottom(px(SIDEBAR_INSET))
            .w(px(self.sidebar_width))
            .child(
                div()
                    .id("sidebar-wrapper")
                    .size_full()
                    .rounded(px(16.0))
                    .overflow_hidden()
                    .border_1()
                    .border_color(rgba(rgba_u32(BORDER_SOFT, BORDER_SOFT_ALPHA)))
                    .bg(base)
                    .shadow(Self::sidebar_shadow())
                    .child(
                        div()
                            .id("sidebar-glass")
                            .size_full()
                            .relative()
                            .child(self.render_sidebar_content(cx)),
                    ),
            )
            .child(
                div()
                    .id("sidebar-resizer")
                    .absolute()
                    .top_0()
                    .bottom_0()
                    .left(px(self.sidebar_width - 3.0))
                    .w(px(6.0))
                    .rounded(px(999.0))
                    .bg(gpui::transparent_black())
                    .cursor_col_resize()
                    .hover(move |s| s.bg(resizer_hover_bg))
                    .on_mouse_down(MouseButton::Left, cx.listener(Self::start_sidebar_resize))
                    .on_mouse_up(MouseButton::Left, cx.listener(Self::stop_sidebar_resize)),
            )
    }

    pub fn render_sidebar_content(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let top_offset_for_title_bar = TITLE_BAR_HEIGHT - px(SIDEBAR_INSET);
        div()
            .id("sidebar-content")
            .size_full()
            .flex()
            .flex_col()
            .when(cfg!(not(target_os = "macos")), |el| {
                el.child(div().h(top_offset_for_title_bar).flex_shrink_0())
            })
            .child(self.render_sidebar_header(cx))
            .child(self.render_add_project(cx))
            .child(self.render_sections_list(cx))
    }

    pub fn render_sidebar_header(&self, cx: &mut Context<Self>) -> impl IntoElement {
        div()
            .h(px(44.0))
            .pl(px(SIDEBAR_HEADER_LEFT_PADDING))
            .pr(px(12.0))
            .flex()
            .items_center()
            .justify_between()
            .border_b_1()
            .border_color(rgba(rgba_u32(BORDER_SOFT, BORDER_SOFT_ALPHA)))
            .child(
                div()
                    .text_sm()
                    .font_weight(gpui::FontWeight::SEMIBOLD)
                    .text_color(cx.theme().foreground)
                    .child("AGENT TERM"),
            )
            .child(
                div()
                    .flex()
                    .gap(px(10.0))
                    .child(
                        Button::new("sidebar-new-tab")
                            .label("T")
                            .ghost()
                            .compact()
                            .on_click(cx.listener(|this, _: &ClickEvent, window, cx| {
                                this.new_shell_tab(&NewShellTab, window, cx);
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
                .on_click(cx.listener(|this, _: &ClickEvent, _window, cx| {
                    let name = "New Project".to_string();
                    let path = String::new();
                    let _ = this.session_store.create_section(name, path);
                    this.reload_from_store(cx);
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
        for section in &self.sections {
            list = list.child(self.render_section(section, cx));
        }
        list
    }

    pub fn render_section(
        &self,
        section: &SectionItem,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let sessions: Vec<&SessionRecord> = self
            .sessions
            .iter()
            .filter(|s| s.section_id == section.section.id)
            .collect();

        let section_id = section.section.id.clone();
        let is_collapsed = section.section.collapsed;
        let section_icon = section.section.icon.clone();

        let hover_bg = cx.theme().list_hover;
        let section_header = div()
            .id(format!("section-header-{}", section.section.id))
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
                .color(cx.theme().muted_foreground),
            )
            .child(
                section_icon
                    .as_ref()
                    .map(|s| icon_from_string(s))
                    .unwrap_or_else(|| Icon::new(IconName::Folder))
                    .size(IconSize::Medium)
                    .color(cx.theme().foreground),
            )
            .child(
                div()
                    .text_sm()
                    .font_weight(gpui::FontWeight::MEDIUM)
                    .text_color(cx.theme().foreground)
                    .flex_1()
                    .child(section.section.name.clone()),
            )
            .context_menu({
                let section_id = section_id.clone();
                move |menu, _window, _cx| {
                    menu.menu("Edit Project...", Box::new(EditSection(section_id.clone())))
                        .separator()
                        .menu(
                            "Remove Project",
                            Box::new(RemoveSection(section_id.clone())),
                        )
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
                    .text_color(cx.theme().muted_foreground)
                    .child("No terminals"),
            );
            return container;
        }

        for session in sessions {
            container = container.child(self.render_session_row(session, cx));
        }

        container
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
        let active_bg = cx.theme().list_hover;
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
            .when(is_active, move |s| s.bg(active_bg))
            .hover(move |s| s.bg(hover_bg))
            .child(
                session_icon
                    .as_ref()
                    .map(|s| icon_from_string(s))
                    .unwrap_or_else(|| Icon::new(IconName::Terminal))
                    .size(IconSize::Small)
                    .color(cx.theme().muted_foreground),
            )
            .child(
                div()
                    .text_sm()
                    .text_color(cx.theme().foreground)
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
                move |menu, _window, _cx| {
                    menu.menu("Edit Tab...", Box::new(RenameSession(session_id.clone())))
                        .menu(
                            "Restart",
                            Box::new(RestartSessionAction(session_id.clone())),
                        )
                        .separator()
                        .menu("Close", Box::new(CloseSessionAction(session_id.clone())))
                }
            })
    }

    // Sidebar resize methods
    pub fn start_sidebar_resize(
        &mut self,
        event: &MouseDownEvent,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.resizing_sidebar = true;
        self.resize_start_x = event.position.x;
        self.resize_start_width = self.sidebar_width;
        cx.notify();
    }

    pub fn stop_sidebar_resize(
        &mut self,
        _event: &MouseUpEvent,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.resizing_sidebar {
            self.resizing_sidebar = false;
            cx.notify();
        }
    }

    pub fn update_sidebar_resize(
        &mut self,
        event: &MouseMoveEvent,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if !self.resizing_sidebar || !event.dragging() {
            return;
        }

        let delta = event.position.x - self.resize_start_x;
        let next_width =
            (self.resize_start_width + delta / px(1.0)).clamp(SIDEBAR_MIN_WIDTH, SIDEBAR_MAX_WIDTH);
        if (next_width - self.sidebar_width).abs() > 0.1 {
            self.sidebar_width = next_width;
            cx.notify();
        }
    }

    // Section management methods
    pub fn toggle_section_collapsed(&mut self, section_id: String, cx: &mut Context<Self>) {
        if section_id == DEFAULT_SECTION_ID {
            return;
        }
        let Some(section) = self
            .sections
            .iter()
            .find(|s| s.section.id == section_id)
            .map(|s| s.section.clone())
        else {
            return;
        };
        let next = !section.collapsed;
        let _ = self.session_store.set_section_collapsed(&section_id, next);
        self.reload_from_store(cx);
    }

    pub fn move_section(&mut self, section_id: String, delta: isize, cx: &mut Context<Self>) {
        let mut ordered: Vec<agentterm_session::SectionRecord> = self
            .sections
            .iter()
            .filter(|s| s.section.id != DEFAULT_SECTION_ID)
            .map(|s| s.section.clone())
            .collect();
        ordered.sort_by_key(|s| s.order);

        let idx = ordered.iter().position(|s| s.id == section_id);
        let Some(idx) = idx else { return };
        let new_idx = (idx as isize + delta).clamp(0, ordered.len().saturating_sub(1) as isize);
        if new_idx as usize == idx {
            return;
        }
        let item = ordered.remove(idx);
        ordered.insert(new_idx as usize, item);
        let ids: Vec<String> = ordered.into_iter().map(|s| s.id).collect();
        let _ = self.session_store.reorder_sections(&ids);
        self.reload_from_store(cx);
    }
}
