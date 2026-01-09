//! Tab picker dialog for selecting shells and tools to create new tabs.

use std::sync::Arc;

use agentterm_mcp::McpManager;
use agentterm_session::SessionTool;
use agentterm_tools::{ShellInfo, ShellType, ToolInfo};
use gpui::{
    ClickEvent, Context, Entity, IntoElement, MouseButton, MouseDownEvent, ParentElement, Render,
    SharedString, Styled, Window, div, prelude::*, px,
};

use crate::ui::{ActiveTheme, Divider, WindowExt, v_flex};

use super::AgentTermApp;

/// TabPickerDialog - A dialog for selecting shells and tools to create new tabs.
/// This is rendered as an Entity inside the gpui-component Dialog system.
pub struct TabPickerDialog {
    view: Entity<AgentTermApp>,
    tokio: Arc<tokio::runtime::Runtime>,
    mcp_manager: McpManager,
    loading: bool,
    error: Option<SharedString>,
    tools: Vec<ToolInfo>,
    shells: Vec<ShellInfo>,
    pinned_shell_ids: Vec<String>,
}

impl TabPickerDialog {
    pub fn new(
        view: Entity<AgentTermApp>,
        tokio: Arc<tokio::runtime::Runtime>,
        mcp_manager: McpManager,
    ) -> Self {
        Self {
            view,
            tokio,
            mcp_manager,
            loading: true,
            error: None,
            tools: Vec::new(),
            shells: Vec::new(),
            pinned_shell_ids: Vec::new(),
        }
    }

    pub fn load_data(&mut self) {
        self.loading = true;
        self.error = None;

        let tools = match self.tokio.block_on(agentterm_tools::tools_list(&self.mcp_manager)) {
            Ok(list) => list.into_iter().filter(|t| t.enabled).collect(),
            Err(e) => {
                self.error = Some(e.to_string().into());
                Vec::new()
            }
        };

        let pinned = match self
            .tokio
            .block_on(agentterm_tools::get_pinned_shells(&self.mcp_manager))
        {
            Ok(list) => list,
            Err(e) => {
                self.error = Some(e.to_string().into());
                Vec::new()
            }
        };

        self.tools = tools;
        self.shells = agentterm_tools::available_shells();
        self.pinned_shell_ids = pinned;
        self.loading = false;
    }

    fn toggle_pin(&mut self, shell_id: String, cx: &mut Context<Self>) {
        match self
            .tokio
            .block_on(agentterm_tools::toggle_pin_shell(&self.mcp_manager, shell_id))
        {
            Ok(pinned) => self.pinned_shell_ids = pinned,
            Err(e) => self.error = Some(e.to_string().into()),
        }
        cx.notify();
    }

    fn select_tool(&self, tool: ToolInfo, window: &mut Window, cx: &mut Context<Self>) {
        let session_tool = if tool.is_builtin {
            match tool.id.as_str() {
                "claude" => SessionTool::Claude,
                "gemini" => SessionTool::Gemini,
                "codex" => SessionTool::Codex,
                "openCode" => SessionTool::OpenCode,
                _ => SessionTool::Custom(tool.id.clone()),
            }
        } else {
            SessionTool::Custom(tool.id.clone())
        };

        let icon = if tool.icon.is_empty() {
            None
        } else {
            Some(tool.icon.clone())
        };

        window.close_dialog(cx);

        self.view.update(cx, |app, cx| {
            app.create_session_from_tool(
                session_tool,
                tool.name.clone(),
                tool.command.clone(),
                tool.args.clone(),
                icon,
                window,
                cx,
            );
        });
    }

    fn select_shell(&self, shell: ShellInfo, window: &mut Window, cx: &mut Context<Self>) {
        let icon = if shell.icon.is_empty() {
            None
        } else {
            Some(shell.icon.clone())
        };

        window.close_dialog(cx);

        self.view.update(cx, |app, cx| {
            app.create_session_from_tool(
                SessionTool::Shell,
                shell.name.clone(),
                shell.command.clone(),
                shell.args.clone(),
                icon,
                window,
                cx,
            );
        });
    }

    fn render_tool_row(&self, tool: ToolInfo, cx: &mut Context<Self>) -> impl IntoElement {
        let label = tool.name.clone();
        let icon_letter = label.chars().next().unwrap_or('T').to_string();
        let hover_bg = cx.theme().list_hover;
        let icon_bg = cx.theme().list_hover;

        div()
            .id(format!("tab-picker-tool-{}", tool.id))
            .px(px(10.0))
            .py(px(8.0))
            .flex()
            .items_center()
            .gap(px(10.0))
            .rounded(px(8.0))
            .cursor_pointer()
            .hover(move |s| s.bg(hover_bg))
            .on_click(cx.listener({
                let tool = tool.clone();
                move |this, _: &ClickEvent, window, cx| {
                    this.select_tool(tool.clone(), window, cx);
                }
            }))
            .child(
                div()
                    .w(px(22.0))
                    .h(px(22.0))
                    .rounded(px(6.0))
                    .bg(icon_bg)
                    .flex()
                    .items_center()
                    .justify_center()
                    .text_color(cx.theme().foreground)
                    .text_sm()
                    .child(icon_letter),
            )
            .child(
                div()
                    .text_sm()
                    .text_color(cx.theme().foreground)
                    .truncate()
                    .child(label),
            )
    }

