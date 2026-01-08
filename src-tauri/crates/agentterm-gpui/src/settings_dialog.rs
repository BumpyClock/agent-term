//! Settings dialog component for AgentTerm.
//!
//! Opens in its own window with 3 tabs: General, Appearance, and Tools.

use gpui::{
    div, prelude::*, px, rgb, AnyElement, App, Context, Entity, FocusHandle, Focusable,
    InteractiveElement, IntoElement, ParentElement, Render, SharedString,
    StatefulInteractiveElement, Styled, Window,
};

use crate::settings::{AppSettings, Theme};
use crate::ui::{Slider, SliderEvent, SliderState, Switch, Tab, TabBar};

/// Colors for the settings dialog.
const DIALOG_BG: u32 = 0x1a1a1a;
const DIALOG_BORDER: u32 = 0x3a3a3a;
const TEXT_PRIMARY: u32 = 0xd8d8d8;
const TEXT_MUTED: u32 = 0xa6a6a6;
const ACCENT: u32 = 0x5eead4;
const BUTTON_BG: u32 = 0x2a2a2a;
const BUTTON_HOVER: u32 = 0x3a3a3a;

/// Settings dialog state.
pub struct SettingsDialog {
    focus_handle: FocusHandle,
    tab_index: usize,
    settings: AppSettings,
    font_size_slider: Entity<SliderState>,
    line_height_slider: Entity<SliderState>,
    letter_spacing_slider: Entity<SliderState>,
    on_close: Option<Box<dyn Fn(&mut Window, &mut App) + 'static>>,
    on_save: Option<Box<dyn Fn(AppSettings, &mut Window, &mut App) + 'static>>,
}

