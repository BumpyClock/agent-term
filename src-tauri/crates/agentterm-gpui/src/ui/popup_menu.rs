//! Simplified popup menu component for agentterm-gpui.
//!
//! Adapted from gpui-component with theme system removed.
//! Uses hardcoded colors matching the terminal app's design.

use gpui::{
    actions, anchored, div, prelude::FluentBuilder, px, rgb, Action, AnyElement, App, AppContext,
    Bounds, ClickEvent, Context, Corner, DismissEvent, Edges, Entity, EventEmitter, FocusHandle,
    Focusable, InteractiveElement, IntoElement, KeyBinding, MouseButton, MouseUpEvent,
    ParentElement, Pixels, Render, ScrollHandle, SharedString, StatefulInteractiveElement, Styled,
    StyleRefinement, WeakEntity, Window,
};
use std::rc::Rc;

// Hardcoded colors matching the terminal app design
const TEXT_PRIMARY: u32 = 0xd8d8d8;
const TEXT_MUTED: u32 = 0xa6a6a6;
const SURFACE_BG: u32 = 0x2a2a2a;
const BORDER_COLOR: u32 = 0x3a3a3a;
const HOVER_BG: u32 = 0x3a3a3a;
const ACCENT_COLOR: u32 = 0x5eead4;

const CONTEXT: &str = "PopupMenu";

actions!(popup_menu, [Cancel, Confirm, SelectUp, SelectDown]);

/// Initialize key bindings for popup menus.
pub fn init(cx: &mut App) {
    cx.bind_keys([
        KeyBinding::new("enter", Confirm, Some(CONTEXT)),
        KeyBinding::new("escape", Cancel, Some(CONTEXT)),
        KeyBinding::new("up", SelectUp, Some(CONTEXT)),
        KeyBinding::new("down", SelectDown, Some(CONTEXT)),
    ]);
}

/// A menu item in a popup menu.
pub enum PopupMenuItem {
    /// A menu separator item.
    Separator,
    /// A non-interactive label item.
    Label(SharedString),
    /// A standard menu item.
    Item {
        label: SharedString,
        disabled: bool,
        checked: bool,
        action: Option<Box<dyn Action>>,
        handler: Option<Rc<dyn Fn(&ClickEvent, &mut Window, &mut App)>>,
    },
    /// A menu item with custom element render.
    ElementItem {
        disabled: bool,
        checked: bool,
        action: Option<Box<dyn Action>>,
        render: Box<dyn Fn(&mut Window, &mut App) -> AnyElement + 'static>,
        handler: Option<Rc<dyn Fn(&ClickEvent, &mut Window, &mut App)>>,
    },
    /// A submenu item that opens another popup menu.
    Submenu {
        label: SharedString,
        disabled: bool,
        menu: Entity<PopupMenu>,
    },
}

impl FluentBuilder for PopupMenuItem {}

impl PopupMenuItem {
    /// Create a new menu item with the given label.
    #[inline]
    pub fn new(label: impl Into<SharedString>) -> Self {
        PopupMenuItem::Item {
            label: label.into(),
            disabled: false,
            checked: false,
            action: None,
            handler: None,
        }
    }

    /// Create a new menu item with custom element render.
    #[inline]
    pub fn element<F, E>(builder: F) -> Self
    where
        F: Fn(&mut Window, &mut App) -> E + 'static,
        E: IntoElement,
    {
        PopupMenuItem::ElementItem {
            disabled: false,
            checked: false,
            action: None,
            render: Box::new(move |window, cx| builder(window, cx).into_any_element()),
            handler: None,
        }
    }

    /// Create a new submenu item that opens another popup menu.
    #[inline]
    pub fn submenu(label: impl Into<SharedString>, menu: Entity<PopupMenu>) -> Self {
        PopupMenuItem::Submenu {
            label: label.into(),
            disabled: false,
            menu,
        }
    }

    /// Create a separator menu item.
    #[inline]
    pub fn separator() -> Self {
        PopupMenuItem::Separator
    }

    /// Creates a label menu item.
    #[inline]
    pub fn label(label: impl Into<SharedString>) -> Self {
        PopupMenuItem::Label(label.into())
    }

