//! A simplified tab component for settings dialog tabs.
//!
//! This is a minimal tab implementation with underline style only,
//! using hardcoded colors suitable for the AgentTerm settings UI.

use std::rc::Rc;

use gpui::{
    div, prelude::*, px, rgb, rgba, App, ClickEvent, InteractiveElement, IntoElement, MouseButton,
    ParentElement, RenderOnce, SharedString, StatefulInteractiveElement, Styled, Window,
};

/// Hardcoded colors for the tab component.
const ACTIVE_TEXT: u32 = 0x5eead4;
const INACTIVE_TEXT: u32 = 0xa6a6a6;
const UNDERLINE_COLOR: u32 = 0x5eead4;

/// A Tab element for the [`super::TabBar`].
///
/// # Example
///
/// ```ignore
/// Tab::new(0)
///     .label("General")
///     .selected(true)
///     .on_click(|event, window, cx| {
///         // Handle click
///     })
/// ```
#[derive(IntoElement)]
pub struct Tab {
    ix: usize,
    label: Option<SharedString>,
    selected: bool,
    disabled: bool,
    on_click: Option<Rc<dyn Fn(&ClickEvent, &mut Window, &mut App) + 'static>>,
}

impl Tab {
    /// Create a new tab with the given index.
    pub fn new(ix: usize) -> Self {
        Self {
            ix,
            label: None,
            selected: false,
            disabled: false,
            on_click: None,
        }
    }

    /// Set label for the tab.
    pub fn label(mut self, label: impl Into<SharedString>) -> Self {
        self.label = Some(label.into());
        self
    }

    /// Set selected state for the tab.
    pub fn selected(mut self, selected: bool) -> Self {
        self.selected = selected;
        self
    }

    /// Set disabled state for the tab.
    pub fn disabled(mut self, disabled: bool) -> Self {
        self.disabled = disabled;
        self
    }

    /// Set the click handler for the tab.
    pub fn on_click(
        mut self,
        on_click: impl Fn(&ClickEvent, &mut Window, &mut App) + 'static,
    ) -> Self {
        self.on_click = Some(Rc::new(on_click));
        self
    }

    /// Get the index of this tab.
    pub fn index(&self) -> usize {
        self.ix
    }

    /// Get the label of this tab.
    pub fn get_label(&self) -> Option<&SharedString> {
        self.label.as_ref()
    }

    /// Check if the tab is disabled.
    pub fn is_disabled(&self) -> bool {
        self.disabled
    }
}

impl From<&'static str> for Tab {
    fn from(label: &'static str) -> Self {
        Self::new(0).label(label)
    }
}

impl From<String> for Tab {
    fn from(label: String) -> Self {
        Self::new(0).label(label)
    }
}

impl From<SharedString> for Tab {
    fn from(label: SharedString) -> Self {
        Self::new(0).label(label)
    }
}

impl RenderOnce for Tab {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        let text_color = if self.selected {
            rgb(ACTIVE_TEXT)
        } else {
            rgb(INACTIVE_TEXT)
        };

        let border_color = if self.selected {
            rgb(UNDERLINE_COLOR)
        } else {
            rgba(0x00000000)
        };

        div()
            .id(self.ix)
            .flex()
            .items_center()
            .h(px(36.))
            .text_sm()
            .text_color(text_color)
            .when(self.disabled, |this| this.opacity(0.5))
            .when(!self.disabled && !self.selected, |this| {
                this.cursor_pointer()
                    .hover(|this| this.text_color(rgb(ACTIVE_TEXT)))
            })
            .child(
                div()
                    .h(px(28.))
                    .flex()
                    .items_center()
                    .border_b(px(2.))
                    .border_color(border_color)
                    .when_some(self.label, |this, label| this.child(label)),
            )
            .on_mouse_down(MouseButton::Left, |_, _, cx| {
                cx.stop_propagation();
            })
            .when(!self.disabled, |this| {
                this.when_some(self.on_click, |this, on_click| {
                    this.on_click(move |event, window, cx| on_click(event, window, cx))
                })
            })
    }
}