    fn render_shell_row(
        &self,
        shell: ShellInfo,
        pinned: bool,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let label = shell.name.clone();
        let icon_letter = label.chars().next().unwrap_or('S').to_string();
        let shell_id = shell.id.clone();
        let hover_bg = cx.theme().list_hover;
        let icon_bg = cx.theme().list_hover;
        let pin_hover_bg = cx.theme().list_hover;
        let pin_hover_fg = cx.theme().foreground;

        div()
            .id(format!("tab-picker-shell-{}", shell.id))
            .px(px(10.0))
            .py(px(8.0))
            .flex()
            .items_center()
            .justify_between()
            .rounded(px(8.0))
            .hover(move |s| s.bg(hover_bg))
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap(px(10.0))
                    .flex_1()
                    .cursor_pointer()
                    .on_mouse_down(
                        MouseButton::Left,
                        cx.listener({
                            let shell = shell.clone();
                            move |this, _: &MouseDownEvent, window, cx| {
                                this.select_shell(shell.clone(), window, cx);
                            }
                        }),
                    )
                    .child(
                        div()
                            .w(px(22.0))
                            .h(px(22.0))
                            .rounded(px(6.0))
                            .bg(icon_bg)
                            .flex()
                            .items_center()
                            .justify_center()
                            .text_color(cx.theme().foreground)
                            .text_sm()
                            .child(icon_letter),
                    )
                    .child(
                        div()
                            .text_sm()
                            .text_color(cx.theme().foreground)
                            .truncate()
                            .child(label),
                    ),
            )
            .child(
                div()
                    .w(px(22.0))
                    .h(px(22.0))
                    .flex()
                    .items_center()
                    .justify_center()
                    .rounded(px(6.0))
                    .cursor_pointer()
                    .text_color(cx.theme().muted_foreground)
                    .hover(move |s| s.text_color(pin_hover_fg).bg(pin_hover_bg))
                    .child(if pinned { "★" } else { "☆" })
                    .on_mouse_down(
                        MouseButton::Left,
                        cx.listener(move |this, _: &MouseDownEvent, _w, cx| {
                            this.toggle_pin(shell_id.clone(), cx);
                        }),
                    ),
            )
    }
}

impl Render for TabPickerDialog {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let pinned_set: std::collections::HashSet<&str> = self
            .pinned_shell_ids
            .iter()
            .map(|s| s.as_str())
            .collect();

        let mut pinned_shells: Vec<ShellInfo> = self
            .shells
            .iter()
            .cloned()
            .filter(|s| pinned_set.contains(s.id.as_str()))
            .collect();
        pinned_shells.sort_by(|a, b| a.name.cmp(&b.name));

        let mut native_shells: Vec<ShellInfo> = self
            .shells
            .iter()
            .cloned()
            .filter(|s| s.shell_type == ShellType::Native && !pinned_set.contains(s.id.as_str()))
            .collect();
        native_shells.sort_by(|a, b| a.name.cmp(&b.name));

        let mut wsl_shells: Vec<ShellInfo> = self
            .shells
            .iter()
            .cloned()
            .filter(|s| s.shell_type == ShellType::Wsl && !pinned_set.contains(s.id.as_str()))
            .collect();
        wsl_shells.sort_by(|a, b| a.name.cmp(&b.name));

        let builtin_tools: Vec<ToolInfo> = self
            .tools
            .iter()
            .cloned()
            .filter(|t| t.is_builtin)
            .collect();

        let custom_tools: Vec<ToolInfo> = self
            .tools
            .iter()
            .cloned()
            .filter(|t| !t.is_builtin)
            .collect();

        let mut body = v_flex().gap(px(6.0));

        if self.loading {
            body = body.child(
                div()
                    .py(px(10.0))
                    .text_sm()
                    .text_color(cx.theme().muted_foreground)
                    .child("Loading..."),
            );
        } else {
            if !pinned_shells.is_empty() {
                body = body.child(
                    div()
                        .pt(px(8.0))
                        .text_xs()
                        .text_color(cx.theme().muted_foreground)
                        .child("Pinned shells"),
                );
                for shell in pinned_shells {
                    body = body.child(self.render_shell_row(shell, true, cx));
                }
                body = body.child(Divider::horizontal().pt(px(2.0)));
            }

            body = body.child(
                div()
                    .pt(px(8.0))
                    .text_xs()
                    .text_color(cx.theme().muted_foreground)
                    .child("Shells"),
            );
            for shell in native_shells {
                body = body.child(self.render_shell_row(shell, false, cx));
            }
            for shell in wsl_shells {
                body = body.child(self.render_shell_row(shell, false, cx));
            }

            body = body.child(Divider::horizontal().pt(px(2.0)));

            body = body.child(
                div()
                    .pt(px(8.0))
                    .text_xs()
                    .text_color(cx.theme().muted_foreground)
                    .child("Tools"),
            );

            for tool in builtin_tools {
                body = body.child(self.render_tool_row(tool, cx));
            }

            if !custom_tools.is_empty() {
                body = body.child(Divider::horizontal().pt(px(2.0)));
                body = body.child(
                    div()
                        .pt(px(8.0))
                        .text_xs()
                        .text_color(cx.theme().muted_foreground)
                        .child("Custom tools"),
                );
                for tool in custom_tools {
                    body = body.child(self.render_tool_row(tool, cx));
                }
            }
        }

        body.when_some(self.error.as_ref(), |el, err| {
            el.child(
                div()
                    .pt(px(10.0))
                    .text_sm()
                    .text_color(cx.theme().danger)
                    .child(err.clone()),
            )
        })
    }
}
