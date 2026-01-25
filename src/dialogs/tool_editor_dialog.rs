//! Tool editor dialog for adding/editing custom tools.

use gpui::{Context, Entity, FocusHandle, IntoElement, Render, Styled, Window, div, prelude::*, px};
use gpui_component::input::InputState as GpuiInputState;

use crate::icons::IconDescriptor;
use crate::settings::CustomTool;
use crate::ui::{
    ActiveTheme, IconPicker, WindowExt,
    helpers::{agentterm_input_field, icon_descriptor_from_string, icon_descriptor_to_string},
};

/// Dialog for adding/editing a custom tool.
pub struct ToolEditorDialog {
    /// Tool ID (None for new tools)
    tool_id: Option<String>,
    name_input: Entity<GpuiInputState>,
    command_input: Entity<GpuiInputState>,
    args_input: Entity<GpuiInputState>,
    description_input: Entity<GpuiInputState>,
    current_icon: Option<String>,
    on_save: Option<Box<dyn Fn(CustomTool, &mut Window, &mut gpui::App) + 'static>>,
    focus_handle: FocusHandle,
}

impl ToolEditorDialog {
    pub fn new(
        tool: Option<&CustomTool>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let (tool_id, name, command, args, description, icon) = if let Some(t) = tool {
            (
                Some(t.id.clone()),
                t.name.clone(),
                t.command.clone(),
                t.args.join(" "),
                t.description.clone().unwrap_or_default(),
                t.icon.clone(),
            )
        } else {
            (None, String::new(), String::new(), String::new(), String::new(), None)
        };

        let name_input = cx.new(|cx| {
            GpuiInputState::new(window, cx)
                .placeholder("Tool name")
                .default_value(name)
        });
        let command_input = cx.new(|cx| {
            GpuiInputState::new(window, cx)
                .placeholder("Command (e.g., /usr/bin/mytool)")
                .default_value(command)
        });
        let args_input = cx.new(|cx| {
            GpuiInputState::new(window, cx)
                .placeholder("Arguments (space-separated)")
                .default_value(args)
        });
        let description_input = cx.new(|cx| {
            GpuiInputState::new(window, cx)
                .placeholder("Description (optional)")
                .default_value(description)
        });

        Self {
            tool_id,
            name_input,
            command_input,
            args_input,
            description_input,
            current_icon: icon,
            on_save: None,
            focus_handle: cx.focus_handle(),
        }
    }

    pub fn on_save(
        mut self,
        callback: impl Fn(CustomTool, &mut Window, &mut gpui::App) + 'static,
    ) -> Self {
        self.on_save = Some(Box::new(callback));
        self
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
        let args_str = self
            .args_input
            .read(cx)
            .value()
            .to_string()
            .trim()
            .to_string();
        let description = self
            .description_input
            .read(cx)
            .value()
            .to_string()
            .trim()
            .to_string();

        if name.is_empty() || command.is_empty() {
            return;
        }

        let args: Vec<String> = if args_str.is_empty() {
            Vec::new()
        } else {
            shell_words::split(&args_str).unwrap_or_else(|_| {
                args_str.split_whitespace().map(String::from).collect()
            })
        };

        let tool_id = self
            .tool_id
            .clone()
            .unwrap_or_else(|| format!("custom-{}", uuid::Uuid::new_v4()));

        let tool = CustomTool {
            id: tool_id,
            name,
            command,
            args,
            icon: self.current_icon.clone(),
            description: if description.is_empty() {
                None
            } else {
                Some(description)
            },
            is_shell: false,
            enabled: true,
        };

        window.close_dialog(cx);

        if let Some(on_save) = self.on_save.as_ref() {
            on_save(tool, window, cx);
        }
    }

    pub fn focus_handle(&self) -> &FocusHandle {
        &self.focus_handle
    }
}

impl Render for ToolEditorDialog {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let current_icon = self.current_icon.clone();
        let entity = cx.entity();
        let muted_foreground = cx.theme().muted_foreground;

        div()
            .track_focus(&self.focus_handle)
            .flex()
            .flex_col()
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
                            .text_color(muted_foreground)
                            .child("Icon"),
                    )
                    .child(
                        IconPicker::new("tool-icon-picker")
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
                            .text_color(muted_foreground)
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
                            .text_color(muted_foreground)
                            .child("Command"),
                    )
                    .child(agentterm_input_field(&self.command_input)),
            )
            // Args section
            .child(
                div()
                    .flex()
                    .flex_col()
                    .gap(px(8.))
                    .child(
                        div()
                            .text_sm()
                            .text_color(muted_foreground)
                            .child("Arguments"),
                    )
                    .child(agentterm_input_field(&self.args_input)),
            )
            // Description section
            .child(
                div()
                    .flex()
                    .flex_col()
                    .gap(px(8.))
                    .child(
                        div()
                            .text_sm()
                            .text_color(muted_foreground)
                            .child("Description"),
                    )
                    .child(agentterm_input_field(&self.description_input)),
            )
    }
}
