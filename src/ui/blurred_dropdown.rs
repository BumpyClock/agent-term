use std::rc::Rc;

use gpui::{
    AnyWindowHandle, App, Bounds, Context, EventEmitter, FocusHandle, InteractiveElement,
    MouseButton, MouseDownEvent, ParentElement, Pixels, Render, SharedString, Styled, Subscription,
    Window, WindowBackgroundAppearance, WindowBounds, WindowKind, WindowOptions, div, point,
    prelude::*, px, size,
};

use gpui_component::{ElementExt, scroll::ScrollableElement};

use crate::icons::{Icon, IconName, IconSize};
use crate::ui::ActiveTheme;

#[derive(Clone)]
pub struct DropdownOption {
    pub label: SharedString,
    pub value: String,
}

pub enum BlurredDropdownEvent {
    Confirm(String),
}

pub struct BlurredDropdown {
    focus_handle: FocusHandle,
    options: Vec<DropdownOption>,
    selected_value: Option<String>,
    placeholder: SharedString,
    width: Pixels,
    blur_enabled: bool,
    anchor_bounds: Bounds<Pixels>,
    popup_handle: Option<AnyWindowHandle>,
    bounds_subscription: Option<Subscription>,
}

impl BlurredDropdown {
    pub fn new(
        options: Vec<DropdownOption>,
        selected_value: Option<String>,
        placeholder: impl Into<SharedString>,
        width: Pixels,
        blur_enabled: bool,
        cx: &mut Context<Self>,
    ) -> Self {
        Self {
            focus_handle: cx.focus_handle(),
            options,
            selected_value,
            placeholder: placeholder.into(),
            width,
            blur_enabled,
            anchor_bounds: Bounds::default(),
            popup_handle: None,
            bounds_subscription: None,
        }
    }

    pub fn set_selected_value(&mut self, value: Option<String>, cx: &mut Context<Self>) {
        self.selected_value = value;
        cx.notify();
    }

    pub fn set_blur_enabled(&mut self, enabled: bool, cx: &mut Context<Self>) {
        self.blur_enabled = enabled;
        cx.notify();
    }

    fn toggle_popup(&mut self, _: &MouseDownEvent, window: &mut Window, cx: &mut Context<Self>) {
        if self.popup_handle.is_some() {
            self.dismiss_popup(cx);
        } else {
            self.open_popup(window, cx);
        }
    }

    fn open_popup(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let popup_bounds = self.popup_bounds(window, cx);
        let dropdown = cx.entity().downgrade();
        let on_close = Rc::new(move |cx: &mut App| {
            if let Some(dropdown) = dropdown.upgrade() {
                dropdown.update(cx, |dropdown, cx| dropdown.clear_popup_state(cx));
            }
        });

        let dropdown = cx.entity().downgrade();
        let on_select = Rc::new(move |value: String, cx: &mut App| {
            if let Some(dropdown) = dropdown.upgrade() {
                dropdown.update(cx, |dropdown, cx| {
                    dropdown.selected_value = Some(value.clone());
                    cx.emit(BlurredDropdownEvent::Confirm(value));
                });
            }
        });

        let options = self.options.clone();
        let selected_value = self.selected_value.clone();
        let background = if self.blur_enabled {
            WindowBackgroundAppearance::Blurred
        } else {
            WindowBackgroundAppearance::Transparent
        };
        let display_id = window.display(cx).map(|display| display.id());

        let result = cx.open_window(
            WindowOptions {
                window_bounds: Some(WindowBounds::Windowed(popup_bounds)),
                titlebar: None,
                focus: true,
                show: true,
                kind: WindowKind::PopUp,
                is_movable: false,
                is_resizable: false,
                display_id,
                window_background: background,
                ..Default::default()
            },
            move |popup_window, cx| {
                cx.new(|cx| {
                    DropdownPopup::new(
                        options,
                        selected_value,
                        on_select.clone(),
                        on_close.clone(),
                        popup_window,
                        cx,
                    )
                })
            },
        );

        if let Ok(handle) = result {
            self.popup_handle = Some(handle.into());
            self.register_bounds_observer(window, cx);
        }
    }

    fn register_bounds_observer(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let subscription = cx.observe_window_bounds(window, |this, _window, cx| {
            if this.popup_handle.is_some() {
                this.dismiss_popup(cx);
            }
        });
        self.bounds_subscription = Some(subscription);
    }

    fn dismiss_popup(&mut self, cx: &mut Context<Self>) {
        if let Some(handle) = self.popup_handle.take() {
            let _ = cx.update_window(handle, |_, window, _| window.remove_window());
        }
        self.bounds_subscription = None;
        cx.notify();
    }

    fn clear_popup_state(&mut self, cx: &mut Context<Self>) {
        self.popup_handle = None;
        self.bounds_subscription = None;
        cx.notify();
    }

