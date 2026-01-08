//! A simplified tab bar component for settings dialog tabs.
//!
//! This is a minimal tab bar implementation with underline style only,
//! using hardcoded colors suitable for the AgentTerm settings UI.

use std::rc::Rc;

use gpui::{
    div, prelude::*, px, rgb, App, ElementId, IntoElement, ParentElement, RenderOnce, Styled,
    Window,
};
use smallvec::SmallVec;

use super::Tab;

/// Hardcoded colors for the tab bar component.
const BORDER_COLOR: u32 = 0x3a3a3a;

/// A TabBar element that contains multiple [`Tab`] items.
///
/// # Example
///
/// ```ignore
/// TabBar::new("settings-tabs")
///     .child(Tab::new(0).label("General"))
///     .child(Tab::new(1).label("Appearance"))
///     .child(Tab::new(2).label("Advanced"))
///     .selected_index(0)
///     .on_click(|index, window, cx| {
///         // Handle tab selection
///     })
/// ```
#[derive(IntoElement)]
pub struct TabBar {
    id: ElementId,
    children: SmallVec<[Tab; 4]>,
    selected_index: Option<usize>,
    on_click: Option<Rc<dyn Fn(&usize, &mut Window, &mut App) + 'static>>,
}

impl TabBar {
    /// Create a new TabBar.
    pub fn new(id: impl Into<ElementId>) -> Self {
        Self {
            id: id.into(),
            children: SmallVec::new(),
            selected_index: None,
            on_click: None,
        }
    }

    /// Add a single tab to the TabBar.
    pub fn child(mut self, child: impl Into<Tab>) -> Self {
        self.children.push(child.into());
        self
    }

    /// Add multiple tabs to the TabBar.
    pub fn children(mut self, children: impl IntoIterator<Item = impl Into<Tab>>) -> Self {
        self.children.extend(children.into_iter().map(Into::into));
        self
    }

    /// Set the selected index of the TabBar.
    pub fn selected_index(mut self, index: usize) -> Self {
        self.selected_index = Some(index);
        self
    }

    /// Set the on_click callback of the TabBar.
    ///
    /// The callback receives the index of the clicked tab.
    /// When this is set, the children's on_click will be ignored.
    pub fn on_click<F>(mut self, on_click: F) -> Self
    where
        F: Fn(&usize, &mut Window, &mut App) + 'static,
    {
        self.on_click = Some(Rc::new(on_click));
        self
    }
}

impl RenderOnce for TabBar {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        let selected_index = self.selected_index;
        let on_click = self.on_click.clone();
        let gap = px(16.);

        div()
            .id(self.id)
            .relative()
            .flex()
            .items_center()
            .gap(gap)
            .child(
                div()
                    .id("border-b")
                    .absolute()
                    .left_0()
                    .bottom_0()
                    .size_full()
                    .border_b_1()
                    .border_color(rgb(BORDER_COLOR)),
            )
            .children(self.children.into_iter().enumerate().map(|(ix, child)| {
                let is_selected = selected_index == Some(ix);
                let is_disabled = child.is_disabled();
                let on_click_clone = on_click.clone();

                Tab::new(ix)
                    .label(child.get_label().cloned().unwrap_or_default())
                    .selected(is_selected)
                    .disabled(is_disabled)
                    .when_some(on_click_clone, move |this, on_click| {
                        this.on_click(move |_, window, cx| on_click(&ix, window, cx))
                    })
            }))
    }
}
