//! Settings dialog component for AgentTerm.
//!
//! Opens in its own window with 3 tabs: General, Appearance, and Tools.

use gpui::{
    AnyElement, App, Context, Entity, FocusHandle, Focusable, InteractiveElement, IntoElement,
    ParentElement, Render, SharedString, StatefulInteractiveElement, Styled, Window, div,
    prelude::*, px,
};

use crate::fonts::{FontOption, find_font_index, font_presets};
use crate::settings::{AppSettings, Theme};
use crate::ui::{
    ActiveTheme, Button, ButtonVariants, Slider, SliderEvent, SliderState, Switch, Tab, TabBar,
};
use gpui_component::IndexPath;
use gpui_component::input::{
    InputEvent, InputState as GpuiInputState, NumberInput, NumberInputEvent, StepAction,
};
use gpui_component::select::{Select, SelectEvent, SelectItem, SelectState};

/// A font item for the Select dropdown that wraps FontOption.
#[derive(Debug, Clone, PartialEq, Eq)]
struct FontItem {
    name: SharedString,
    family: String,
}

impl FontItem {
    fn from_font_option(opt: &FontOption) -> Self {
        Self {
            name: opt.name.into(),
            family: opt.family.to_string(),
        }
    }
}

impl SelectItem for FontItem {
    type Value = String;

    fn title(&self) -> SharedString {
        self.name.clone()
    }

    fn value(&self) -> &Self::Value {
        &self.family
    }
}

/// Settings dialog state.
pub struct SettingsDialog {
    focus_handle: FocusHandle,
    tab_index: usize,
    settings: AppSettings,
    original_settings: AppSettings,
    font_size_input: Entity<GpuiInputState>,
    line_height_input: Entity<GpuiInputState>,
    letter_spacing_input: Entity<GpuiInputState>,
    transparency_slider: Entity<SliderState>,
    font_select: Entity<SelectState<Vec<FontItem>>>,
    on_close: Option<Box<dyn Fn(&mut Window, &mut App) + 'static>>,
    on_save: Option<Box<dyn Fn(AppSettings, &mut Window, &mut App) + 'static>>,
    on_change: Option<Box<dyn Fn(AppSettings, &mut Window, &mut App) + 'static>>,
}

