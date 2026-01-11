//! MCP Manager dialog for attaching/detaching MCP servers.

use std::sync::Arc;

use agentterm_mcp::{McpManager, McpScope};
use gpui::{
    ClickEvent, Context, IntoElement, ParentElement, Render, SharedString, Styled, Window, div,
    prelude::*, px,
};
use gpui_component::Size as ComponentSize;

use crate::ui::{ActiveTheme, Sizable, Tab, TabBar, v_flex};

#[derive(Clone)]
pub struct McpItem {
    pub name: SharedString,
    pub description: SharedString,
    pub transport: SharedString,
    pub is_orphan: bool,
}

/// McpManagerDialog - A dialog for managing MCP (Model Context Protocol) servers.
/// Allows attaching/detaching MCPs at Global or Local (project) scope.
pub struct McpManagerDialog {
    tokio: Arc<tokio::runtime::Runtime>,
    mcp_manager: McpManager,
    scope: McpScope,
    attached: Vec<SharedString>,
    available: Vec<McpItem>,
    error: Option<SharedString>,
    session_title: String,
    project_path: Option<String>,
    has_project: bool,
}

impl McpManagerDialog {
    pub fn new(
        tokio: Arc<tokio::runtime::Runtime>,
        mcp_manager: McpManager,
        session_title: String,
        project_path: Option<String>,
    ) -> Self {
        let has_project = project_path.is_some();
        Self {
            tokio,
            mcp_manager,
            scope: McpScope::Global,
            attached: Vec::new(),
            available: Vec::new(),
            error: None,
            session_title,
            project_path,
            has_project,
        }
    }

    pub fn load_data(&mut self) {
        if self.scope == McpScope::Local && self.project_path.is_none() {
            self.error = Some("Project path is required for local MCPs".into());
            self.attached.clear();
            self.available.clear();
            return;
        }

        let attached = self
            .tokio
            .block_on(
                self.mcp_manager
                    .get_attached_mcps(self.scope, self.project_path.as_deref()),
            )
            .unwrap_or_default();

        let available = self
            .tokio
            .block_on(self.mcp_manager.get_available_mcps())
            .unwrap_or_default();

        let mut items: Vec<McpItem> = available
            .iter()
            .map(|(name, def)| McpItem {
                name: name.clone().into(),
                description: def.description.clone().into(),
                transport: resolve_transport(def).into(),
                is_orphan: false,
            })
            .collect();
        items.sort_by(|a, b| a.name.cmp(&b.name));

        self.attached = attached.into_iter().map(SharedString::from).collect();
        self.available = items;
        self.error = None;
    }

    fn set_scope(&mut self, scope: McpScope, cx: &mut Context<Self>) {
        self.scope = scope;
        self.load_data();
        cx.notify();
    }

    fn attach(&mut self, name: SharedString, cx: &mut Context<Self>) {
        if self.scope == McpScope::Local && self.project_path.is_none() {
            self.error = Some("Project path is required for local MCPs".into());
            cx.notify();
            return;
        }
        let res = self.tokio.block_on(self.mcp_manager.attach_mcp(
            self.scope,
            self.project_path.as_deref(),
            &name,
        ));
        if let Err(e) = res {
            self.error = Some(e.to_string().into());
        }
        self.load_data();
        cx.notify();
    }

    fn detach(&mut self, name: SharedString, cx: &mut Context<Self>) {
        if self.scope == McpScope::Local && self.project_path.is_none() {
            self.error = Some("Project path is required for local MCPs".into());
            cx.notify();
            return;
        }
        let res = self.tokio.block_on(self.mcp_manager.detach_mcp(
            self.scope,
            self.project_path.as_deref(),
            &name,
        ));
        if let Err(e) = res {
            self.error = Some(e.to_string().into());
        }
        self.load_data();
        cx.notify();
    }

