//! A simplified toggle switch component for settings dialogs.
//!
//! This is a minimal switch implementation without animations or themes,
//! using hardcoded colors suitable for the AgentTerm settings UI.

use std::rc::Rc;

use gpui::{
    div, prelude::*, px, rgb, App, ElementId, InteractiveElement, IntoElement, MouseButton,
    ParentElement, RenderOnce, SharedString, Styled, Window,
};

/// Hardcoded colors for the switch component.
const CHECKED_BG: u32 = 0x5eead4;
const UNCHECKED_BG: u32 = 0x4a4a4a;
const THUMB_COLOR: u32 = 0xffffff;
const LABEL_COLOR: u32 = 0xd8d8d8;
const DISABLED_OPACITY: f32 = 0.5;

/// A Switch element that can be toggled on or off.
///
/// # Example
///
/// ```ignore
/// Switch::new("dark-mode-toggle")
///     .checked(is_dark_mode)
///     .label("Dark Mode")
///     .on_click(|checked, window, cx| {
///         // Handle toggle
///     })
/// ```
#[derive(IntoElement)]
pub struct Switch {
    id: ElementId,
    checked: bool,
    disabled: bool,
    label: Option<SharedString>,
    on_click: Option<Rc<dyn Fn(&bool, &mut Window, &mut App)>>,
}

impl Switch {
    /// Create a new Switch element.
    pub fn new(id: impl Into<ElementId>) -> Self {
        Self {
            id: id.into(),
            checked: false,
            disabled: false,
            label: None,
            on_click: None,
        }
    }

    /// Set the checked state of the switch.
    pub fn checked(mut self, checked: bool) -> Self {
        self.checked = checked;
        self
    }

    /// Set the disabled state of the switch.
    pub fn disabled(mut self, disabled: bool) -> Self {
        self.disabled = disabled;
        self
    }

    /// Set the label of the switch.
    pub fn label(mut self, label: impl Into<SharedString>) -> Self {
        self.label = Some(label.into());
        self
    }

    /// Add a click handler for the switch.
    pub fn on_click<F>(mut self, handler: F) -> Self
    where
        F: Fn(&bool, &mut Window, &mut App) + 'static,
    {
        self.on_click = Some(Rc::new(handler));
        self
    }
}

impl RenderOnce for Switch {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        let checked = self.checked;
        let on_click = self.on_click.clone();

        let bg_color = if checked {
            rgb(CHECKED_BG)
        } else {
            rgb(UNCHECKED_BG)
        };

        let bg_width = px(36.);
        let bg_height = px(20.);
        let thumb_size = px(16.);
        let inset = px(2.);
        let max_x = bg_width - thumb_size - inset * 2;
        let thumb_x = if checked { max_x } else { px(0.) };

        div()
            .id(self.id.clone())
            .flex()
            .items_center()
            .gap(px(8.))
            .when(self.disabled, |this| this.opacity(DISABLED_OPACITY))
            .when(!self.disabled, |this| this.cursor_pointer())
            .child(
                div()
                    .w(bg_width)
                    .h(bg_height)
                    .rounded(bg_height)
                    .flex()
                    .items_center()
                    .p(inset)
                    .bg(bg_color)
                    .child(
                        div()
                            .size(thumb_size)
                            .rounded(thumb_size)
                            .bg(rgb(THUMB_COLOR))
                            .ml(thumb_x),
                    ),
            )
            .when_some(self.label, |this, label| {
                this.child(
                    div()
                        .text_sm()
                        .text_color(rgb(LABEL_COLOR))
                        .line_height(bg_height)
                        .child(label),
                )
            })
            .when_some(on_click.filter(|_| !self.disabled), |this, on_click| {
                this.on_mouse_down(MouseButton::Left, move |_, window, cx| {
                    cx.stop_propagation();
                    on_click(&!checked, window, cx);
                })
            })
    }
}
