//! MCP Manager dialog for attaching/detaching MCP servers.

use std::collections::HashMap;
use std::sync::Arc;

use agentterm_mcp::{McpManager, McpScope, PoolStatusResponse, ServerStatus, get_pool_status};
use gpui::{
    ClickEvent, Context, IntoElement, ParentElement, Render, SharedString, Styled, Window, div,
    prelude::*, px,
};
use gpui_component::Size as ComponentSize;

use crate::ui::{ActiveTheme, Button, ButtonVariants, Sizable, Tab, TabBar, v_flex};

#[derive(Clone)]
pub struct McpItem {
    pub name: SharedString,
    pub description: SharedString,
    pub transport: SharedString,
    pub is_orphan: bool,
    // Pool status fields
    pub pool_status: Option<ServerStatus>,
    pub pool_uptime_seconds: Option<u64>,
    pub pool_connection_count: u32,
    pub pool_owned: bool,
}

/// McpManagerDialog - A dialog for managing MCP (Model Context Protocol) servers.
/// Allows attaching/detaching MCPs at Global or Workspace scope.
pub struct McpManagerDialog {
    tokio: Arc<tokio::runtime::Runtime>,
    mcp_manager: McpManager,
    scope: McpScope,
    attached: Vec<SharedString>,
    available: Vec<McpItem>,
    error: Option<SharedString>,
    session_title: String,
    workspace_path: Option<String>,
    has_workspace: bool,
    pool_status: PoolStatusResponse,
}

impl McpManagerDialog {
    pub fn new(
        tokio: Arc<tokio::runtime::Runtime>,
        mcp_manager: McpManager,
        session_title: String,
        workspace_path: Option<String>,
    ) -> Self {
        let has_workspace = workspace_path.is_some();
        Self {
            tokio,
            mcp_manager,
            scope: McpScope::Global,
            attached: Vec::new(),
            available: Vec::new(),
            error: None,
            session_title,
            workspace_path,
            has_workspace,
            pool_status: PoolStatusResponse {
                enabled: false,
                server_count: 0,
                servers: vec![],
            },
        }
    }