impl SettingsDialog {
    /// Create a new settings dialog.
    pub fn new(settings: AppSettings, window: &mut Window, cx: &mut Context<Self>) -> Self {
        let font_size_slider = cx.new(|_| {
            SliderState::new()
                .min(8.0)
                .max(32.0)
                .step(1.0)
                .default_value(settings.font_size)
        });
        let line_height_slider = cx.new(|_| {
            SliderState::new()
                .min(1.0)
                .max(2.0)
                .step(0.1)
                .default_value(settings.line_height)
        });
        let letter_spacing_slider = cx.new(|_| {
            SliderState::new()
                .min(-2.0)
                .max(5.0)
                .step(0.5)
                .default_value(settings.letter_spacing)
        });

        cx.subscribe(&font_size_slider, |this, _, event: &SliderEvent, cx| {
            let SliderEvent::Change(slider_value) = event;
            this.settings.font_size = slider_value.start();
            cx.notify();
        })
        .detach();

        cx.subscribe(&line_height_slider, |this, _, event: &SliderEvent, cx| {
            let SliderEvent::Change(slider_value) = event;
            this.settings.line_height = slider_value.start();
            cx.notify();
        })
        .detach();

        cx.subscribe(
            &letter_spacing_slider,
            |this, _, event: &SliderEvent, cx| {
                let SliderEvent::Change(slider_value) = event;
                this.settings.letter_spacing = slider_value.start();
                cx.notify();
            },
        )
        .detach();

        Self {
            focus_handle: cx.focus_handle(),
            tab_index: 0,
            settings,
            font_size_slider,
            line_height_slider,
            letter_spacing_slider,
            on_close: None,
            on_save: None,
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

    fn handle_close(&mut self, window: &mut Window, cx: &mut App) {
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
    }

    fn render_general_tab(&self, _window: &mut Window, _cx: &mut App) -> AnyElement {
        div()
            .flex()
            .flex_col()
            .gap(px(16.))
            .child(
                div()
                    .text_lg()
                    .font_weight(gpui::FontWeight::SEMIBOLD)
                    .text_color(rgb(TEXT_PRIMARY))
                    .child("Updates"),
            )
            .child(self.render_setting_row(
                "Check for updates",
                "Automatically check for new versions",
                Switch::new("check-updates")
                    .checked(self.settings.check_for_updates)
                    .into_any_element(),
            ))
            .child(self.render_setting_row(
                "Auto update",
                "Automatically install updates when available",
                Switch::new("auto-update")
                    .checked(self.settings.auto_update)
                    .into_any_element(),
            ))
            .child(
                div()
                    .mt(px(16.))
                    .text_lg()
                    .font_weight(gpui::FontWeight::SEMIBOLD)
                    .text_color(rgb(TEXT_PRIMARY))
                    .child("About"),
            )
            .child(
                div()
                    .text_sm()
                    .text_color(rgb(TEXT_MUTED))
                    .child("AgentTerm v0.1.0"),
            )
            .into_any_element()
    }

    fn render_appearance_tab(&self, _window: &mut Window, cx: &mut App) -> AnyElement {
        let font_size = self.font_size_slider.read(cx).value().start();
        let line_height = self.line_height_slider.read(cx).value().start();
        let letter_spacing = self.letter_spacing_slider.read(cx).value().start();

        div()
            .flex()
            .flex_col()
            .gap(px(16.))
            .child(
                div()
                    .text_lg()
                    .font_weight(gpui::FontWeight::SEMIBOLD)
                    .text_color(rgb(TEXT_PRIMARY))
                    .child("Theme"),
            )
            .child(self.render_theme_selector())
            .child(
                div()
                    .mt(px(16.))
                    .text_lg()
                    .font_weight(gpui::FontWeight::SEMIBOLD)
                    .text_color(rgb(TEXT_PRIMARY))
                    .child("Typography"),
            )
            .child(self.render_slider_row(
                "Font Size",
                format!("{:.0}px", font_size),
                Slider::new(&self.font_size_slider).into_any_element(),
            ))
            .child(self.render_slider_row(
                "Line Height",
                format!("{:.1}", line_height),
                Slider::new(&self.line_height_slider).into_any_element(),
            ))
            .child(self.render_slider_row(
                "Letter Spacing",
                format!("{:.1}px", letter_spacing),
                Slider::new(&self.letter_spacing_slider).into_any_element(),
            ))
            .into_any_element()
    }

    fn render_tools_tab(&self, _window: &mut Window, _cx: &mut App) -> AnyElement {
        div()
            .flex()
            .flex_col()
            .gap(px(16.))
            .child(
                div()
                    .text_lg()
                    .font_weight(gpui::FontWeight::SEMIBOLD)
                    .text_color(rgb(TEXT_PRIMARY))
                    .child("Default Shell"),
            )
            .child(
                div()
                    .text_sm()
                    .text_color(rgb(TEXT_MUTED))
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
                    .text_color(rgb(TEXT_PRIMARY))
                    .child("Custom Tools"),
            )
            .child(
                div()
                    .text_sm()
                    .text_color(rgb(TEXT_MUTED))
                    .child(format!("{} custom tools configured", self.settings.custom_tools.len())),
            )
            .into_any_element()
    }

    fn render_theme_selector(&self) -> AnyElement {
        let current_theme = &self.settings.theme;
        let themes = [
            (Theme::Light, "Light"),
            (Theme::Dark, "Dark"),
            (Theme::System, "System"),
        ];

        div()
            .flex()
            .gap(px(8.))
            .children(themes.into_iter().map(|(theme, label)| {
                let is_selected = *current_theme == theme;
                div()
                    .id(SharedString::from(format!("theme-{:?}", theme)))
                    .px(px(16.))
                    .py(px(8.))
                    .rounded(px(6.))
                    .cursor_pointer()
                    .bg(if is_selected {
                        rgb(ACCENT)
                    } else {
                        rgb(BUTTON_BG)
                    })
                    .text_color(if is_selected {
                        rgb(0x000000)
                    } else {
                        rgb(TEXT_PRIMARY)
                    })
                    .hover(|s| {
                        if !is_selected {
                            s.bg(rgb(BUTTON_HOVER))
                        } else {
                            s
                        }
                    })
                    .child(label)
            }))
            .into_any_element()
    }

    fn render_setting_row(
        &self,
        label: impl Into<SharedString>,
        description: impl Into<SharedString>,
        control: AnyElement,
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
                    .child(div().text_sm().text_color(rgb(TEXT_PRIMARY)).child(label))
                    .child(
                        div()
                            .text_xs()
                            .text_color(rgb(TEXT_MUTED))
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
                    .child(div().text_sm().text_color(rgb(TEXT_PRIMARY)).child(label))
                    .child(div().text_sm().text_color(rgb(ACCENT)).child(value)),
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
            .bg(rgb(DIALOG_BG))
            .flex()
            .flex_col()
            // Tab bar
            .child(
                div()
                    .px(px(24.))
                    .py(px(12.))
                    .border_b_1()
                    .border_color(rgb(DIALOG_BORDER))
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
                    .border_color(rgb(DIALOG_BORDER))
                    .child(
                        div()
                            .id("cancel-btn")
                            .px(px(16.))
                            .py(px(8.))
                            .rounded(px(6.))
                            .cursor_pointer()
                            .bg(rgb(BUTTON_BG))
                            .text_color(rgb(TEXT_PRIMARY))
                            .hover(|s| s.bg(rgb(BUTTON_HOVER)))
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.handle_close(window, cx);
                            }))
                            .child("Cancel"),
                    )
                    .child(
                        div()
                            .id("save-btn")
                            .px(px(16.))
                            .py(px(8.))
                            .rounded(px(6.))
                            .cursor_pointer()
                            .bg(rgb(ACCENT))
                            .text_color(rgb(0x000000))
                            .font_weight(gpui::FontWeight::MEDIUM)
                            .hover(|s| s.opacity(0.9))
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.handle_save(window, cx);
                            }))
                            .child("Save"),
                    ),
            )
    }
}
