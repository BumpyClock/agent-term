//! Icon picker component for selecting icons.
//!
//! Displays a grid of tool icons and common Lucide icons,
//! with a "More icons..." button to open the full Lucide search.

use std::rc::Rc;

use gpui::{
    App, ElementId, InteractiveElement, IntoElement, MouseButton, ParentElement, Styled, Window,
    div, prelude::*, px,
};

use crate::icons::{Icon, IconDescriptor, IconSize, TOOL_ICONS};
use crate::ui::ActiveTheme;

/// Common Lucide icons shown in the picker (subset for quick access)
const COMMON_LUCIDE_ICONS: &[(&str, &str)] = &[
    ("terminal", "Terminal"),
    ("code", "Code"),
    ("sparkles", "Sparkles"),
    ("bot", "Bot"),
    ("cpu", "CPU"),
    ("zap", "Zap"),
    ("star", "Star"),
    ("folder", "Folder"),
    ("file", "File"),
    ("settings", "Settings"),
];

/// Icon picker component using RenderOnce pattern.
///
/// Displays a grid of tool icons and common Lucide icons with
/// callbacks for selection and opening the full search modal.
///
/// # Example
///
/// ```ignore
/// IconPicker::new("icon-picker")
///     .value(Some(IconDescriptor::lucide("terminal")))
///     .on_change(|icon, window, cx| {
///         // Handle icon selection
///     })
///     .on_open_search(|window, cx| {
///         // Open full Lucide search modal
///     })
/// ```
#[derive(IntoElement)]
pub struct IconPicker {
    id: ElementId,
    value: Option<IconDescriptor>,
    on_change: Option<Rc<dyn Fn(Option<IconDescriptor>, &mut Window, &mut App)>>,
    on_open_search: Option<Rc<dyn Fn(&mut Window, &mut App)>>,
}

#[derive(Clone, Copy)]
struct IconPickerPalette {
    section_title: gpui::Hsla,
    icon_button_bg: gpui::Hsla,
    icon_button_hover: gpui::Hsla,
    icon_button_selected: gpui::Hsla,
    default_button_bg: gpui::Hsla,
    text_primary: gpui::Hsla,
    text_muted: gpui::Hsla,
    selected_text: gpui::Hsla,
    icon_unselected: gpui::Hsla,
}

fn icon_picker_palette(cx: &App) -> IconPickerPalette {
    IconPickerPalette {
        section_title: cx.theme().muted_foreground,
        icon_button_bg: cx.theme().muted,
        icon_button_hover: cx.theme().list_hover,
        icon_button_selected: cx.theme().primary,
        default_button_bg: cx.theme().muted,
        text_primary: cx.theme().foreground,
        text_muted: cx.theme().muted_foreground,
        selected_text: cx.theme().primary_foreground,
        icon_unselected: cx.theme().foreground.alpha(0.8),
    }
}

impl IconPicker {
    pub fn new(id: impl Into<ElementId>) -> Self {
        Self {
            id: id.into(),
            value: None,
            on_change: None,
            on_open_search: None,
        }
    }

    pub fn value(mut self, value: Option<IconDescriptor>) -> Self {
        self.value = value;
        self
    }

    pub fn on_change(
        mut self,
        callback: impl Fn(Option<IconDescriptor>, &mut Window, &mut App) + 'static,
    ) -> Self {
        self.on_change = Some(Rc::new(callback));
        self
    }

    pub fn on_open_search(mut self, callback: impl Fn(&mut Window, &mut App) + 'static) -> Self {
        self.on_open_search = Some(Rc::new(callback));
        self
    }
}

impl RenderOnce for IconPicker {
    fn render(self, _window: &mut Window, cx: &mut App) -> impl IntoElement {
        let value = self.value.clone();
        let on_change = self.on_change.clone();
        let on_open_search = self.on_open_search.clone();
        let palette = icon_picker_palette(cx);

        div()
            .id(self.id)
            .flex()
            .flex_col()
            .gap(px(12.))
            .child(render_default_button(&value, on_change.clone(), palette))
            .child(render_section("Tool icons", palette))
            .child(render_tool_icons_grid(&value, on_change.clone(), palette))
            .child(render_section("Icons", palette))
            .child(render_lucide_icons_grid(&value, on_change, palette))
            .child(render_more_button(on_open_search, palette))
    }
}