    /// Set the action for the menu item.
    pub fn action(mut self, action: Box<dyn Action>) -> Self {
        match &mut self {
            PopupMenuItem::Item { action: a, .. } => {
                *a = Some(action);
            }
            PopupMenuItem::ElementItem { action: a, .. } => {
                *a = Some(action);
            }
            _ => {}
        }
        self
    }

    /// Set the disabled state for the menu item.
    pub fn disabled(mut self, disabled: bool) -> Self {
        match &mut self {
            PopupMenuItem::Item { disabled: d, .. } => {
                *d = disabled;
            }
            PopupMenuItem::ElementItem { disabled: d, .. } => {
                *d = disabled;
            }
            PopupMenuItem::Submenu { disabled: d, .. } => {
                *d = disabled;
            }
            _ => {}
        }
        self
    }

    /// Set checked state for the menu item.
    pub fn checked(mut self, checked: bool) -> Self {
        match &mut self {
            PopupMenuItem::Item { checked: c, .. } => {
                *c = checked;
            }
            PopupMenuItem::ElementItem { checked: c, .. } => {
                *c = checked;
            }
            _ => {}
        }
        self
    }

    /// Add a click handler for the menu item.
    pub fn on_click<F>(mut self, handler: F) -> Self
    where
        F: Fn(&ClickEvent, &mut Window, &mut App) + 'static,
    {
        match &mut self {
            PopupMenuItem::Item { handler: h, .. } => {
                *h = Some(Rc::new(handler));
            }
            PopupMenuItem::ElementItem { handler: h, .. } => {
                *h = Some(Rc::new(handler));
            }
            _ => {}
        }
        self
    }

    #[inline]
    fn is_clickable(&self) -> bool {
        !matches!(self, PopupMenuItem::Separator)
            && matches!(
                self,
                PopupMenuItem::Item {
                    disabled: false,
                    ..
                } | PopupMenuItem::ElementItem {
                    disabled: false,
                    ..
                } | PopupMenuItem::Submenu {
                    disabled: false,
                    ..
                }
            )
    }

    #[inline]
    fn is_separator(&self) -> bool {
        matches!(self, PopupMenuItem::Separator)
    }

    #[inline]
    fn is_checked(&self) -> bool {
        match self {
            PopupMenuItem::Item { checked, .. } => *checked,
            PopupMenuItem::ElementItem { checked, .. } => *checked,
            _ => false,
        }
    }
}

pub struct PopupMenu {
    pub(crate) focus_handle: FocusHandle,
    pub(crate) menu_items: Vec<PopupMenuItem>,
    /// The focus handle of Entity to handle actions.
    pub(crate) action_context: Option<FocusHandle>,
    selected_index: Option<usize>,
    min_width: Option<Pixels>,
    max_width: Option<Pixels>,
    max_height: Option<Pixels>,
    bounds: Bounds<Pixels>,

    /// The parent menu of this menu, if this is a submenu
    parent_menu: Option<WeakEntity<Self>>,
    scrollable: bool,
    scroll_handle: ScrollHandle,
    submenu_anchor: (Corner, Pixels),
}

impl PopupMenu {
    pub(crate) fn new(cx: &mut App) -> Self {
        Self {
            focus_handle: cx.focus_handle(),
            action_context: None,
            parent_menu: None,
            menu_items: Vec::new(),
            selected_index: None,
            min_width: None,
            max_width: None,
            max_height: None,
            bounds: Bounds::default(),
            scrollable: false,
            scroll_handle: ScrollHandle::default(),
            submenu_anchor: (Corner::TopLeft, Pixels::ZERO),
        }
    }

    pub fn build(
        window: &mut Window,
        cx: &mut App,
        f: impl FnOnce(Self, &mut Window, &mut Context<PopupMenu>) -> Self,
    ) -> Entity<Self> {
        cx.new(|cx| f(Self::new(cx), window, cx))
    }

    /// Set the focus handle of Entity to handle actions.
    pub fn action_context(mut self, handle: FocusHandle) -> Self {
        self.action_context = Some(handle);
        self
    }

    /// Set min width of the popup menu, default is 120px.
    pub fn min_w(mut self, width: impl Into<Pixels>) -> Self {
        self.min_width = Some(width.into());
        self
    }