    fn popup_bounds(&self, window: &Window, cx: &App) -> Bounds<Pixels> {
        let anchor = self.anchor_bounds;
        let window_bounds = window.bounds();
        let width = if anchor.size.width > px(0.0) {
            anchor.size.width
        } else {
            self.width
        };
        let row_height = px(28.0);
        let padding = px(8.0);
        let max_height = px(240.0);
        let item_count = self.options.len().max(1) as f32;
        let list_height = (row_height * item_count + padding * 2.0).min(max_height);
        let gap = px(6.0);
        let origin_base = point(
            window_bounds.origin.x + anchor.origin.x,
            window_bounds.origin.y + anchor.origin.y,
        );
        let mut origin = point(origin_base.x, origin_base.y + anchor.size.height + gap);
        let display_bounds = window
            .display(cx)
            .map(|display| display.bounds())
            .unwrap_or(window_bounds);
        if origin.y + list_height > display_bounds.bottom() {
            origin = point(origin.x, origin_base.y - list_height - gap);
        }
        Bounds {
            origin,
            size: size(width, list_height),
        }
    }

    fn selected_label(&self) -> Option<SharedString> {
        let selected = self.selected_value.as_deref()?;
        self.options
            .iter()
            .find(|option| option.value == selected)
            .map(|option| option.label.clone())
    }
}

impl EventEmitter<BlurredDropdownEvent> for BlurredDropdown {}

impl Render for BlurredDropdown {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let selected_label = self.selected_label();
        let has_selection = selected_label.is_some();
        let display_label = selected_label.unwrap_or_else(|| self.placeholder.clone());
        let text_color = if has_selection {
            cx.theme().foreground
        } else {
            cx.theme().muted_foreground
        };
        let border_color = cx.theme().border.alpha(0.3);
        let input_bg = cx.theme().muted.alpha(0.2);
        let open = self.popup_handle.is_some();
        let focus_ring = cx.theme().ring.alpha(0.6);
        let icon = Icon::new(IconName::ChevronDown)
            .size(IconSize::XSmall)
            .color(cx.theme().muted_foreground);

        div()
            .relative()
            .w(self.width)
            .h(px(34.0))
            .px(px(10.0))
            .rounded(px(8.0))
            .flex()
            .items_center()
            .justify_between()
            .bg(input_bg)
            .border_1()
            .border_color(if open { focus_ring } else { border_color })
            .cursor_pointer()
            .on_mouse_down(MouseButton::Left, cx.listener(Self::toggle_popup))
            .on_prepaint({
                let entity = cx.entity();
                move |bounds, _, cx| {
                    entity.update(cx, |this, _| {
                        this.anchor_bounds = bounds;
                    });
                }
            })
            .child(div().text_sm().text_color(text_color).child(display_label))
            .child(icon)
    }
}

struct DropdownPopup {
    focus_handle: FocusHandle,
    options: Vec<DropdownOption>,
    selected_value: Option<String>,
    on_select: Rc<dyn Fn(String, &mut App)>,
    on_close: Rc<dyn Fn(&mut App)>,
    closing: bool,
    saw_active: bool,
    _activation_subscription: Subscription,
}

impl DropdownPopup {
    fn new(
        options: Vec<DropdownOption>,
        selected_value: Option<String>,
        on_select: Rc<dyn Fn(String, &mut App)>,
        on_close: Rc<dyn Fn(&mut App)>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let activation = cx.observe_window_activation(window, |this, window, cx| {
            if window.is_window_active() {
                this.saw_active = true;
                return;
            }
            if !this.saw_active {
                return;
            }
            if !window.is_window_active() {
                this.close(window, cx);
            }
        });

        Self {
            focus_handle: cx.focus_handle(),
            options,
            selected_value,
            on_select,
            on_close,
            closing: false,
            saw_active: false,
            _activation_subscription: activation,
        }
    }

    fn close(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.closing {
            return;
        }
        self.closing = true;
        (self.on_close)(cx);
        window.remove_window();
    }

    fn select_value(&mut self, value: String, window: &mut Window, cx: &mut Context<Self>) {
        (self.on_select)(value, cx);
        self.close(window, cx);
    }
}

impl Render for DropdownPopup {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let selected = self.selected_value.as_deref();
        let row_height = px(28.0);

        div().size_full().p(px(4.0)).child(
            div()
                .size_full()
                .rounded(px(8.0))
                .border_1()
                .border_color(cx.theme().border)
                .bg(cx.theme().popover)
                .overflow_hidden()
                .child(
                    div()
                        .flex()
                        .flex_col()
                        .gap(px(2.0))
                        .p(px(4.0))
                        .overflow_y_scrollbar()
                        .children(self.options.iter().enumerate().map(|(index, option)| {
                            let value = option.value.clone();
                            let label = option.label.clone();
                            let is_selected = selected.map(|s| s == value).unwrap_or(false);
                            let base_bg = if is_selected {
                                cx.theme().list_active
                            } else {
                                gpui::transparent_black()
                            };

                            div()
                                .id(format!("dropdown-option-{}", index))
                                .h(row_height)
                                .px(px(8.0))
                                .rounded(px(6.0))
                                .flex()
                                .items_center()
                                .text_sm()
                                .text_color(cx.theme().foreground)
                                .bg(base_bg)
                                .hover(|s| s.bg(cx.theme().list_hover))
                                .child(label)
                                .on_mouse_down(
                                    MouseButton::Left,
                                    cx.listener(move |this, _: &MouseDownEvent, window, cx| {
                                        this.select_value(value.clone(), window, cx);
                                    }),
                                )
                        })),
                ),
        )
    }
}
