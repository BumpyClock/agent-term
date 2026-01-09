//! Project editor dialog for editing project name, path, and icon.

use gpui::{Context, Entity, IntoElement, Render, Styled, Window, div, prelude::*, px};
use gpui_component::input::InputState as GpuiInputState;

use crate::icons::IconDescriptor;
use crate::ui::{
    helpers::{agentterm_input_field, icon_descriptor_from_string, icon_descriptor_to_string},
    ActiveTheme, IconPicker, v_flex, WindowExt,
};

use super::AgentTermApp;

/// ProjectEditorDialog - A dialog for editing project name, path, and icon.
pub struct ProjectEditorDialog {
    view: Entity<AgentTermApp>,
    section_id: String,
    name_input: Entity<GpuiInputState>,
    path_input: Entity<GpuiInputState>,
    current_icon: Option<String>,
}

impl ProjectEditorDialog {
    pub fn new(
        view: Entity<AgentTermApp>,
        section_id: String,
        name_input: Entity<GpuiInputState>,
        path_input: Entity<GpuiInputState>,
        current_icon: Option<String>,
    ) -> Self {
        Self {
            view,
            section_id,
            name_input,
            path_input,
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
        let path = self
            .path_input
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
        let section_id = self.section_id.clone();
        let icon = self.current_icon.clone();
        view.update(cx, |app, cx| {
            let _ = app.session_store.rename_section(&section_id, name);
            let _ = app.session_store.set_section_path(&section_id, path);
            let _ = app.session_store.set_section_icon(&section_id, icon);
            app.reload_from_store(cx);
            app.ensure_active_terminal(window, cx);
        });
    }
}

impl Render for ProjectEditorDialog {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let current_icon = self.current_icon.clone();
        let entity = cx.entity().clone();

        v_flex()
            .gap(px(16.))
            // Icon section with inline IconPicker
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
                        IconPicker::new("project-icon-picker")
                            .value(current_icon.as_ref().map(|s| icon_descriptor_from_string(s)))
                            .on_change(move |icon, _window, cx| {
                                entity.update(cx, |this, cx| {
                                    this.set_icon(icon, cx);
                                });
                            }),
                    ),
            )
            // Name input
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
            // Path input
            .child(
                div()
                    .flex()
                    .flex_col()
                    .gap(px(8.))
                    .child(
                        div()
                            .text_sm()
                            .text_color(cx.theme().muted_foreground)
                            .child("Path"),
                    )
                    .child(agentterm_input_field(&self.path_input)),
            )
    }
}