impl SettingsDialog {
    /// Create a new settings dialog.
    pub fn new(settings: AppSettings, window: &mut Window, cx: &mut Context<Self>) -> Self {
        let original_settings = settings.clone();

        let font_size_input = cx.new(|cx| {
            GpuiInputState::new(window, cx).default_value(settings.font_size.to_string())
        });

        let line_height_input = cx.new(|cx| {
            GpuiInputState::new(window, cx).default_value(format!("{:.1}", settings.line_height))
        });

        let letter_spacing_input = cx.new(|cx| {
            GpuiInputState::new(window, cx).default_value(format!("{:.1}", settings.letter_spacing))
        });

        let transparency_slider = cx.new(|_| {
            SliderState::new()
                .min(0.0)
                .max(100.0)
                .step(5.0)
                .default_value(settings.window_transparency * 100.0)
        });

        // Create font select with initial selection
        let font_items: Vec<FontItem> = font_presets()
            .iter()
            .map(FontItem::from_font_option)
            .collect();
        let selected_font_index =
            find_font_index(&settings.font_family).map(|idx| IndexPath::default().row(idx));
        let font_select =
            cx.new(|cx| SelectState::new(font_items, selected_font_index, window, cx));

        // Subscribe to font select events
        cx.subscribe_in(
            &font_select,
            window,
            |this, _, event: &SelectEvent<Vec<FontItem>>, window, cx| {
                if let SelectEvent::Confirm(Some(family)) = event {
                    this.settings.font_family = family.clone();
                    this.notify_change(window, cx);
                }
            },
        )
        .detach();

        // Subscribe to font size NumberInput events
        cx.subscribe_in(
            &font_size_input,
            window,
            |this, input, event: &NumberInputEvent, window, cx| {
                if let NumberInputEvent::Step(action) = event {
                    input.update(cx, |input, cx| {
                        let value = input.value().parse::<f32>().unwrap_or(14.0);
                        let new_value = match action {
                            StepAction::Increment => (value + 1.0).min(32.0),
                            StepAction::Decrement => (value - 1.0).max(8.0),
                        };
                        this.settings.font_size = new_value;
                        input.set_value(new_value.to_string(), window, cx);
                    });
                    this.notify_change(window, cx);
                }
            },
        )
        .detach();

        cx.subscribe_in(
            &font_size_input,
            window,
            |this, input, event: &InputEvent, window, cx| {
                if let InputEvent::Change = event {
                    if let Ok(value) = input.read(cx).value().parse::<f32>() {
                        this.settings.font_size = value.clamp(8.0, 32.0);
                        this.notify_change(window, cx);
                    }
                }
            },
        )
        .detach();

        // Subscribe to line height NumberInput events
        cx.subscribe_in(
            &line_height_input,
            window,
            |this, input, event: &NumberInputEvent, window, cx| {
                if let NumberInputEvent::Step(action) = event {
                    input.update(cx, |input, cx| {
                        let value = input.value().parse::<f32>().unwrap_or(1.4);
                        let new_value = match action {
                            StepAction::Increment => (value + 0.1).min(2.5),
                            StepAction::Decrement => (value - 0.1).max(1.0),
                        };
                        this.settings.line_height = new_value;
                        input.set_value(format!("{:.1}", new_value), window, cx);
                    });
                    this.notify_change(window, cx);
                }
            },
        )
        .detach();

        cx.subscribe_in(
            &line_height_input,
            window,
            |this, input, event: &InputEvent, window, cx| {
                if let InputEvent::Change = event {
                    if let Ok(value) = input.read(cx).value().parse::<f32>() {
                        this.settings.line_height = value.clamp(1.0, 2.5);
                        this.notify_change(window, cx);
                    }
                }
            },
        )
        .detach();

        // Subscribe to letter spacing NumberInput events
        cx.subscribe_in(
            &letter_spacing_input,
            window,
            |this, input, event: &NumberInputEvent, window, cx| {
                if let NumberInputEvent::Step(action) = event {
                    input.update(cx, |input, cx| {
                        let value = input.value().parse::<f32>().unwrap_or(0.0);
                        let new_value = match action {
                            StepAction::Increment => (value + 0.5).min(10.0),
                            StepAction::Decrement => (value - 0.5).max(-2.0),
                        };
                        this.settings.letter_spacing = new_value;
                        input.set_value(format!("{:.1}", new_value), window, cx);
                    });
                    this.notify_change(window, cx);
                }
            },
        )
        .detach();

        cx.subscribe_in(
            &letter_spacing_input,
            window,
            |this, input, event: &InputEvent, window, cx| {
                if let InputEvent::Change = event {
                    if let Ok(value) = input.read(cx).value().parse::<f32>() {
                        this.settings.letter_spacing = value.clamp(-2.0, 10.0);
                        this.notify_change(window, cx);
                    }
                }
            },
        )
        .detach();

        // Subscribe to transparency slider events
        cx.subscribe(&transparency_slider, |this, _, event: &SliderEvent, cx| {
            let SliderEvent::Change(slider_value) = event;
            this.settings.window_transparency = slider_value.start() / 100.0;
            cx.notify();
            // Note: Can't call notify_change here since we don't have window access
            // The slider subscription doesn't provide window in older GPUI versions
        })
        .detach();

        Self {
            focus_handle: cx.focus_handle(),
            tab_index: 0,
            settings,
            original_settings,
            font_size_input,
            line_height_input,
            letter_spacing_input,
            transparency_slider,
            font_select,
            on_close: None,
            on_save: None,
            on_change: None,
        }
    }

