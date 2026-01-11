//! Icon picker component for selecting icons.
//!
//! Displays a grid of tool icons and common Lucide icons,
//! with a "More icons..." button to open the full Lucide search.

use std::rc::Rc;

use gpui::{
    App, ElementId, InteractiveElement, IntoElement, MouseButton, ParentElement, Styled, Window,
    div, prelude::*, px, rgb,
};

use crate::icons::{Icon, IconDescriptor, IconSize, TOOL_ICONS};

/// Colors for the icon picker
const SECTION_TITLE: u32 = 0xa0a0a0;
const ICON_BUTTON_BG: u32 = 0x2a2a2a;
const ICON_BUTTON_HOVER: u32 = 0x3a3a3a;
const ICON_BUTTON_SELECTED: u32 = 0x5eead4;
const DEFAULT_BUTTON_BG: u32 = 0x2a2a2a;
const TEXT_PRIMARY: u32 = 0xd8d8d8;
const TEXT_MUTED: u32 = 0x808080;

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
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        let value = self.value.clone();
        let on_change = self.on_change.clone();
        let on_open_search = self.on_open_search.clone();

        div()
            .id(self.id)
            .flex()
            .flex_col()
            .gap(px(12.))
            .child(render_default_button(&value, on_change.clone()))
            .child(render_section("Tool icons"))
            .child(render_tool_icons_grid(&value, on_change.clone()))
            .child(render_section("Icons"))
            .child(render_lucide_icons_grid(&value, on_change))
            .child(render_more_button(on_open_search))
    }
}

fn render_section(title: &str) -> impl IntoElement {
    div()
        .text_xs()
        .text_color(rgb(SECTION_TITLE))
        .font_weight(gpui::FontWeight::MEDIUM)
        .child(title.to_string())
}

fn render_default_button(
    value: &Option<IconDescriptor>,
    on_change: Option<Rc<dyn Fn(Option<IconDescriptor>, &mut Window, &mut App)>>,
) -> impl IntoElement {
    let is_selected = value.is_none();

    div()
        .id("icon-default")
        .px(px(12.))
        .py(px(6.))
        .rounded(px(4.))
        .cursor_pointer()
        .bg(if is_selected {
            rgb(ICON_BUTTON_SELECTED)
        } else {
            rgb(DEFAULT_BUTTON_BG)
        })
        .text_color(if is_selected {
            rgb(0x000000)
        } else {
            rgb(TEXT_PRIMARY)
        })
        .text_sm()
        .hover(|s| {
            if !is_selected {
                s.bg(rgb(ICON_BUTTON_HOVER))
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
            )
        }))
}

fn render_lucide_icons_grid(
    value: &Option<IconDescriptor>,
    on_change: Option<Rc<dyn Fn(Option<IconDescriptor>, &mut Window, &mut App)>>,
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
        .when_some(on_change, move |el, callback| {
            el.on_mouse_down(MouseButton::Left, move |_, window, cx| {
                callback(Some(descriptor_clone.clone()), window, cx);
            })
        })
        .child(icon.size(IconSize::Medium).color(if is_selected {
            gpui::hsla(0., 0., 0., 1.)
        } else {
            gpui::hsla(0., 0., 0.85, 1.)
        }))
}

fn render_more_button(
    on_open_search: Option<Rc<dyn Fn(&mut Window, &mut App)>>,
) -> impl IntoElement {
    div()
        .id("icon-more")
        .px(px(12.))
        .py(px(6.))
        .rounded(px(4.))
        .cursor_pointer()
        .bg(rgb(DEFAULT_BUTTON_BG))
        .text_color(rgb(TEXT_MUTED))
        .text_sm()
        .hover(|s| s.bg(rgb(ICON_BUTTON_HOVER)).text_color(rgb(TEXT_PRIMARY)))
        .when_some(on_open_search, |el, callback| {
            el.on_mouse_down(MouseButton::Left, move |_, window, cx| {
                callback(window, cx);
            })
        })
        .child("More icons...")
}