fn render_section(title: &str, palette: IconPickerPalette) -> impl IntoElement {
    div()
        .text_xs()
        .text_color(palette.section_title)
        .font_weight(gpui::FontWeight::MEDIUM)
        .child(title.to_string())
}

fn render_default_button(
    value: &Option<IconDescriptor>,
    on_change: Option<Rc<dyn Fn(Option<IconDescriptor>, &mut Window, &mut App)>>,
    palette: IconPickerPalette,
) -> impl IntoElement {
    let is_selected = value.is_none();

    div()
        .id("icon-default")
        .px(px(12.))
        .py(px(6.))
        .rounded(px(4.))
        .cursor_pointer()
        .bg(if is_selected {
            palette.icon_button_selected
        } else {
            palette.default_button_bg
        })
        .text_color(if is_selected {
            palette.selected_text
        } else {
            palette.text_primary
        })
        .text_sm()
        .hover(|s| {
            if !is_selected {
                s.bg(palette.icon_button_hover)
            } else {
                s
            }
        })
        .when_some(on_change, |el, callback| {
            el.on_mouse_down(MouseButton::Left, move |_, window, cx| {
                callback(None, window, cx);
            })
        })
        .child("Default")
}

fn render_tool_icons_grid(
    value: &Option<IconDescriptor>,
    on_change: Option<Rc<dyn Fn(Option<IconDescriptor>, &mut Window, &mut App)>>,
    palette: IconPickerPalette,
) -> impl IntoElement {
    div()
        .flex()
        .flex_wrap()
        .gap(px(4.))
        .children(TOOL_ICONS.iter().take(12).enumerate().map(|(idx, info)| {
            render_icon_button(
                format!("tool-{}", idx),
                IconDescriptor::tool(info.id),
                Icon::tool(info.id),
                value,
                on_change.clone(),
                palette,
            )
        }))
}

fn render_lucide_icons_grid(
    value: &Option<IconDescriptor>,
    on_change: Option<Rc<dyn Fn(Option<IconDescriptor>, &mut Window, &mut App)>>,
    palette: IconPickerPalette,
) -> impl IntoElement {
    div()
        .flex()
        .flex_wrap()
        .gap(px(4.))
        .children(
            COMMON_LUCIDE_ICONS
                .iter()
                .enumerate()
                .map(|(idx, (name, _))| {
                    render_icon_button(
                        format!("lucide-{}", idx),
                        IconDescriptor::lucide(*name),
                        Icon::lucide(name),
                        value,
                        on_change.clone(),
                        palette,
                    )
                }),
        )
}

fn render_icon_button(
    id: impl Into<ElementId>,
    descriptor: IconDescriptor,
    icon: Icon,
    current_value: &Option<IconDescriptor>,
    on_change: Option<Rc<dyn Fn(Option<IconDescriptor>, &mut Window, &mut App)>>,
    palette: IconPickerPalette,
) -> impl IntoElement {
    let is_selected = current_value.as_ref() == Some(&descriptor);
    let descriptor_clone = descriptor.clone();

    div()
        .id(id.into())
        .size(px(32.))
        .rounded(px(4.))
        .cursor_pointer()
        .flex()
        .items_center()
        .justify_center()
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
        .when_some(on_change, move |el, callback| {
            el.on_mouse_down(MouseButton::Left, move |_, window, cx| {
                callback(Some(descriptor_clone.clone()), window, cx);
            })
        })
        .child(icon.size(IconSize::Medium).color(if is_selected {
            palette.selected_text
        } else {
            palette.icon_unselected
        }))
}

fn render_more_button(
    on_open_search: Option<Rc<dyn Fn(&mut Window, &mut App)>>,
    palette: IconPickerPalette,
) -> impl IntoElement {
    div()
        .id("icon-more")
        .px(px(12.))
        .py(px(6.))
        .rounded(px(4.))
        .cursor_pointer()
        .bg(palette.default_button_bg)
        .text_color(palette.text_muted)
        .text_sm()
        .hover(|s| {
            s.bg(palette.icon_button_hover)
                .text_color(palette.text_primary)
        })
        .when_some(on_open_search, |el, callback| {
            el.on_mouse_down(MouseButton::Left, move |_, window, cx| {
                callback(window, cx);
            })
        })
        .child("More icons...")
}