    /// Set the close callback.
    pub fn on_close(mut self, callback: impl Fn(&mut Window, &mut App) + 'static) -> Self {
        self.on_close = Some(Box::new(callback));
        self
    }

    /// Set the save callback.
    pub fn on_save(
        mut self,
        callback: impl Fn(AppSettings, &mut Window, &mut App) + 'static,
    ) -> Self {
        self.on_save = Some(Box::new(callback));
        self
    }

    /// Set the change callback for live preview.
    pub fn on_change(
        mut self,
        callback: impl Fn(AppSettings, &mut Window, &mut App) + 'static,
    ) -> Self {
        self.on_change = Some(Box::new(callback));
        self
    }

    /// Notify that settings have changed (for live preview).
    fn notify_change(&mut self, window: &mut Window, cx: &mut App) {
        if let Some(on_change) = self.on_change.as_ref() {
            on_change(self.settings.clone(), window, cx);
        }
    }

    fn handle_close(&mut self, window: &mut Window, cx: &mut App) {
        // Revert to original settings
        if let Some(on_change) = self.on_change.as_ref() {
            on_change(self.original_settings.clone(), window, cx);
        }
        if let Some(on_close) = self.on_close.take() {
            on_close(window, cx);
            self.on_close = Some(on_close);
        }
    }

    fn handle_save(&mut self, window: &mut Window, cx: &mut App) {
        let _ = self.settings.save();
        if let Some(on_save) = self.on_save.take() {
            on_save(self.settings.clone(), window, cx);
            self.on_save = Some(on_save);
        }
        // Close the window after saving
        if let Some(on_close) = self.on_close.take() {
            on_close(window, cx);
            self.on_close = Some(on_close);
        }
    }

    fn render_general_tab(&self, _window: &mut Window, cx: &mut Context<Self>) -> AnyElement {
        div()
            .flex()
            .flex_col()
            .gap(px(16.))
            .child(
                div()
                    .text_lg()
                    .font_weight(gpui::FontWeight::SEMIBOLD)
                    .text_color(cx.theme().foreground)
                    .child("Updates"),
            )
            .child(
                self.render_setting_row(
                    "Check for updates",
                    "Automatically check for new versions",
                    Switch::new("check-updates")
                        .checked(self.settings.check_for_updates)
                        .into_any_element(),
                    cx,
                ),
            )
            .child(
                self.render_setting_row(
                    "Auto update",
                    "Automatically install updates when available",
                    Switch::new("auto-update")
                        .checked(self.settings.auto_update)
                        .into_any_element(),
                    cx,
                ),
            )
            .child(
                div()
                    .mt(px(16.))
                    .text_lg()
                    .font_weight(gpui::FontWeight::SEMIBOLD)
                    .text_color(cx.theme().foreground)
                    .child("About"),
            )
            .child(
                div()
                    .text_sm()
                    .text_color(cx.theme().muted_foreground)
                    .child("AgentTerm v0.1.0"),
            )
            .into_any_element()
    }