    fn render_mcp_column(
        &self,
        cx: &mut Context<Self>,
        title: &'static str,
        items: Vec<McpItem>,
        attached: bool,
    ) -> impl IntoElement {
        let border = cx.theme().border.alpha(0.35);
        let row_border = cx.theme().border.alpha(0.25);
        let mut col = div()
            .flex_1()
            .border_1()
            .border_color(border)
            .rounded(px(10.0))
            .overflow_hidden()
            .child(
                div()
                    .px(px(12.0))
                    .py(px(10.0))
                    .bg(cx.theme().muted)
                    .text_sm()
                    .font_weight(gpui::FontWeight::SEMIBOLD)
                    .text_color(cx.theme().foreground)
                    .child(title),
            );

        if items.is_empty() {
            return col.child(
                div()
                    .px(px(12.0))
                    .py(px(12.0))
                    .text_sm()
                    .text_color(cx.theme().muted_foreground)
                    .child(if attached {
                        "No MCPs attached"
                    } else {
                        "No MCPs available"
                    }),
            );
        }

        for item in items {
            let name = item.name.clone();
            let transport = item.transport.clone();
            let desc = if item.description.is_empty() {
                "No description".into()
            } else {
                item.description.clone()
            };

            col = col.child(
                div()
                    .px(px(12.0))
                    .py(px(10.0))
                    .border_t_1()
                    .border_color(row_border)
                    .flex()
                    .justify_between()
                    .gap(px(10.0))
                    .child(
                        div()
                            .flex_1()
                            .child(
                                div()
                                    .text_sm()
                                    .text_color(cx.theme().foreground)
                                    .child(name.clone()),
                            )
                            .child(
                                div()
                                    .pt(px(2.0))
                                    .text_xs()
                                    .text_color(cx.theme().muted_foreground)
                                    .child(desc),
                            ),
                    )
                    .child(
                        div()
                            .flex()
                            .items_center()
                            .gap(px(8.0))
                            .child(
                                div()
                                    .px(px(8.0))
                                    .py(px(4.0))
                                    .rounded(px(999.0))
                                    .bg(cx.theme().list_hover)
                                    .text_xs()
                                    .text_color(cx.theme().muted_foreground)
                                    .child(transport),
                            )
                            .child(
                                div()
                                    .px(px(10.0))
                                    .py(px(6.0))
                                    .rounded(px(8.0))
                                    .cursor_pointer()
                                    .bg(if attached {
                                        cx.theme().danger.opacity(0.063)
                                    } else {
                                        cx.theme().success.opacity(0.063)
                                    })
                                    .hover(|s| {
                                        s.bg(if attached {
                                            cx.theme().danger.opacity(0.094)
                                        } else {
                                            cx.theme().success.opacity(0.094)
                                        })
                                    })
                                    .text_sm()
                                    .text_color(cx.theme().foreground)
                                    .child(if attached { "Detach" } else { "Attach" })
                                    .id(format!(
                                        "mcp-{}-{}",
                                        if attached { "detach" } else { "attach" },
                                        name
                                    ))
                                    .on_click(cx.listener(move |this, _: &ClickEvent, _w, cx| {
                                        if attached {
                                            this.detach(name.clone(), cx);
                                        } else {
                                            this.attach(name.clone(), cx);
                                        }
                                    })),
                            ),
                    ),
            );
        }

        col
    }
}

impl Render for McpManagerDialog {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let attached_set: std::collections::HashSet<&str> =
            self.attached.iter().map(|s| s.as_ref()).collect();

        let mut attached: Vec<McpItem> = self
            .attached
            .iter()
            .map(|name| {
                self.available
                    .iter()
                    .find(|m| m.name == *name)
                    .cloned()
                    .unwrap_or(McpItem {
                        name: name.clone(),
                        description: "Not in config.toml".into(),
                        transport: "STDIO".into(),
                        is_orphan: true,
                    })
            })
            .collect();
        attached.sort_by(|a, b| a.name.cmp(&b.name));

        let mut available: Vec<McpItem> = self
            .available
            .iter()
            .filter(|m| !attached_set.contains(m.name.as_ref()))
            .cloned()
            .collect();
        available.sort_by(|a, b| a.name.cmp(&b.name));

        let selected_index = if self.scope == McpScope::Global { 0 } else { 1 };

        v_flex()
            .gap(px(10.0))
            .child(
                div()
                    .text_sm()
                    .text_color(cx.theme().muted_foreground)
                    .child(self.session_title.clone()),
            )
            .child(
                div().flex().gap(px(8.0)).items_center().child(
                    TabBar::new("mcp-scope-tabs")
                        .pill()
                        .with_size(ComponentSize::Small)
                        .child(Tab::new().label("Shared"))
                        .child(Tab::new().label("Project").disabled(!self.has_project))
                        .selected_index(selected_index)
                        .on_click(cx.listener(|this, ix: &usize, _w, cx| {
                            let scope = if *ix == 0 {
                                McpScope::Global
                            } else {
                                McpScope::Local
                            };
                            this.set_scope(scope, cx);
                        })),
                ),
            )
            .child(
                div()
                    .flex()
                    .gap(px(12.0))
                    .child(self.render_mcp_column(cx, "Attached", attached, true))
                    .child(self.render_mcp_column(cx, "Available", available, false)),
            )
            .when_some(self.error.as_ref(), |el, err| {
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

fn resolve_transport(def: &agentterm_mcp::MCPDef) -> String {
    if !def.transport.is_empty() {
        return def.transport.to_uppercase();
    }
    if !def.url.is_empty() {
        return "HTTP".to_string();
    }
    "STDIO".to_string()
}