    /// Set max width of the popup menu, default is 500px.
    pub fn max_w(mut self, width: impl Into<Pixels>) -> Self {
        self.max_width = Some(width.into());
        self
    }

    /// Set max height of the popup menu.
    pub fn max_h(mut self, height: impl Into<Pixels>) -> Self {
        self.max_height = Some(height.into());
        self
    }

    /// Set the menu to be scrollable.
    pub fn scrollable(mut self, scrollable: bool) -> Self {
        self.scrollable = scrollable;
        self
    }

    /// Add Menu Item.
    pub fn menu(self, label: impl Into<SharedString>, action: Box<dyn Action>) -> Self {
        self.menu_with_disabled(label, action, false)
    }

    /// Add Menu Item with disabled state.
    pub fn menu_with_disabled(
        mut self,
        label: impl Into<SharedString>,
        action: Box<dyn Action>,
        disabled: bool,
    ) -> Self {
        self.menu_items.push(
            PopupMenuItem::new(label)
                .disabled(disabled)
                .action(action),
        );
        self
    }

    /// Add Menu Item with check.
    pub fn menu_with_check(
        mut self,
        label: impl Into<SharedString>,
        checked: bool,
        action: Box<dyn Action>,
    ) -> Self {
        self.menu_items.push(
            PopupMenuItem::new(label)
                .checked(checked)
                .action(action),
        );
        self
    }

    /// Add label.
    pub fn label(mut self, label: impl Into<SharedString>) -> Self {
        self.menu_items.push(PopupMenuItem::label(label.into()));
        self
    }

    /// Add a separator Menu Item.
    pub fn separator(mut self) -> Self {
        if self.menu_items.is_empty() {
            return self;
        }

        if let Some(PopupMenuItem::Separator) = self.menu_items.last() {
            return self;
        }

        self.menu_items.push(PopupMenuItem::separator());
        self
    }

    /// Add a Submenu.
    pub fn submenu(
        mut self,
        label: impl Into<SharedString>,
        window: &mut Window,
        cx: &mut Context<Self>,
        f: impl Fn(PopupMenu, &mut Window, &mut Context<PopupMenu>) -> PopupMenu + 'static,
    ) -> Self {
        let submenu = PopupMenu::build(window, cx, f);
        let parent_menu = cx.entity().downgrade();
        submenu.update(cx, |view, _| {
            view.parent_menu = Some(parent_menu);
        });

        self.menu_items.push(PopupMenuItem::submenu(label, submenu));
        self
    }

    /// Add menu item.
    pub fn item(mut self, item: impl Into<PopupMenuItem>) -> Self {
        let item: PopupMenuItem = item.into();
        self.menu_items.push(item);
        self
    }

    pub(crate) fn active_submenu(&self) -> Option<Entity<PopupMenu>> {
        if let Some(ix) = self.selected_index {
            if let Some(item) = self.menu_items.get(ix) {
                return match item {
                    PopupMenuItem::Submenu { menu, .. } => Some(menu.clone()),
                    _ => None,
                };
            }
        }
        None
    }

    pub fn is_empty(&self) -> bool {
        self.menu_items.is_empty()
    }

    fn clickable_menu_items(&self) -> impl Iterator<Item = (usize, &PopupMenuItem)> {
        self.menu_items
            .iter()
            .enumerate()
            .filter(|(_, item)| item.is_clickable())
    }

    fn on_click(&mut self, ix: usize, window: &mut Window, cx: &mut Context<Self>) {
        cx.stop_propagation();
        window.prevent_default();
        self.selected_index = Some(ix);
        self.confirm(&Confirm, window, cx);
    }

    fn confirm(&mut self, _: &Confirm, window: &mut Window, cx: &mut Context<Self>) {
        match self.selected_index {
            Some(index) => {
                let item = self.menu_items.get(index);
                match item {
                    Some(PopupMenuItem::Item {
                        handler, action, ..
                    }) => {
                        if let Some(handler) = handler {
                            handler(&ClickEvent::default(), window, cx);
                        } else if let Some(action) = action.as_ref() {
                            self.dispatch_confirm_action(action, window, cx);
                        }
                        self.dismiss(&Cancel, window, cx)
                    }
                    Some(PopupMenuItem::ElementItem {
                        handler, action, ..
                    }) => {
                        if let Some(handler) = handler {
                            handler(&ClickEvent::default(), window, cx);
                        } else if let Some(action) = action.as_ref() {
                            self.dispatch_confirm_action(action, window, cx);
                        }
                        self.dismiss(&Cancel, window, cx)
                    }
                    _ => {}
                }
            }
            _ => {}
        }
    }