    fn render_appearance_tab(&self, _window: &mut Window, cx: &mut Context<Self>) -> AnyElement {
        let transparency_percent = self.transparency_slider.read(cx).value().start();

        div()
            .flex()
            .flex_col()
            .gap(px(16.))
            // Theme section
            .child(
                div()
                    .text_lg()
                    .font_weight(gpui::FontWeight::SEMIBOLD)
                    .text_color(cx.theme().foreground)
                    .child("Theme"),
            )
            .child(self.render_theme_selector(cx))
            // Font section
            .child(
                div()
                    .mt(px(16.))
                    .text_lg()
                    .font_weight(gpui::FontWeight::SEMIBOLD)
                    .text_color(cx.theme().foreground)
                    .child("Terminal Font"),
            )
            .child(self.render_font_selector(cx))
            // Typography section
            .child(
                div()
                    .mt(px(16.))
                    .text_lg()
                    .font_weight(gpui::FontWeight::SEMIBOLD)
                    .text_color(cx.theme().foreground)
                    .child("Typography"),
            )
            .child(
                self.render_setting_row(
                    "Font Size",
                    "8-32 px",
                    NumberInput::new(&self.font_size_input)
                        .suffix(
                            div()
                                .text_xs()
                                .text_color(cx.theme().muted_foreground)
                                .child("px"),
                        )
                        .into_any_element(),
                    cx,
                ),
            )
            .child(self.render_setting_row(
                "Line Height",
                "1.0-2.5",
                NumberInput::new(&self.line_height_input).into_any_element(),
                cx,
            ))
            .child(
                self.render_setting_row(
                    "Letter Spacing",
                    "-2 to 10 px",
                    NumberInput::new(&self.letter_spacing_input)
                        .suffix(
                            div()
                                .text_xs()
                                .text_color(cx.theme().muted_foreground)
                                .child("px"),
                        )
                        .into_any_element(),
                    cx,
                ),
            )
            // Window section
            .child(
                div()
                    .mt(px(16.))
                    .text_lg()
                    .font_weight(gpui::FontWeight::SEMIBOLD)
                    .text_color(cx.theme().foreground)
                    .child("Window"),
            )
            .child(self.render_slider_row(
                "Transparency",
                format!("{:.0}%", transparency_percent),
                Slider::new(&self.transparency_slider).into_any_element(),
                cx,
            ))
            .child(
                self.render_setting_row(
                    "Background Blur",
                    "Enable macOS vibrancy effect",
                    Switch::new("blur-enabled")
                        .checked(self.settings.blur_enabled)
                        .on_click(cx.listener(|this, checked: &bool, window, cx| {
                            this.settings.blur_enabled = *checked;
                            this.notify_change(window, cx);
                        }))
                        .into_any_element(),
                    cx,
                ),
            )
            .into_any_element()
    }

    fn render_tools_tab(&self, _window: &mut Window, cx: &mut Context<Self>) -> AnyElement {
        div()
            .flex()
            .flex_col()
            .gap(px(16.))
            .child(
                div()
                    .text_lg()
                    .font_weight(gpui::FontWeight::SEMIBOLD)
                    .text_color(cx.theme().foreground)
                    .child("Default Shell"),
            )
            .child(
                div()
                    .text_sm()
                    .text_color(cx.theme().muted_foreground)
                    .child(
                        self.settings
                            .default_shell_id
                            .clone()
                            .unwrap_or_else(|| "System default".to_string()),
                    ),
            )
            .child(
                div()
                    .mt(px(16.))
                    .text_lg()
                    .font_weight(gpui::FontWeight::SEMIBOLD)
                    .text_color(cx.theme().foreground)
                    .child("Custom Tools"),
            )
            .child(
                div()
                    .text_sm()
                    .text_color(cx.theme().muted_foreground)
                    .child(format!(
                        "{} custom tools configured",
                        self.settings.custom_tools.len()
                    )),
            )
            .into_any_element()
    }

    fn render_theme_selector(&self, cx: &mut Context<Self>) -> AnyElement {
        let current_theme = &self.settings.theme;

        div()
            .flex()
            .gap(px(8.))
            .child(
                Button::new("theme-light")
                    .label("Light")
                    .map(|b| {
                        if *current_theme == Theme::Light {
                            b.primary()
                        } else {
                            b.ghost()
                        }
                    })
                    .on_click(cx.listener(|this, _, window, cx| {
                        this.settings.theme = Theme::Light;
                        this.notify_change(window, cx);
                    })),
            )
            .child(
                Button::new("theme-dark")
                    .label("Dark")
                    .map(|b| {
                        if *current_theme == Theme::Dark {
                            b.primary()
                        } else {
                            b.ghost()
                        }
                    })
                    .on_click(cx.listener(|this, _, window, cx| {
                        this.settings.theme = Theme::Dark;
                        this.notify_change(window, cx);
                    })),
            )
            .child(
                Button::new("theme-system")
                    .label("System")
                    .map(|b| {
                        if *current_theme == Theme::System {
                            b.primary()
                        } else {
                            b.ghost()
                        }
                    })
                    .on_click(cx.listener(|this, _, window, cx| {
                        this.settings.theme = Theme::System;
                        this.notify_change(window, cx);
                    })),
            )
            .into_any_element()
    }

