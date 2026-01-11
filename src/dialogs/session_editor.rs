//! Session editor dialog for editing session properties: icon, name, command.

use gpui::{Context, Entity, IntoElement, Render, Styled, Window, div, prelude::*, px};
use gpui_component::input::InputState as GpuiInputState;

use crate::icons::IconDescriptor;
use crate::ui::{
    ActiveTheme, IconPicker, WindowExt,
    helpers::{agentterm_input_field, icon_descriptor_from_string, icon_descriptor_to_string},
    v_flex,
};

use super::AgentTermApp;

/// Dialog for editing session properties: icon, name, command
pub struct SessionEditorDialog {
    view: Entity<AgentTermApp>,
    session_id: String,
    name_input: Entity<GpuiInputState>,
    command_input: Entity<GpuiInputState>,
    current_icon: Option<String>,
}

impl SessionEditorDialog {
    pub fn new(
        view: Entity<AgentTermApp>,
        session_id: String,
        name_input: Entity<GpuiInputState>,
        command_input: Entity<GpuiInputState>,
        current_icon: Option<String>,
    ) -> Self {
        Self {
            view,
            session_id,
            name_input,
            command_input,
            current_icon,
        }
    }

    pub fn set_icon(&mut self, icon: Option<IconDescriptor>, cx: &mut Context<Self>) {
        self.current_icon = icon.map(|d| icon_descriptor_to_string(&d));
        cx.notify();
    }

    pub fn save(&self, window: &mut Window, cx: &mut Context<Self>) {
        let name = self
            .name_input
            .read(cx)
            .value()
            .to_string()
            .trim()
            .to_string();
        let command = self
            .command_input
            .read(cx)
            .value()
            .to_string()
            .trim()
            .to_string();

        if name.is_empty() {
            return;
        }

        window.close_dialog(cx);

        let view = self.view.clone();
        let session_id = self.session_id.clone();
        let icon = self.current_icon.clone();

        view.update(cx, |app, cx| {
            let _ = app.session_store.rename_session(&session_id, name, true);
            if !command.is_empty() {
                let _ = app.session_store.set_session_command(&session_id, command);
            }
            let _ = app.session_store.set_session_icon(&session_id, icon);
            app.reload_from_store(cx);
            app.ensure_active_terminal(window, cx);
        });
    }
}

impl Render for SessionEditorDialog {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let current_icon = self.current_icon.clone();
        let entity = cx.entity().clone();

        v_flex()
            .gap(px(16.))
            // Icon section
            .child(
                div()
                    .flex()
                    .flex_col()
                    .gap(px(8.))
                    .child(
                        div()
                            .text_sm()
                            .text_color(cx.theme().muted_foreground)
                            .child("Icon"),
                    )
                    .child(
                        IconPicker::new("session-icon-picker")
                            .value(
                                current_icon
                                    .as_ref()
                                    .map(|s| icon_descriptor_from_string(s)),
                            )
                            .on_change(move |icon, _window, cx| {
                                entity.update(cx, |this, cx| {
                                    this.set_icon(icon, cx);
                                });
                            }),
                    ),
            )
            // Name section
            .child(
                div()
                    .flex()
                    .flex_col()
                    .gap(px(8.))
                    .child(
                        div()
                            .text_sm()
                            .text_color(cx.theme().muted_foreground)
                            .child("Name"),
                    )
                    .child(agentterm_input_field(&self.name_input)),
            )
            // Command section
            .child(
                div()
                    .flex()
                    .flex_col()
                    .gap(px(8.))
                    .child(
                        div()
                            .text_sm()
                            .text_color(cx.theme().muted_foreground)
                            .child("Command"),
                    )
                    .child(agentterm_input_field(&self.command_input)),
            )
    }
}