    fn dispatch_confirm_action(
        &self,
        action: &Box<dyn Action>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if let Some(context) = self.action_context.as_ref() {
            context.focus(window, cx);
        }
        window.dispatch_action(action.boxed_clone(), cx);
    }

    fn set_selected_index(&mut self, ix: usize, cx: &mut Context<Self>) {
        if self.selected_index != Some(ix) {
            self.selected_index = Some(ix);
            self.scroll_handle.scroll_to_item(ix);
            cx.notify();
        }
    }

    fn select_up(&mut self, _: &SelectUp, _: &mut Window, cx: &mut Context<Self>) {
        cx.stop_propagation();
        let ix = self.selected_index.unwrap_or(0);

        if let Some((prev_ix, _)) = self
            .menu_items
            .iter()
            .enumerate()
            .rev()
            .find(|(i, item)| *i < ix && item.is_clickable())
        {
            self.set_selected_index(prev_ix, cx);
            return;
        }

        let last_clickable_ix = self.clickable_menu_items().last().map(|(ix, _)| ix);
        self.set_selected_index(last_clickable_ix.unwrap_or(0), cx);
    }

    fn select_down(&mut self, _: &SelectDown, _: &mut Window, cx: &mut Context<Self>) {
        cx.stop_propagation();
        let Some(ix) = self.selected_index else {
            self.set_selected_index(0, cx);
            return;
        };

        if let Some((next_ix, _)) = self
            .menu_items
            .iter()
            .enumerate()
            .find(|(i, item)| *i > ix && item.is_clickable())
        {
            self.set_selected_index(next_ix, cx);
            return;
        }

        self.set_selected_index(0, cx);
    }

    fn dismiss(&mut self, _: &Cancel, window: &mut Window, cx: &mut Context<Self>) {
        if self.active_submenu().is_some() {
            return;
        }

        cx.emit(DismissEvent);

        // Focus back to the previous focused handle.
        if let Some(action_context) = self.action_context.as_ref() {
            window.focus(action_context, cx);
        }

        let Some(parent_menu) = self.parent_menu.clone() else {
            return;
        };

        // Dismiss parent menu when this menu is dismissed
        _ = parent_menu.update(cx, |view, cx| {
            view.selected_index = None;
            view.dismiss(&Cancel, window, cx);
        });
    }

    #[inline]
    fn max_width(&self) -> Pixels {
        self.max_width.unwrap_or(px(500.))
    }

    /// Calculate the anchor corner and left offset for child submenu.
    fn update_submenu_menu_anchor(&mut self, window: &Window) {
        let bounds = self.bounds;
        let max_width = self.max_width();
        let (anchor, left) = if max_width + bounds.origin.x > window.bounds().size.width {
            (Corner::TopRight, -px(16.))
        } else {
            (Corner::TopLeft, bounds.size.width - px(8.))
        };

        let is_bottom_pos = bounds.origin.y + bounds.size.height > window.bounds().size.height;
        self.submenu_anchor = if is_bottom_pos {
            (anchor.other_side_corner_along(gpui::Axis::Vertical), left)
        } else {
            (anchor, left)
        };
    }

