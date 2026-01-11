//! Lucide icon search modal.
//!
//! A full-screen modal for searching and selecting from all Lucide icons.

use std::rc::Rc;

use gpui::{
    App, Context, Entity, FocusHandle, Focusable, InteractiveElement, IntoElement, MouseButton,
    ParentElement, Render, SharedString, StatefulInteractiveElement, Styled, Window, div,
    prelude::*, px, rgb, rgba,
};

use crate::icons::{Icon, IconDescriptor, IconName, IconSize, search_lucide_icons};
use crate::text_input::TextInput;

/// Colors for the search modal
const MODAL_BG: u32 = 0x1a1a1a;
const MODAL_BORDER: u32 = 0x3a3a3a;
const HEADER_BG: u32 = 0x222222;
const ICON_BUTTON_BG: u32 = 0x2a2a2a;
const ICON_BUTTON_HOVER: u32 = 0x3a3a3a;
const ICON_BUTTON_SELECTED: u32 = 0x5eead4;
const TEXT_PRIMARY: u32 = 0xd8d8d8;
const TEXT_MUTED: u32 = 0x808080;

/// Number of icons to show per page
const ICONS_PER_PAGE: usize = 100;

/// Lucide icon search modal state.
///
/// A modal dialog for searching and selecting from all available Lucide icons.
/// Supports filtering by name, pagination for performance, and keyboard/mouse
/// interaction for icon selection.
///
/// # Example
///
/// ```ignore
/// let modal = cx.new(|cx| {
///     let mut modal = LucideSearchModal::new(cx);
///     modal.set_on_select(|icon, window, cx| {
///         // Handle icon selection
///     });
///     modal.set_on_close(|window, cx| {
///         // Close modal
///     });
///     modal
/// });
/// ```
pub struct LucideSearchModal {
    focus_handle: FocusHandle,
    search_input: Entity<TextInput>,
    visible_count: usize,
    current_value: Option<String>,
    on_select: Option<Rc<dyn Fn(IconDescriptor, &mut Window, &mut App)>>,
    on_close: Option<Rc<dyn Fn(&mut Window, &mut App)>>,
}

impl LucideSearchModal {
    pub fn new(cx: &mut Context<Self>) -> Self {
        let search_input = cx.new(|cx| TextInput::new("Search icons...", "", cx));

        Self {
            focus_handle: cx.focus_handle(),
            search_input,
            visible_count: ICONS_PER_PAGE,
            current_value: None,
            on_select: None,
            on_close: None,
        }
    }

    pub fn set_current_value(&mut self, value: Option<String>) {
        self.current_value = value;
    }

    pub fn set_on_select(
        &mut self,
        callback: impl Fn(IconDescriptor, &mut Window, &mut App) + 'static,
    ) {
        self.on_select = Some(Rc::new(callback));
    }

    pub fn set_on_close(&mut self, callback: impl Fn(&mut Window, &mut App) + 'static) {
        self.on_close = Some(Rc::new(callback));
    }

    fn select_icon(&self, name: &str, window: &mut Window, cx: &mut App) {
        if let Some(on_select) = &self.on_select {
            on_select(IconDescriptor::lucide(name), window, cx);
        }
        self.close(window, cx);
    }

    fn close(&self, window: &mut Window, cx: &mut App) {
        if let Some(on_close) = &self.on_close {
            on_close(window, cx);
        }
    }

    fn load_more(&mut self, query: &str) {
        let filtered_count = search_lucide_icons(query).len();
        if self.visible_count < filtered_count {
            self.visible_count += ICONS_PER_PAGE;
        }
    }

    fn get_search_query(&self, cx: &App) -> String {
        self.search_input.read(cx).text()
    }
}

impl Focusable for LucideSearchModal {
    fn focus_handle(&self, _cx: &App) -> FocusHandle {
        self.focus_handle.clone()
    }
}

impl Render for LucideSearchModal {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let search_query = self.get_search_query(cx);
        let filtered_icons = search_lucide_icons(&search_query);
        let visible_icons: Vec<_> = filtered_icons.iter().take(self.visible_count).collect();
        let has_more = self.visible_count < filtered_icons.len();
        let current_value = self.current_value.clone();
        let remaining = filtered_icons.len().saturating_sub(self.visible_count);
        let total_count = filtered_icons.len();

        div()
            .id("lucide-search-overlay")
            .absolute()
            .inset_0()
            .bg(rgba(0x00000080))
            .flex()
            .items_center()
            .justify_center()
            .on_mouse_down(
                MouseButton::Left,
                cx.listener(|this, _, window, cx| {
                    this.close(window, cx);
                }),
            )
            .child(
                div()
                    .id("lucide-search-modal")
                    .w(px(600.))
                    .max_h(px(500.))
                    .bg(rgb(MODAL_BG))
                    .border_1()
                    .border_color(rgb(MODAL_BORDER))
                    .rounded(px(8.))
                    .flex()
                    .flex_col()
                    .overflow_hidden()
                    .on_mouse_down(MouseButton::Left, |_, _, cx| {
                        cx.stop_propagation();
                    })
                    .child(self.render_header(cx))
                    .child(self.render_icon_grid(
                        visible_icons.as_slice(),
                        &current_value,
                        has_more,
                        remaining,
                        &search_query,
                        cx,
                    ))
                    .child(self.render_footer(total_count)),
            )
    }
}

