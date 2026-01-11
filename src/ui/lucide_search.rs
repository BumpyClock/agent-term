//! Lucide icon search modal.
//!
//! A full-screen modal for searching and selecting from all Lucide icons.

use std::rc::Rc;

use gpui::{
    App, Context, Entity, FocusHandle, Focusable, InteractiveElement, IntoElement, MouseButton,
    ParentElement, Render, SharedString, StatefulInteractiveElement, Styled, Window, div,
    prelude::*, px,
};
use gpui_component::theme::ThemeMode;

use crate::icons::{Icon, IconDescriptor, IconName, IconSize, search_lucide_icons};
use crate::text_input::TextInput;
use crate::theme::surface_background;
use crate::ui::ActiveTheme;

/// Number of icons to show per page
const ICONS_PER_PAGE: usize = 100;

#[derive(Clone, Copy)]
struct LucideSearchPalette {
    overlay: gpui::Hsla,
    modal_bg: gpui::Hsla,
    modal_border: gpui::Hsla,
    header_bg: gpui::Hsla,
    icon_button_bg: gpui::Hsla,
    icon_button_hover: gpui::Hsla,
    icon_button_selected: gpui::Hsla,
    text_primary: gpui::Hsla,
    text_muted: gpui::Hsla,
    selected_text: gpui::Hsla,
}

fn lucide_search_palette(cx: &App) -> LucideSearchPalette {
    let mode = if cx.theme().is_dark() {
        ThemeMode::Dark
    } else {
        ThemeMode::Light
    };
    LucideSearchPalette {
        overlay: cx.theme().overlay,
        modal_bg: surface_background(mode),
        modal_border: cx.theme().border,
        header_bg: cx.theme().secondary,
        icon_button_bg: cx.theme().muted,
        icon_button_hover: cx.theme().list_hover,
        icon_button_selected: cx.theme().primary,
        text_primary: cx.theme().foreground,
        text_muted: cx.theme().muted_foreground,
        selected_text: cx.theme().primary_foreground,
    }
}

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
        let palette = lucide_search_palette(cx);

        div()
            .id("lucide-search-overlay")
            .absolute()
            .inset_0()
            .bg(palette.overlay)
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
                    .bg(palette.modal_bg)
                    .border_1()
                    .border_color(palette.modal_border)
                    .rounded(px(8.))
                    .flex()
                    .flex_col()
                    .overflow_hidden()
                    .on_mouse_down(MouseButton::Left, |_, _, cx| {
                        cx.stop_propagation();
                    })
                    .child(self.render_header(cx, palette))
                    .child(self.render_icon_grid(
                        visible_icons.as_slice(),
                        &current_value,
                        has_more,
                        remaining,
                        &search_query,
                        palette,
                        cx,
                    ))
                    .child(self.render_footer(total_count, palette)),
            )
    }
}

impl LucideSearchModal {
    fn render_header(&self, cx: &Context<Self>, palette: LucideSearchPalette) -> impl IntoElement {
        div()
            .px(px(16.))
            .py(px(12.))
            .bg(palette.header_bg)
            .border_b_1()
            .border_color(palette.modal_border)
            .flex()
            .items_center()
            .gap(px(12.))
            .child(
                div()
                    .text_lg()
                    .font_weight(gpui::FontWeight::SEMIBOLD)
                    .text_color(palette.text_primary)
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
                    .hover(|s| s.bg(palette.icon_button_hover))
                    .on_mouse_down(
                        MouseButton::Left,
                        cx.listener(|this, _, window, cx| {
                            this.close(window, cx);
                        }),
                    )
                    .child(
                        Icon::new(IconName::X)
                            .size(IconSize::Medium)
                            .color(palette.text_muted),
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
        palette: LucideSearchPalette,
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
                                palette.icon_button_selected
                            } else {
                                palette.icon_button_bg
                            })
                            .hover(|s| {
                                if !is_selected {
                                    s.bg(palette.icon_button_hover)
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
                                    palette.selected_text
                                } else {
                                    palette.text_primary
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
                                .bg(palette.icon_button_bg)
                                .text_color(palette.text_muted)
                                .text_sm()
                                .hover(|s| {
                                    s.bg(palette.icon_button_hover)
                                        .text_color(palette.text_primary)
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

    fn render_footer(&self, total_count: usize, palette: LucideSearchPalette) -> impl IntoElement {
        div()
            .px(px(16.))
            .py(px(8.))
            .border_t_1()
            .border_color(palette.modal_border)
            .text_xs()
            .text_color(palette.text_muted)
            .child(format!("{} icons available", total_count))
    }
}