    fn render_item(
        &self,
        ix: usize,
        item: &PopupMenuItem,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let selected = self.selected_index == Some(ix);
        let is_submenu = matches!(item, PopupMenuItem::Submenu { .. });
        let group_name = format!("{}:item-{}", cx.entity().entity_id(), ix);

        let base = MenuItemElement::new(ix, &group_name)
            .relative()
            .text_sm()
            .py_0()
            .px(px(8.))
            .rounded(px(4.))
            .items_center()
            .selected(selected)
            .on_hover(cx.listener(move |this, hovered, _, cx| {
                if *hovered {
                    this.selected_index = Some(ix);
                } else if !is_submenu && this.selected_index == Some(ix) {
                    this.selected_index = None;
                }
                cx.notify();
            }));

        match item {
            PopupMenuItem::Separator => base
                .h_auto()
                .p_0()
                .my(px(2.))
                .mx(px(-4.))
                .h(px(1.))
                .bg(rgb(BORDER_COLOR))
                .disabled(true),

            PopupMenuItem::Label(label) => base.disabled(true).cursor_default().child(
                div()
                    .cursor_default()
                    .items_center()
                    .gap_x(px(4.))
                    .child(div().flex_1().child(label.clone())),
            ),

            PopupMenuItem::ElementItem {
                render, disabled, ..
            } => base
                .when(!disabled, |this| {
                    this.on_click(
                        cx.listener(move |this, _, window, cx| this.on_click(ix, window, cx)),
                    )
                })
                .disabled(*disabled)
                .child(
                    div()
                        .flex_1()
                        .min_h(px(26.))
                        .items_center()
                        .gap_x(px(4.))
                        .child((render)(_window, cx)),
                ),

            PopupMenuItem::Item {
                label, disabled, checked, ..
            } => base
                .when(!disabled, |this| {
                    this.on_click(
                        cx.listener(move |this, _, window, cx| this.on_click(ix, window, cx)),
                    )
                })
                .disabled(*disabled)
                .h(px(26.))
                .gap_x(px(4.))
                .child(
                    div()
                        .w_full()
                        .gap(px(12.))
                        .flex()
                        .items_center()
                        .justify_between()
                        .child(label.clone())
                        .when(*checked, |el| el.child(div().text_xs().child("*"))),
                ),

            PopupMenuItem::Submenu {
                label,
                menu,
                disabled,
            } => base
                .selected(selected)
                .disabled(*disabled)
                .items_start()
                .child(
                    div()
                        .min_h(px(26.))
                        .size_full()
                        .flex()
                        .items_center()
                        .gap_x(px(4.))
                        .child(
                            div()
                                .flex_1()
                                .gap(px(8.))
                                .flex()
                                .items_center()
                                .justify_between()
                                .child(label.clone())
                                .child(div().text_xs().text_color(rgb(TEXT_MUTED)).child(">")),
                        ),
                )
                .when(selected, |this| {
                    this.child({
                        let (anchor, left) = self.submenu_anchor;
                        let is_bottom_pos =
                            matches!(anchor, Corner::BottomLeft | Corner::BottomRight);
                        anchored()
                            .anchor(anchor)
                            .child(
                                div()
                                    .id("submenu")
                                    .occlude()
                                    .when(is_bottom_pos, |this| this.bottom_0())
                                    .when(!is_bottom_pos, |this| this.top(px(-4.)))
                                    .left(left)
                                    .child(menu.clone()),
                            )
                            .snap_to_window_with_margin(Edges::all(px(4.)))
                    })
                }),
        }
    }
}

impl FluentBuilder for PopupMenu {}
impl EventEmitter<DismissEvent> for PopupMenu {}

impl Focusable for PopupMenu {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus_handle.clone()
    }
}

impl Render for PopupMenu {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        self.update_submenu_menu_anchor(window);

        let items_count = self.menu_items.len();

        let max_height = self.max_height.unwrap_or_else(|| {
            let window_half_height = window.window_bounds().get_bounds().size.height * 0.5;
            window_half_height.min(px(450.))
        });

        let max_width = self.max_width();

        // Collect rendered items first to avoid borrow conflicts
        let mut rendered_items: Vec<AnyElement> = Vec::new();
        for (ix, item) in self.menu_items.iter().enumerate() {
            // Ignore last separator
            if ix + 1 == items_count && item.is_separator() {
                continue;
            }
            rendered_items.push(self.render_item(ix, item, window, cx).into_any_element());
        }

