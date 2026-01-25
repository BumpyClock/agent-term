//! Release notes dialog displaying update notes.

use gpui::{Context, IntoElement, Render, ScrollHandle, Styled, Window, div, prelude::*, px};

use crate::ui::ActiveTheme;

/// Dialog displaying release notes for an update.
pub struct ReleaseNotesDialog {
    version: String,
    notes: String,
    scroll_handle: ScrollHandle,
}

impl ReleaseNotesDialog {
    pub fn new(version: String, notes: String) -> Self {
        Self {
            version,
            notes,
            scroll_handle: ScrollHandle::new(),
        }
    }
}

impl Render for ReleaseNotesDialog {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let muted_foreground = cx.theme().muted_foreground;
        let foreground = cx.theme().foreground;
        let border_color = cx.theme().border;

        div()
            .flex()
            .flex_col()
            .gap(px(12.))
            .max_h(px(400.))
            .child(
                div()
                    .text_sm()
                    .text_color(muted_foreground)
                    .child(format!("Version {}", self.version)),
            )
            .child(
                div()
                    .flex_1()
                    .overflow_y_hidden()
                    .child(
                        div()
                            .id("release-notes-scroll")
                            .h_full()
                            .overflow_y_scroll()
                            .track_scroll(&self.scroll_handle)
                            .p(px(12.))
                            .rounded(px(6.))
                            .border_1()
                            .border_color(border_color)
                            .bg(cx.theme().muted.opacity(0.3))
                            .child(
                                div()
                                    .text_sm()
                                    .text_color(foreground)
                                    .child(self.notes.clone()),
                            ),
                    ),
            )
    }
}