impl LucideSearchModal {
    fn render_header(&self, cx: &Context<Self>) -> impl IntoElement {
        div()
            .px(px(16.))
            .py(px(12.))
            .bg(rgb(HEADER_BG))
            .border_b_1()
            .border_color(rgb(MODAL_BORDER))
            .flex()
            .items_center()
            .gap(px(12.))
            .child(
                div()
                    .text_lg()
                    .font_weight(gpui::FontWeight::SEMIBOLD)
                    .text_color(rgb(TEXT_PRIMARY))
                    .child("Select Icon"),
            )
            .child(div().flex_1().child(self.search_input.clone()))
            .child(
                div()
                    .id("close-btn")
                    .size(px(24.))
                    .rounded(px(4.))
                    .cursor_pointer()
                    .flex()
                    .items_center()
                    .justify_center()
                    .hover(|s| s.bg(rgb(ICON_BUTTON_HOVER)))
                    .on_mouse_down(
                        MouseButton::Left,
                        cx.listener(|this, _, window, cx| {
                            this.close(window, cx);
                        }),
                    )
                    .child(
                        Icon::new(IconName::X)
                            .size(IconSize::Medium)
                            .color(rgb(TEXT_MUTED)),
                    ),
            )
    }

    fn render_icon_grid(
        &self,
        visible_icons: &[&&crate::icons::LucideIconMeta],
        current_value: &Option<String>,
        has_more: bool,
        remaining: usize,
        search_query: &str,
        cx: &Context<Self>,
    ) -> impl IntoElement {
        let query_for_load_more = search_query.to_string();

        div()
            .id("icon-grid-container")
            .flex_1()
            .overflow_y_scroll()
            .px(px(16.))
            .py(px(12.))
            .child(
                div()
                    .flex()
                    .flex_wrap()
                    .gap(px(4.))
                    .children(visible_icons.iter().map(|icon| {
                        let is_selected = current_value.as_ref() == Some(&icon.name);
                        let name = icon.name.clone();
                        let name_for_click = icon.name.clone();

                        div()
                            .id(SharedString::from(format!("lucide-{}", name)))
                            .size(px(40.))
                            .rounded(px(4.))
                            .cursor_pointer()
                            .flex()
                            .flex_col()
                            .items_center()
                            .justify_center()
                            .gap(px(2.))
                            .bg(if is_selected {
                                rgb(ICON_BUTTON_SELECTED)
                            } else {
                                rgb(ICON_BUTTON_BG)
                            })
                            .hover(|s| {
                                if !is_selected {
                                    s.bg(rgb(ICON_BUTTON_HOVER))
                                } else {
                                    s
                                }
                            })
                            .on_mouse_down(
                                MouseButton::Left,
                                cx.listener(move |this, _, window, cx| {
                                    this.select_icon(&name_for_click, window, cx);
                                }),
                            )
                            .child(Icon::lucide(&name).size(IconSize::Medium).color(
                                if is_selected {
                                    rgb(0x000000)
                                } else {
                                    rgb(TEXT_PRIMARY)
                                },
                            ))
                    })),
            )
            .when(has_more, |el| {
                el.child(
                    div()
                        .id("load-more")
                        .w_full()
                        .py(px(12.))
                        .flex()
                        .justify_center()
                        .child(
                            div()
                                .px(px(16.))
                                .py(px(8.))
                                .rounded(px(4.))
                                .cursor_pointer()
                                .bg(rgb(ICON_BUTTON_BG))
                                .text_color(rgb(TEXT_MUTED))
                                .text_sm()
                                .hover(|s| {
                                    s.bg(rgb(ICON_BUTTON_HOVER)).text_color(rgb(TEXT_PRIMARY))
                                })
                                .on_mouse_down(MouseButton::Left, {
                                    cx.listener(move |this, _, _, cx| {
                                        this.load_more(&query_for_load_more);
                                        cx.notify();
                                    })
                                })
                                .child(format!("Load more ({} remaining)", remaining)),
                        ),
                )
            })
    }

    fn render_footer(&self, total_count: usize) -> impl IntoElement {
        div()
            .px(px(16.))
            .py(px(8.))
            .border_t_1()
            .border_color(rgb(MODAL_BORDER))
            .text_xs()
            .text_color(rgb(TEXT_MUTED))
            .child(format!("{} icons available", total_count))
    }
}