        div()
            .id("popup-menu")
            .key_context(CONTEXT)
            .track_focus(&self.focus_handle)
            .on_action(cx.listener(Self::select_up))
            .on_action(cx.listener(Self::select_down))
            .on_action(cx.listener(Self::confirm))
            .on_action(cx.listener(Self::dismiss))
            .on_mouse_up_out(
                MouseButton::Left,
                cx.listener(|this, ev: &MouseUpEvent, window, cx| {
                    // Do not dismiss if click inside the parent menu
                    if let Some(parent) = this.parent_menu.as_ref() {
                        if let Some(parent) = parent.upgrade() {
                            if parent.read(cx).bounds.contains(&ev.position) {
                                return;
                            }
                        }
                    }
                    this.dismiss(&Cancel, window, cx);
                }),
            )
            // Popover styling with hardcoded colors
            .bg(rgb(SURFACE_BG))
            .border_1()
            .border_color(rgb(BORDER_COLOR))
            .rounded(px(8.))
            .shadow_lg()
            .text_color(rgb(TEXT_PRIMARY))
            .relative()
            .child(
                div()
                    .id("items")
                    .flex()
                    .flex_col()
                    .p(px(4.))
                    .gap_y(px(2.))
                    .min_w(px(120.))
                    .when_some(self.min_width, |this, min_width| this.min_w(min_width))
                    .max_w(max_width)
                    .when(self.scrollable, |this| {
                        this.max_h(max_height)
                            .overflow_y_scroll()
                            .track_scroll(&self.scroll_handle)
                    })
                    .children(rendered_items),
            )
    }
}

// Simplified menu item element without external dependencies
#[derive(IntoElement)]
pub(crate) struct MenuItemElement {
    id: gpui::ElementId,
    group_name: SharedString,
    style: StyleRefinement,
    disabled: bool,
    selected: bool,
    on_click: Option<Box<dyn Fn(&ClickEvent, &mut Window, &mut App) + 'static>>,
    on_hover: Option<Box<dyn Fn(&bool, &mut Window, &mut App) + 'static>>,
    children: Vec<AnyElement>,
}

impl MenuItemElement {
    pub(crate) fn new(id: impl Into<gpui::ElementId>, group_name: impl Into<SharedString>) -> Self {
        Self {
            id: id.into(),
            group_name: group_name.into(),
            style: StyleRefinement::default(),
            disabled: false,
            selected: false,
            on_click: None,
            on_hover: None,
            children: Vec::new(),
        }
    }

    pub(crate) fn selected(mut self, selected: bool) -> Self {
        self.selected = selected;
        self
    }

    pub(crate) fn disabled(mut self, disabled: bool) -> Self {
        self.disabled = disabled;
        self
    }

    pub(crate) fn on_click(
        mut self,
        handler: impl Fn(&ClickEvent, &mut Window, &mut App) + 'static,
    ) -> Self {
        self.on_click = Some(Box::new(handler));
        self
    }

    #[allow(unused)]
    pub fn on_hover(mut self, handler: impl Fn(&bool, &mut Window, &mut App) + 'static) -> Self {
        self.on_hover = Some(Box::new(handler));
        self
    }
}

impl Styled for MenuItemElement {
    fn style(&mut self) -> &mut StyleRefinement {
        &mut self.style
    }
}

impl ParentElement for MenuItemElement {
    fn extend(&mut self, elements: impl IntoIterator<Item = AnyElement>) {
        self.children.extend(elements);
    }
}

impl gpui::RenderOnce for MenuItemElement {
    fn render(self, _: &mut Window, _cx: &mut App) -> impl IntoElement {
        div()
            .id(self.id)
            .group(&self.group_name)
            .gap_x(px(4.))
            .py(px(4.))
            .px(px(8.))
            .text_sm()
            .text_color(rgb(TEXT_PRIMARY))
            .relative()
            .flex()
            .items_center()
            .justify_between()
            .when_some(self.on_hover, |this, on_hover| {
                this.on_hover(move |hovered, window, cx| (on_hover)(hovered, window, cx))
            })
            .when(!self.disabled, |this| {
                this.group_hover(&self.group_name, |this| this.bg(rgb(HOVER_BG)))
                    .when(self.selected, |this| this.bg(rgb(HOVER_BG)))
                    .when_some(self.on_click, |this, on_click| {
                        this.on_mouse_down(MouseButton::Left, move |_, _, cx| {
                            cx.stop_propagation();
                        })
                        .on_click(on_click)
                    })
            })
            .when(self.disabled, |this| this.text_color(rgb(TEXT_MUTED)))
            .children(self.children)
    }
}
