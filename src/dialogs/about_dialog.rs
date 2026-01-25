//! About dialog displaying application information.

use gpui::{Context, IntoElement, Render, Styled, Window, div, prelude::*, px};

use crate::icons::{Icon, IconName, IconSize};
use crate::ui::ActiveTheme;

/// Dialog displaying application information.
pub struct AboutDialog;

impl AboutDialog {
    pub fn new() -> Self {
        Self
    }
}

impl Render for AboutDialog {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let version = env!("CARGO_PKG_VERSION");
        let foreground = cx.theme().foreground;
        let muted_foreground = cx.theme().muted_foreground;

        div()
            .flex()
            .flex_col()
            .items_center()
            .gap(px(16.))
            .p(px(24.))
            .child(
                Icon::new(IconName::Terminal)
                    .size(IconSize::XLarge)
                    .color(cx.theme().accent),
            )
            .child(
                div()
                    .text_xl()
                    .font_weight(gpui::FontWeight::BOLD)
                    .text_color(foreground)
                    .child("Agent Term"),
            )
            .child(
                div()
                    .text_sm()
                    .text_color(muted_foreground)
                    .child(format!("Version {}", version)),
            )
            .child(
                div()
                    .mt(px(8.))
                    .text_xs()
                    .text_color(muted_foreground)
                    .text_center()
                    .child("A cross-platform terminal emulator optimized for agentic coding workflows."),
            )
            .child(
                div()
                    .mt(px(16.))
                    .text_xs()
                    .text_color(muted_foreground)
                    .child("github.com/BumpyClock/agent-term"),
            )
    }
}