    fn render_font_selector(&self, _cx: &mut Context<Self>) -> AnyElement {
        Select::new(&self.font_select)
            .placeholder("Select a font...")
            .w(px(280.))
            .into_any_element()
    }

    fn render_setting_row(
        &self,
        label: impl Into<SharedString>,
        description: impl Into<SharedString>,
        control: AnyElement,
        cx: &Context<Self>,
    ) -> AnyElement {
        let label: SharedString = label.into();
        let description: SharedString = description.into();
        div()
            .flex()
            .items_center()
            .justify_between()
            .child(
                div()
                    .flex()
                    .flex_col()
                    .gap(px(2.))
                    .child(
                        div()
                            .text_sm()
                            .text_color(cx.theme().foreground)
                            .child(label),
                    )
                    .child(
                        div()
                            .text_xs()
                            .text_color(cx.theme().muted_foreground)
                            .child(description),
                    ),
            )
            .child(control)
            .into_any_element()
    }

    fn render_slider_row(
        &self,
        label: impl Into<SharedString>,
        value: impl Into<SharedString>,
        control: AnyElement,
        cx: &Context<Self>,
    ) -> AnyElement {
        let label: SharedString = label.into();
        let value: SharedString = value.into();
        div()
            .flex()
            .flex_col()
            .gap(px(8.))
            .child(
                div()
                    .flex()
                    .justify_between()
                    .child(
                        div()
                            .text_sm()
                            .text_color(cx.theme().foreground)
                            .child(label),
                    )
                    .child(div().text_sm().text_color(cx.theme().accent).child(value)),
            )
            .child(control)
            .into_any_element()
    }
}

impl Focusable for SettingsDialog {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus_handle.clone()
    }
}

impl Render for SettingsDialog {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let tab_index = self.tab_index;

        let tab_content = match self.tab_index {
            0 => self.render_general_tab(window, cx),
            1 => self.render_appearance_tab(window, cx),
            2 => self.render_tools_tab(window, cx),
            _ => div().into_any_element(),
        };

        // Render directly as window content (no overlay wrapper)
        div()
            .id("settings-dialog")
            .track_focus(&self.focus_handle)
            .size_full()
            .bg(cx.theme().background)
            .flex()
            .flex_col()
            // Tab bar
            .child(
                div()
                    .px(px(24.))
                    .py(px(12.))
                    .border_b_1()
                    .border_color(cx.theme().border)
                    .child(
                        TabBar::new("settings-tabs")
                            .child(Tab::new().label("General"))
                            .child(Tab::new().label("Appearance"))
                            .child(Tab::new().label("Tools"))
                            .selected_index(tab_index)
                            .on_click(cx.listener(|this, ix: &usize, _, cx| {
                                this.tab_index = *ix;
                                cx.notify();
                            })),
                    ),
            )
            // Content area
            .child(
                div()
                    .id("settings-content")
                    .flex_1()
                    .overflow_y_scroll()
                    .px(px(24.))
                    .py(px(16.))
                    .child(tab_content),
            )
            // Footer with buttons
            .child(
                div()
                    .flex()
                    .items_center()
                    .justify_end()
                    .gap(px(8.))
                    .px(px(24.))
                    .py(px(16.))
                    .border_t_1()
                    .border_color(cx.theme().border)
                    .child(
                        Button::new("cancel-btn")
                            .label("Cancel")
                            .ghost()
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.handle_close(window, cx);
                            })),
                    )
                    .child(
                        Button::new("save-btn")
                            .label("Save")
                            .primary()
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.handle_save(window, cx);
                            })),
                    ),
            )
    }
}
