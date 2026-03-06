//! Lucide icon search dialog content.
//!
//! A searchable chooser for selecting from all Lucide icons.

use std::rc::Rc;

use gpui::{
    AnyElement, App, Context, Entity, FocusHandle, Focusable, InteractiveElement, IntoElement,
    ListSizingBehavior, MouseButton, ParentElement, Pixels, Render, SharedString,
    Size as GpuiSize, Styled, Window, div, prelude::*, px,
};
use gpui_component::{VirtualListScrollHandle, v_virtual_list};
use gpui_component::theme::ThemeMode;

use crate::icons::{Icon, IconDescriptor, IconName, IconSize, search_lucide_icons};
use crate::text_input::TextInput;
use crate::theme::surface_background;
use crate::ui::ActiveTheme;

const GRID_GAP: f32 = 4.0;
const ICON_BUTTON_SIZE: f32 = 40.0;
/// Columns per row, sized for the 720px dialog (720 - ~48px chrome/padding).
const GRID_COLUMNS: usize = 15;

#[derive(Clone, Copy)]
struct LucideSearchPalette {
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
    scroll_handle: VirtualListScrollHandle,
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
            scroll_handle: VirtualListScrollHandle::new(),
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
        let total_count = filtered_icons.len();
        let palette = lucide_search_palette(cx);

        div()
            .id("lucide-search-modal")
            .w_full()
            .h(px(500.))
            .bg(palette.modal_bg)
            .flex()
            .flex_col()
            .overflow_hidden()
            .child(self.render_header(cx, palette))
            .child(self.render_icon_grid(filtered_icons, palette, cx))
            .child(self.render_footer(total_count, palette))
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
        filtered_icons: Vec<&'static crate::icons::LucideIconMeta>,
        palette: LucideSearchPalette,
        cx: &Context<Self>,
    ) -> impl IntoElement {
        let total_icons = filtered_icons.len();
        let row_count = (total_icons + GRID_COLUMNS - 1) / GRID_COLUMNS;

        let row_sizes: Rc<Vec<GpuiSize<Pixels>>> = Rc::new(
            (0..row_count)
                .map(|_| GpuiSize {
                    width: px(0.),
                    height: px(ICON_BUTTON_SIZE),
                })
                .collect(),
        );

        div()
            .id("icon-grid-container")
            .w_full()
            .flex_1()
            .min_h(px(0.))
            .overflow_hidden()
            .child(
                v_virtual_list(
                    cx.entity(),
                    "lucide-search-grid",
                    row_sizes,
                    move |this, visible_range, _window, cx| {
                        let entity = cx.entity();
                        let current_value = this.current_value.clone();

                        visible_range
                            .map(|row_ix| {
                                let start = row_ix * GRID_COLUMNS;
                                let end = (start + GRID_COLUMNS).min(filtered_icons.len());
                                let current_value = current_value.clone();

                                render_icon_row(
                                    entity.clone(),
                                    &filtered_icons[start..end],
                                    current_value,
                                    palette,
                                )
                            })
                            .collect()
                    },
                )
                .track_scroll(&self.scroll_handle)
                .px(px(16.))
                .py(px(12.))
                .gap(px(GRID_GAP))
                .with_sizing_behavior(ListSizingBehavior::Auto),
            )
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

fn render_icon_row(
    entity: Entity<LucideSearchModal>,
    row_icons: &[&'static crate::icons::LucideIconMeta],
    current_value: Option<String>,
    palette: LucideSearchPalette,
) -> AnyElement {
    div()
        .flex()
        .items_center()
        .gap(px(GRID_GAP))
        .children(row_icons.iter().map(|icon| {
            let name = &icon.name;
            let is_selected = current_value.as_deref() == Some(name.as_str());
            let name_for_click = name.clone();
            let entity = entity.clone();

            div()
                .id(SharedString::from(format!("lucide-{}", name)))
                .size(px(ICON_BUTTON_SIZE))
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
                .on_mouse_down(MouseButton::Left, move |_, window, cx| {
                    entity.update(cx, |this, cx| {
                        this.select_icon(&name_for_click, window, cx);
                    });
                })
                .child(
                    Icon::lucide(name)
                        .size(IconSize::Medium)
                        .color(if is_selected {
                            palette.selected_text
                        } else {
                            palette.text_primary
                        }),
                )
        }))
        .into_any_element()
}
