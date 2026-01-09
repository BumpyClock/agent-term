//! Terminal container rendering.

use gpui::{Context, IntoElement, ParentElement, Styled, div, prelude::*, px};

use crate::ui::ActiveTheme;

use super::constants::*;
use super::state::AgentTermApp;

impl AgentTermApp {
    pub fn render_terminal_container(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let content_left = if self.sidebar_visible {
            self.sidebar_width + SIDEBAR_INSET + SIDEBAR_GAP
        } else {
            0.0
        };

        let active_view = self
            .active_session_id
            .as_ref()
            .and_then(|id| self.terminal_views.get(id))
            .cloned();

        div()
            .id("terminal-container")
            .absolute()
            .top_0()
            .right_0()
            .bottom_0()
            .left(px(content_left))
            .flex()
            .flex_col()
            .when_some(active_view.as_ref(), |el, tv| {
                el.child(
                    div()
                        .flex_1()
                        .overflow_hidden()
                        .py(px(16.0))
                        .px(px(8.0))
                        .child(tv.clone()),
                )
            })
            .when(active_view.is_none(), |el| {
                el.flex().items_center().justify_center().child(
                    div()
                        .text_color(cx.theme().muted_foreground)
                        .child("No terminal selected"),
                )
            })
    }
}
