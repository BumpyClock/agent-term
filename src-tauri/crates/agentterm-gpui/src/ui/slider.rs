//! A simplified horizontal slider component for settings dialogs.
//!
//! This is a minimal slider implementation without animations, logarithmic
//! scaling, or range support. Uses hardcoded colors suitable for the
//! AgentTerm settings UI.

use gpui::{
    div, prelude::*, px, relative, rgb, App, Bounds, Context, DragMoveEvent, Empty, Entity,
    EntityId, EventEmitter, InteractiveElement, IntoElement, MouseButton, MouseDownEvent,
    ParentElement, Pixels, Point, Render, RenderOnce, StatefulInteractiveElement, Styled, Window,
};

/// Hardcoded colors for the slider component.
const TRACK_BG: u32 = 0x3a3a3a;
const FILL_COLOR: u32 = 0x5eead4;
const THUMB_COLOR: u32 = 0xffffff;

/// Drag state for the slider thumb.
#[derive(Clone)]
struct DragSlider(EntityId);

impl Render for DragSlider {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        Empty
    }
}

/// Events emitted by the [`SliderState`].
pub enum SliderEvent {
    Change(f32),
}

/// State of the [`Slider`].
///
/// Manages the slider value, bounds, and drag interactions.
///
/// # Example
///
/// ```ignore
/// let slider_state = cx.new(|_| SliderState::new().min(0.0).max(100.0).default_value(50.0));
/// ```
pub struct SliderState {
    min: f32,
    max: f32,
    step: f32,
    value: f32,
    percentage: f32,
    bounds: Bounds<Pixels>,
}

impl SliderState {
    /// Create a new [`SliderState`].
    pub fn new() -> Self {
        Self {
            min: 0.0,
            max: 100.0,
            step: 1.0,
            value: 0.0,
            percentage: 0.0,
            bounds: Bounds::default(),
        }
    }

    /// Set the minimum value of the slider, default: 0.0
    pub fn min(mut self, min: f32) -> Self {
        self.min = min;
        self.update_percentage();
        self
    }

    /// Set the maximum value of the slider, default: 100.0
    pub fn max(mut self, max: f32) -> Self {
        self.max = max;
        self.update_percentage();
        self
    }

    /// Set the step value of the slider, default: 1.0
    pub fn step(mut self, step: f32) -> Self {
        self.step = step;
        self
    }

    /// Set the default value of the slider, default: 0.0
    pub fn default_value(mut self, value: f32) -> Self {
        self.value = value.clamp(self.min, self.max);
        self.update_percentage();
        self
    }

    /// Set the value of the slider.
    pub fn set_value(&mut self, value: f32, _window: &mut Window, cx: &mut Context<Self>) {
        self.value = value.clamp(self.min, self.max);
        self.update_percentage();
        cx.notify();
    }

    /// Get the current value of the slider.
    pub fn value(&self) -> f32 {
        self.value
    }
    
    /// Set bounds for the slider (called during render to capture track dimensions).
    pub fn set_bounds(&mut self, bounds: Bounds<Pixels>) {
        self.bounds = bounds;
    }

    fn update_percentage(&mut self) {
        let range = self.max - self.min;
        self.percentage = if range <= 0.0 {
            0.0
        } else {
            (self.value - self.min) / range
        };
    }

    fn update_value_by_position(
        &mut self,
        position: Point<Pixels>,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let bounds = self.bounds;
        if bounds.size.width <= px(0.) {
            return;
        }
        let inner_pos = position.x - bounds.left();
        let total_width = bounds.size.width;
        let percentage = (inner_pos / total_width).clamp(0.0, 1.0);

        let raw_value = self.min + (self.max - self.min) * percentage;
        let stepped_value = (raw_value / self.step).round() * self.step;
        self.value = stepped_value.clamp(self.min, self.max);
        self.update_percentage();

        cx.emit(SliderEvent::Change(self.value));
        cx.notify();
    }
}

impl Default for SliderState {
    fn default() -> Self {
        Self::new()
    }
}

impl EventEmitter<SliderEvent> for SliderState {}

/// A horizontal Slider element.
///
/// # Example
///
/// ```ignore
/// let slider_state = cx.new(|_| SliderState::new().min(0.0).max(100.0).default_value(50.0));
/// Slider::new(&slider_state).disabled(false)
/// ```
#[derive(IntoElement)]
pub struct Slider {
    state: Entity<SliderState>,
    disabled: bool,
}

impl Slider {
    /// Create a new [`Slider`] element bound to the [`SliderState`].
    pub fn new(state: &Entity<SliderState>) -> Self {
        Self {
            state: state.clone(),
            disabled: false,
        }
    }

    /// Set the disabled state of the slider, default: false
    pub fn disabled(mut self, disabled: bool) -> Self {
        self.disabled = disabled;
        self
    }
}

impl RenderOnce for Slider {
    fn render(self, window: &mut Window, cx: &mut App) -> impl IntoElement {
        let entity_id = self.state.entity_id();
        let state = self.state.read(cx);
        let percentage = state.percentage;
        let bar_end = relative(1.0 - percentage);
        let disabled = self.disabled;

        div()
            .id(("slider", entity_id))
            .flex()
            .flex_1()
            .items_center()
            .w_full()
            .h(px(24.))
            .when(disabled, |this| this.opacity(0.5))
            .child(
                div()
                    .id("slider-track")
                    .flex_1()
                    .h(px(24.))
                    .flex()
                    .items_center()
                    .when(!disabled, |this| {
                        this.on_mouse_down(
                            MouseButton::Left,
                            window.listener_for(
                                &self.state,
                                move |state, e: &MouseDownEvent, window, cx| {
                                    state.update_value_by_position(e.position, window, cx);
                                },
                            ),
                        )
                        .on_drag(DragSlider(entity_id), |drag, _, _, cx| {
                            cx.stop_propagation();
                            cx.new(|_| drag.clone())
                        })
                        .on_drag_move(window.listener_for(
                            &self.state,
                            move |state, e: &DragMoveEvent<DragSlider>, window, cx| {
                                if let DragSlider(id) = e.drag(cx) {
                                    if *id == entity_id {
                                        state.update_value_by_position(e.event.position, window, cx);
                                    }
                                }
                            },
                        ))
                    })
                    .child(
                        div()
                            .relative()
                            .w_full()
                            .h(px(6.))
                            .bg(rgb(TRACK_BG))
                            .rounded(px(3.))
                            .child(
                                div()
                                    .absolute()
                                    .h_full()
                                    .left_0()
                                    .right(bar_end)
                                    .bg(rgb(FILL_COLOR))
                                    .rounded(px(3.)),
                            )
                            .child(
                                div()
                                    .absolute()
                                    .top(px(-5.))
                                    .left(relative(percentage))
                                    .ml(px(-8.))
                                    .size(px(16.))
                                    .rounded(px(8.))
                                    .bg(rgb(THUMB_COLOR))
                                    .shadow_md(),
                            ),
                    ),
            )
    }
}