    pub fn load_data(&mut self) {
        if self.scope == McpScope::Workspace && self.workspace_path.is_none() {
            self.error = Some("Workspace path is required for workspace MCPs".into());
            self.attached.clear();
            self.available.clear();
            return;
        }

        let attached = self
            .tokio
            .block_on(
                self.mcp_manager
                    .get_attached_mcps(self.scope, self.workspace_path.as_deref()),
            )
            .unwrap_or_default();

        let available = self
            .tokio
            .block_on(self.mcp_manager.get_available_mcps())
            .unwrap_or_default();

        // Get pool status and build lookup map
        self.pool_status = get_pool_status();
        let pool_map: HashMap<String, &agentterm_mcp::McpServerStatus> = self
            .pool_status
            .servers
            .iter()
            .map(|s| (s.name.clone(), s))
            .collect();

        let mut items: Vec<McpItem> = available
            .iter()
            .map(|(name, def)| {
                let pool_info = pool_map.get(name.as_str());
                McpItem {
                    name: name.clone().into(),
                    description: def.description.clone().into(),
                    transport: resolve_transport(def).into(),
                    is_orphan: false,
                    pool_status: pool_info.map(|p| p.status),
                    pool_uptime_seconds: pool_info.and_then(|p| p.uptime_seconds),
                    pool_connection_count: pool_info.map(|p| p.connection_count).unwrap_or(0),
                    pool_owned: pool_info.map(|p| p.owned).unwrap_or(false),
                }
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
        if self.scope == McpScope::Workspace && self.workspace_path.is_none() {
            self.error = Some("Workspace path is required for workspace MCPs".into());
            cx.notify();
            return;
        }
        let res = self.tokio.block_on(self.mcp_manager.attach_mcp(
            self.scope,
            self.workspace_path.as_deref(),
            &name,
        ));
        if let Err(e) = res {
            self.error = Some(e.to_string().into());
        }
        self.load_data();
        cx.notify();
    }

    fn detach(&mut self, name: SharedString, cx: &mut Context<Self>) {
        if self.scope == McpScope::Workspace && self.workspace_path.is_none() {
            self.error = Some("Workspace path is required for workspace MCPs".into());
            cx.notify();
            return;
        }
        let res = self.tokio.block_on(self.mcp_manager.detach_mcp(
            self.scope,
            self.workspace_path.as_deref(),
            &name,
        ));
        if let Err(e) = res {
            self.error = Some(e.to_string().into());
        }
        self.load_data();
        cx.notify();
    }

    fn restart_mcp(&mut self, name: SharedString, cx: &mut Context<Self>) {
        let res = self
            .tokio
            .block_on(agentterm_mcp::restart_pool_server(&name));
        match res {
            Ok(true) => {
                self.error = None;
            }
            Ok(false) => {
                self.error = Some(format!("Server '{}' not found in pool", name).into());
            }
            Err(e) => {
                self.error = Some(format!("Failed to restart: {}", e).into());
            }
        }
        self.load_data();
        cx.notify();
    }

    fn stop_mcp(&mut self, name: SharedString, cx: &mut Context<Self>) {
        let res = agentterm_mcp::stop_pool_server(&name);
        match res {
            Ok(true) => {
                self.error = None;
            }
            Ok(false) => {
                self.error = Some(format!("Server '{}' not found in pool", name).into());
            }
            Err(e) => {
                self.error = Some(format!("Failed to stop: {}", e).into());
            }
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
        let pool_enabled = self.pool_status.enabled;

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

            // Pool status info for attached items
            let show_pool_status = attached && pool_enabled && item.pool_status.is_some();
            let pool_status = item.pool_status;
            let pool_owned = item.pool_owned;
            let pool_connections = item.pool_connection_count;
            let pool_uptime = item.pool_uptime_seconds;

            col = col.child(
                div()
                    .px(px(12.0))
                    .py(px(10.0))
                    .border_t_1()
                    .border_color(row_border)
                    .flex()
                    .flex_col()
                    .gap(px(6.0))
                    // Main row: name, description, transport badge, attach/detach button
                    .child(
                        div()
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
                                            .on_click(cx.listener(
                                                move |this, _: &ClickEvent, _w, cx| {
                                                    if attached {
                                                        this.detach(name.clone(), cx);
                                                    } else {
                                                        this.attach(name.clone(), cx);
                                                    }
                                                },
                                            )),
                                    ),
                            ),
                    )
                    // Pool status row (only for attached, pooled MCPs)
                    .when(show_pool_status, |el| {
                        let status = pool_status.unwrap();
                        let status_color = match status {
                            ServerStatus::Running => cx.theme().success,
                            ServerStatus::Starting => cx.theme().warning,
                            ServerStatus::Failed => cx.theme().danger,
                            ServerStatus::Stopped => cx.theme().muted_foreground,
                        };
                        let status_label = match status {
                            ServerStatus::Running => "Running",
                            ServerStatus::Starting => "Starting",
                            ServerStatus::Failed => "Failed",
                            ServerStatus::Stopped => "Stopped",
                        };
                        let name_restart = item.name.clone();
                        let name_stop = item.name.clone();

                        el.child(
                            div()
                                .flex()
                                .items_center()
                                .justify_between()
                                .pt(px(4.0))
                                .child(
                                    div()
                                        .flex()
                                        .items_center()
                                        .gap(px(8.0))
                                        // Status dot
                                        .child(
                                            div()
                                                .w(px(6.0))
                                                .h(px(6.0))
                                                .rounded(px(999.0))
                                                .bg(status_color),
                                        )
                                        // Status label
                                        .child(
                                            div()
                                                .text_xs()
                                                .text_color(status_color)
                                                .child(status_label),
                                        )
                                        // Connection count (only for running)
                                        .when(
                                            status == ServerStatus::Running && pool_connections > 0,
                                            |el| {
                                                el.child(
                                                    div()
                                                        .text_xs()
                                                        .text_color(cx.theme().muted_foreground)
                                                        .child(format!("{}↔", pool_connections)),
                                                )
                                            },
                                        )
                                        // Uptime (only for running)
                                        .when(status == ServerStatus::Running, |el| {
                                            if let Some(secs) = pool_uptime {
                                                el.child(
                                                    div()
                                                        .text_xs()
                                                        .text_color(cx.theme().muted_foreground)
                                                        .child(format_uptime(secs)),
                                                )
                                            } else {
                                                el
                                            }
                                        }),
                                )
                                // Action buttons (only for owned servers)
                                .when(pool_owned, |el| {
                                    el.child(
                                        div()
                                            .flex()
                                            .items_center()
                                            .gap(px(6.0))
                                            // Restart button (for running or failed)
                                            .when(
                                                status == ServerStatus::Running
                                                    || status == ServerStatus::Failed,
                                                |el| {
                                                    el.child(
                                                        Button::new(format!(
                                                            "restart-{}",
                                                            item.name
                                                        ))
                                                        .label("Restart")
                                                        .ghost()
                                                        .xsmall()
                                                        .on_click(cx.listener(
                                                            move |this, _, _, cx| {
                                                                this.restart_mcp(
                                                                    name_restart.clone(),
                                                                    cx,
                                                                );
                                                            },
                                                        )),
                                                    )
                                                },
                                            )
                                            // Stop button (only for running)
                                            .when(status == ServerStatus::Running, |el| {
                                                el.child(
                                                    Button::new(format!("stop-{}", item.name))
                                                        .label("Stop")
                                                        .ghost()
                                                        .xsmall()
                                                        .on_click(cx.listener(
                                                            move |this, _, _, cx| {
                                                                this.stop_mcp(
                                                                    name_stop.clone(),
                                                                    cx,
                                                                );
                                                            },
                                                        )),
                                                )
                                            }),
                                    )
                                }),
                        )
                    }),
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
                        pool_status: None,
                        pool_uptime_seconds: None,
                        pool_connection_count: 0,
                        pool_owned: false,
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
                        .child(Tab::new().label("Workspace").disabled(!self.has_workspace))
                        .selected_index(selected_index)
                        .on_click(cx.listener(|this, ix: &usize, _w, cx| {
                            let scope = if *ix == 0 {
                                McpScope::Global
                            } else {
                                McpScope::Workspace
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

fn format_uptime(seconds: u64) -> String {
    if seconds < 60 {
        return format!("{}s", seconds);
    }
    if seconds < 3600 {
        return format!("{}m", seconds / 60);
    }
    if seconds < 86400 {
        let h = seconds / 3600;
        let m = (seconds % 3600) / 60;
        return if m > 0 {
            format!("{}h {}m", h, m)
        } else {
            format!("{}h", h)
        };
    }
    let d = seconds / 86400;
    let h = (seconds % 86400) / 3600;
    if h > 0 {
        format!("{}d {}h", d, h)
    } else {
        format!("{}d", d)
    }
}
