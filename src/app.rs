use std::collections::HashMap;
use std::env;
use std::path::PathBuf;

use agentterm_mcp::{McpManager, McpScope};
use agentterm_session::{
    DEFAULT_SECTION_ID, NewSessionInput, SectionRecord, SessionRecord, SessionStore, SessionTool,
};
use agentterm_tools::{ShellInfo, ShellType, ToolInfo};
use crate::icons::{Icon, IconDescriptor, IconName, IconSize, icon_from_string};
use crate::ui::IconPicker;
use crate::settings::AppSettings;
use crate::settings_dialog::SettingsDialog;
use crate::ui::{
    ActiveTheme, Button, ButtonVariants, ContextMenuExt, Divider, Sizable, Tab, TabBar, WindowExt,
    v_flex,
};
use gpui_component::{
    Size as ComponentSize,
    input::{Input as GpuiInput, InputState as GpuiInputState},
    theme::{Theme as GpuiTheme, ThemeMode as GpuiThemeMode},
};
use gpui::{
    App, Application, AsyncApp, Bounds, BoxShadow, ClickEvent, Context, Entity, FocusHandle,
    Focusable, InteractiveElement, IntoElement, KeyBinding, Menu, MenuItem, MouseButton,
    MouseDownEvent, MouseMoveEvent, MouseUpEvent, ParentElement, Pixels, Render, SharedString,
    StatefulInteractiveElement, Styled, WeakEntity, Window, WindowBackgroundAppearance,
    WindowBounds, WindowOptions, actions, div, hsla, point, prelude::*, px, rgba, size,
};
use gpui_term::{Clear, Copy, Paste, SelectAll, Terminal, TerminalBuilder, TerminalView};

actions!(
    agentterm_gpui,
    [
        Quit,
        ToggleSidebar,
        ToggleMcpManager,
        NewShellTab,
        OpenSettings,
        // Window actions (cross-platform)
        Minimize,
        Zoom,
        // macOS-only actions (defined here, but only registered on macOS)
        About,
        Hide,
        HideOthers,
        ShowAll,
    ]
);

// Actions with data for context menu items
#[derive(Clone, PartialEq, serde::Deserialize, schemars::JsonSchema, gpui::Action)]
pub struct RenameSession(pub String);

#[derive(Clone, PartialEq, serde::Deserialize, schemars::JsonSchema, gpui::Action)]
pub struct CloseSessionAction(pub String);

#[derive(Clone, PartialEq, serde::Deserialize, schemars::JsonSchema, gpui::Action)]
pub struct RestartSessionAction(pub String);

#[derive(Clone, PartialEq, serde::Deserialize, schemars::JsonSchema, gpui::Action)]
pub struct EditSection(pub String);

#[derive(Clone, PartialEq, serde::Deserialize, schemars::JsonSchema, gpui::Action)]
pub struct RemoveSection(pub String);

const SIDEBAR_INSET: f32 = 8.0;
const SIDEBAR_GAP: f32 = 16.0;
const SIDEBAR_MIN_WIDTH: f32 = 200.0;
const SIDEBAR_MAX_WIDTH: f32 = 420.0;
const SIDEBAR_HEADER_LEFT_PADDING: f32 = 68.0;

const SIDEBAR_GLASS_BASE_ALPHA: f32 = 0.18;
const SIDEBAR_GLASS_BORDER_ALPHA: f32 = 0.14;

const SURFACE_ROOT: u32 = 0x000000;
const SURFACE_SIDEBAR: u32 = 0x202020;
const BORDER_SOFT: u32 = 0x3a3a3a;

const SURFACE_ROOT_ALPHA: f32 = 0.05;
const SURFACE_SIDEBAR_ALPHA: f32 = 0.32;
const BORDER_SOFT_ALPHA: f32 = 0.50;

const ENABLE_BLUR: bool = true;

/// Create the application menu bar.
/// Uses conditional compilation for platform-specific items.
fn app_menus() -> Vec<Menu> {
    vec![
        Menu {
            name: "Agent Term".into(),
            items: vec![
                MenuItem::action("About Agent Term", About),
                MenuItem::separator(),
                MenuItem::action("Settings...", OpenSettings),
                MenuItem::separator(),
                #[cfg(target_os = "macos")]
                MenuItem::action("Hide Agent Term", Hide),
                #[cfg(target_os = "macos")]
                MenuItem::action("Hide Others", HideOthers),
                #[cfg(target_os = "macos")]
                MenuItem::action("Show All", ShowAll),
                #[cfg(target_os = "macos")]
                MenuItem::separator(),
                MenuItem::action("Quit Agent Term", Quit),
            ],
        },
        Menu {
            name: "Edit".into(),
            items: vec![
                MenuItem::action("Copy", Copy),
                MenuItem::action("Paste", Paste),
                MenuItem::action("Select All", SelectAll),
            ],
        },
        Menu {
            name: "View".into(),
            items: vec![
                MenuItem::action("Toggle Sidebar", ToggleSidebar),
                MenuItem::action("MCP Manager", ToggleMcpManager),
            ],
        },
        Menu {
            name: "Terminal".into(),
            items: vec![
                MenuItem::action("New Tab", NewShellTab),
                MenuItem::action("Clear", Clear),
            ],
        },
        Menu {
            name: "Window".into(),
            items: vec![
                MenuItem::action("Minimize", Minimize),
                MenuItem::action("Zoom", Zoom),
            ],
        },
    ]
}

fn rgba_u32(rgb: u32, alpha: f32) -> u32 {
    let a = (alpha.clamp(0.0, 1.0) * 255.0).round() as u32;
    (rgb << 8) | a
}

#[cfg(target_os = "macos")]
fn configure_macos_titlebar(window: &mut Window) {
    use raw_window_handle::{HasWindowHandle, RawWindowHandle};
    use objc::{msg_send, sel, sel_impl};

    let Ok(handle) = window.window_handle() else {
        return;
    };

    let RawWindowHandle::AppKit(handle) = handle.as_raw() else {
        return;
    };

    // raw-window-handle gives us an NSView*. From that, ask for the NSWindow and
    // remove the titlebar separator line (otherwise it shows up behind our floating sidebar).
    unsafe {
        let ns_view = handle.ns_view.as_ptr() as *mut objc::runtime::Object;
        if ns_view.is_null() {
            return;
        }
        let ns_window: *mut objc::runtime::Object = msg_send![ns_view, window];
        if ns_window.is_null() {
            return;
        }

        let responds: bool = msg_send![
            ns_window,
            respondsToSelector: sel!(setTitlebarSeparatorStyle:)
        ];
        if responds {
            // NSTitlebarSeparatorStyleNone = 1
            let _: () = msg_send![ns_window, setTitlebarSeparatorStyle: 1isize];
        }
    }
}

pub fn run() {
    let app = Application::new().with_assets(crate::assets::Assets);

    // Handle dock icon click when app has no visible windows (macOS)
    // Also handles similar scenarios on other platforms
    app.on_reopen(|cx| {
        // Find existing windows and activate them
        if let Some(window) = cx.windows().first() {
            let _ = cx.update_window(*window, |_root, window, _cx| {
                window.activate_window();
            });
        }
    });

    app.run(|cx: &mut App| {
        // Initialize gpui-component (theme, input bindings, dialogs, menus, etc.)
        gpui_component::init(cx);
        {
            let theme = GpuiTheme::global_mut(cx);
            theme.mode = GpuiThemeMode::Dark;
            // Fully transparent Root background so blur/vibrancy shows through and
            // translucent surfaces keep clean rounded corners.
            theme.colors.background = gpui::transparent_black();
        }

        // Set up key bindings
        cx.bind_keys([
            KeyBinding::new("cmd-q", Quit, None),
            KeyBinding::new("cmd-b", ToggleSidebar, None),
            KeyBinding::new("cmd-m", ToggleMcpManager, None),
            KeyBinding::new("cmd-t", NewShellTab, None),
            KeyBinding::new("cmd-,", OpenSettings, None),
            KeyBinding::new("cmd-c", Copy, Some("Terminal")),
            KeyBinding::new("cmd-v", Paste, Some("Terminal")),
            KeyBinding::new("cmd-a", SelectAll, Some("Terminal")),
            KeyBinding::new("cmd-k", Clear, Some("Terminal")),
        ]);
        crate::text_input::bind_keys(cx);

        // Set up application menu bar
        cx.set_menus(app_menus());

        // Register action handlers
        cx.on_action(|_: &Quit, cx| cx.quit());

        // macOS-specific action handlers
        #[cfg(target_os = "macos")]
        {
            cx.on_action(|_: &Hide, cx| cx.hide());
            cx.on_action(|_: &HideOthers, cx| cx.hide_other_apps());
            cx.on_action(|_: &ShowAll, cx| cx.unhide_other_apps());
        }

        // About action (TODO: show about dialog)
        cx.on_action(|_: &About, _cx| {
            // For now, just a no-op. Could show an about dialog later.
        });

        let background_appearance = if ENABLE_BLUR {
            WindowBackgroundAppearance::Blurred
        } else {
            WindowBackgroundAppearance::Opaque
        };

        let window_options = WindowOptions {
            titlebar: Some(gpui::TitlebarOptions {
                title: Some("Agent Term".into()),
                appears_transparent: true,
                traffic_light_position: Some(gpui::point(px(16.0), px(16.0))),
                ..Default::default()
            }),
            window_background: background_appearance,
            ..Default::default()
        };

        cx.open_window(window_options, |window, cx| {
            window.set_background_appearance(background_appearance);
            #[cfg(target_os = "macos")]
            configure_macos_titlebar(window);
            let app = cx.new(|cx| AgentTermApp::new(window, cx));
            cx.new(|cx| gpui_component::Root::new(app, window, cx))
        })
        .unwrap();

        // Activate the app (bring to front)
        cx.activate(true);
    });
}

use std::sync::Arc;

#[derive(Clone)]
struct SectionItem {
    section: SectionRecord,
    is_default: bool,
}

/// TabPickerDialog - A dialog for selecting shells and tools to create new tabs.
/// This is rendered as an Entity inside the gpui-component Dialog system.
struct TabPickerDialog {
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
    fn new(
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

    fn load_data(&mut self) {
        self.loading = true;
        self.error = None;

        let tools = match self.tokio.block_on(agentterm_tools::tools_list(&self.mcp_manager)) {
            Ok(list) => list.into_iter().filter(|t| t.enabled).collect(),
            Err(e) => {
                self.error = Some(e.to_string().into());
                Vec::new()
            }
        };

        let pinned = match self.tokio.block_on(agentterm_tools::get_pinned_shells(&self.mcp_manager)) {
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
                    .on_mouse_down(MouseButton::Left, cx.listener({
                        let shell = shell.clone();
                        move |this, _: &MouseDownEvent, window, cx| {
                            this.select_shell(shell.clone(), window, cx);
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

#[derive(Clone)]
struct McpItem {
    name: SharedString,
    description: SharedString,
    transport: SharedString,
    is_orphan: bool,
}

/// McpManagerDialog - A dialog for managing MCP (Model Context Protocol) servers.
/// Allows attaching/detaching MCPs at Global or Local (project) scope.
struct McpManagerDialog {
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
    fn new(
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

    fn load_data(&mut self) {
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
        let mut col = div()
            .flex_1()
            .border_1()
            .border_color(rgba(rgba_u32(BORDER_SOFT, 0.35)))
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
                    .border_color(rgba(rgba_u32(BORDER_SOFT, 0.25)))
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
                div()
                    .flex()
                    .gap(px(8.0))
                    .items_center()
                    .child(
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

/// ProjectEditorDialog - A dialog for editing project name, path, and icon.
struct ProjectEditorDialog {
    view: Entity<AgentTermApp>,
    section_id: String,
    name_input: Entity<GpuiInputState>,
    path_input: Entity<GpuiInputState>,
    current_icon: Option<String>,
}

impl ProjectEditorDialog {
    fn new(
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

    fn set_icon(&mut self, icon: Option<IconDescriptor>, cx: &mut Context<Self>) {
        self.current_icon = icon.map(|d| icon_descriptor_to_string(&d));
        cx.notify();
    }

    fn save(&self, window: &mut Window, cx: &mut Context<Self>) {
        let name = self.name_input.read(cx).value().to_string().trim().to_string();
        let path = self.path_input.read(cx).value().to_string().trim().to_string();

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
                            })
                    )
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

/// Convert IconDescriptor to a string format for storage
fn icon_descriptor_to_string(icon: &IconDescriptor) -> String {
    match icon {
        IconDescriptor::Lucide { id } => format!("lucide:{}", id),
        IconDescriptor::Tool { id } => id.clone(),
    }
}

/// Convert a string to IconDescriptor
fn icon_descriptor_from_string(s: &str) -> IconDescriptor {
    if s.starts_with("lucide:") {
        IconDescriptor::lucide(&s[7..])
    } else {
        IconDescriptor::tool(s)
    }
}

/// Dialog for editing session properties: icon, name, command
struct SessionEditorDialog {
    view: Entity<AgentTermApp>,
    session_id: String,
    name_input: Entity<GpuiInputState>,
    command_input: Entity<GpuiInputState>,
    current_icon: Option<String>,
}

impl SessionEditorDialog {
    fn new(
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

    fn set_icon(&mut self, icon: Option<IconDescriptor>, cx: &mut Context<Self>) {
        self.current_icon = icon.map(|d| icon_descriptor_to_string(&d));
        cx.notify();
    }

    fn save(&self, window: &mut Window, cx: &mut Context<Self>) {
        let name = self.name_input.read(cx).value().to_string().trim().to_string();
        let command = self.command_input.read(cx).value().to_string().trim().to_string();

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
                            .value(current_icon.as_ref().map(|s| icon_descriptor_from_string(s)))
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

struct AgentTermApp {
    focus_handle: FocusHandle,

    session_store: SessionStore,
    mcp_manager: McpManager,
    tokio: Arc<tokio::runtime::Runtime>,

    sidebar_visible: bool,
    sidebar_width: f32,
    resizing_sidebar: bool,
    resize_start_x: Pixels,
    resize_start_width: f32,

    sections: Vec<SectionItem>,
    sessions: Vec<SessionRecord>,
    active_session_id: Option<String>,

    terminals: HashMap<String, Entity<Terminal>>,
    terminal_views: HashMap<String, Entity<TerminalView>>,

    session_menu_open: bool,
    session_menu_session_id: Option<String>,

    settings: AppSettings,
}

impl AgentTermApp {
    fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let focus_handle = cx.focus_handle();
        focus_handle.focus(window, cx);

        let tokio = Arc::new(
            tokio::runtime::Builder::new_multi_thread()
                .enable_all()
                .build()
                .expect("tokio runtime"),
        );

        let session_store = SessionStore::open_default_profile().expect("session store");
        let mcp_manager = tokio
            .block_on(agentterm_mcp::build_mcp_manager())
            .expect("mcp manager");

        let mut this = Self {
            focus_handle,
            session_store,
            mcp_manager,
            tokio,
            sidebar_visible: true,
            sidebar_width: 250.0,
            resizing_sidebar: false,
            resize_start_x: Pixels::ZERO,
            resize_start_width: 250.0,
            sections: Vec::new(),
            sessions: Vec::new(),
            active_session_id: None,
            terminals: HashMap::new(),
            terminal_views: HashMap::new(),
            session_menu_open: false,
            session_menu_session_id: None,
            settings: AppSettings::load(),
        };

        this.reload_from_store(cx);
        this.ensure_active_terminal(window, cx);
        this
    }

    fn reload_from_store(&mut self, cx: &mut Context<Self>) {
        let mut sections: Vec<SectionItem> = self
            .session_store
            .list_sections()
            .into_iter()
            .map(|section| SectionItem {
                section,
                is_default: false,
            })
            .collect();

        sections.sort_by_key(|s| s.section.order);
        sections.insert(
            0,
            SectionItem {
                section: SectionRecord {
                    id: DEFAULT_SECTION_ID.to_string(),
                    name: "Default".to_string(),
                    path: String::new(),
                    icon: None,
                    collapsed: false,
                    order: 0,
                },
                is_default: true,
            },
        );

        let sessions = self.session_store.list_sessions();
        let active_session_id = self
            .session_store
            .active_session_id()
            .or_else(|| sessions.first().map(|s| s.id.clone()));

        self.sections = sections;
        self.sessions = sessions;
        self.active_session_id = active_session_id;
        cx.notify();
    }

    fn toggle_sidebar(&mut self, _: &ToggleSidebar, _window: &mut Window, cx: &mut Context<Self>) {
        self.sidebar_visible = !self.sidebar_visible;
        cx.notify();
    }

    fn minimize_window(&mut self, _: &Minimize, window: &mut Window, _cx: &mut Context<Self>) {
        window.minimize_window();
    }

    fn zoom_window(&mut self, _: &Zoom, window: &mut Window, _cx: &mut Context<Self>) {
        window.zoom_window();
    }

    fn open_settings(&mut self, _: &OpenSettings, _window: &mut Window, cx: &mut Context<Self>) {
        let settings = self.settings.clone();

        // Compute bounds before opening window to avoid borrow conflict
        let window_bounds = WindowBounds::Windowed(Bounds::centered(
            None,
            size(px(600.0), px(700.0)),
            cx,
        ));

        let _ = cx.open_window(
            WindowOptions {
                titlebar: Some(gpui::TitlebarOptions {
                    title: Some("Settings".into()),
                    appears_transparent: false,
                    ..Default::default()
                }),
                window_bounds: Some(window_bounds),
                kind: gpui::WindowKind::Normal,
                is_resizable: true,
                is_movable: true,
                focus: true,
                show: true,
                ..Default::default()
            },
            |settings_window, cx| cx.new(|cx| SettingsDialog::new(settings, settings_window, cx)),
        );
    }

    fn open_project_editor(
        &mut self,
        section_id: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(section) = self
            .sections
            .iter()
            .find(|s| s.section.id == section_id)
            .map(|s| s.section.clone())
        else {
            return;
        };

        let view = cx.entity().clone();

        // Create inputs
        let name_input = cx.new(|cx| {
            GpuiInputState::new(window, cx)
                .placeholder("Project name")
                .default_value(section.name.clone())
        });
        let path_input = cx.new(|cx| {
            GpuiInputState::new(window, cx)
                .placeholder("Project path")
                .default_value(section.path.clone())
        });

        let name_focus = name_input.read(cx).focus_handle(cx);

        // Create the dialog entity
        let dialog_entity = cx.new(|_cx| {
            ProjectEditorDialog::new(
                view.clone(),
                section_id.clone(),
                name_input.clone(),
                path_input.clone(),
                section.icon.clone(),
            )
        });

        window.open_dialog(cx, move |dialog, _window, _cx| {
            dialog
                .title("Edit Project")
                .w(px(400.))
                .child(dialog_entity.clone())
                .footer({
                    let dialog_entity = dialog_entity.clone();
                    move |_ok, cancel, window, cx| {
                        vec![
                            cancel(window, cx),
                            Button::new("save")
                                .primary()
                                .label("Save")
                                .on_click({
                                    let dialog_entity = dialog_entity.clone();
                                    move |_, window, cx| {
                                        dialog_entity.update(cx, |dialog, cx| {
                                            dialog.save(window, cx);
                                        });
                                    }
                                })
                                .into_any_element(),
                        ]
                    }
                })
        });

        name_focus.focus(window, cx);
    }

    fn toggle_section_collapsed(&mut self, section_id: String, cx: &mut Context<Self>) {
        if section_id == DEFAULT_SECTION_ID {
            return;
        }
        let Some(section) = self
            .sections
            .iter()
            .find(|s| s.section.id == section_id)
            .map(|s| s.section.clone())
        else {
            return;
        };
        let next = !section.collapsed;
        let _ = self
            .session_store
            .set_section_collapsed(&section_id, next);
        self.reload_from_store(cx);
    }

    fn move_section(&mut self, section_id: String, delta: isize, cx: &mut Context<Self>) {
        let mut ordered: Vec<SectionRecord> = self
            .sections
            .iter()
            .filter(|s| s.section.id != DEFAULT_SECTION_ID)
            .map(|s| s.section.clone())
            .collect();
        ordered.sort_by_key(|s| s.order);

        let idx = ordered.iter().position(|s| s.id == section_id);
        let Some(idx) = idx else { return };
        let new_idx = (idx as isize + delta).clamp(0, ordered.len().saturating_sub(1) as isize);
        if new_idx as usize == idx {
            return;
        }
        let item = ordered.remove(idx);
        ordered.insert(new_idx as usize, item);
        let ids: Vec<String> = ordered.into_iter().map(|s| s.id).collect();
        let _ = self.session_store.reorder_sections(&ids);
        self.reload_from_store(cx);
    }

    fn open_session_menu(&mut self, session_id: String, cx: &mut Context<Self>) {
        self.session_menu_open = true;
        self.session_menu_session_id = Some(session_id);
        cx.notify();
    }

    fn close_session_menu(&mut self, cx: &mut Context<Self>) {
        self.session_menu_open = false;
        self.session_menu_session_id = None;
        cx.notify();
    }

    fn open_session_rename(&mut self, session_id: String, window: &mut Window, cx: &mut Context<Self>) {
        let Some(session) = self.sessions.iter().find(|s| s.id == session_id).cloned() else {
            return;
        };
        self.session_menu_open = false;

        let view = cx.entity().clone();

        // Create inputs with current values
        let name_input = cx.new(|cx| {
            GpuiInputState::new(window, cx)
                .placeholder("Tab name")
                .default_value(session.title.clone())
        });
        let command_input = cx.new(|cx| {
            GpuiInputState::new(window, cx)
                .placeholder("Command (e.g., /bin/zsh)")
                .default_value(session.command.clone())
        });
        let name_focus = name_input.read(cx).focus_handle(cx);

        let dialog_entity = cx.new(|_cx| {
            SessionEditorDialog::new(
                view.clone(),
                session_id.clone(),
                name_input.clone(),
                command_input.clone(),
                session.icon.clone(),
            )
        });

        window.open_dialog(cx, move |dialog, _window, _cx| {
            dialog
                .title("Edit Tab")
                .w(px(400.))
                .child(dialog_entity.clone())
                .footer({
                    let dialog_entity = dialog_entity.clone();
                    move |_ok, cancel, window, cx| {
                        vec![
                            cancel(window, cx),
                            Button::new("save")
                                .primary()
                                .label("Save")
                                .on_click({
                                    let dialog_entity = dialog_entity.clone();
                                    move |_, window, cx| {
                                        dialog_entity.update(cx, |dialog, cx| {
                                            dialog.save(window, cx);
                                        });
                                    }
                                })
                                .into_any_element(),
                        ]
                    }
                })
        });

        name_focus.focus(window, cx);
        cx.notify();
    }

    fn move_session_to_section(
        &mut self,
        session_id: String,
        section_id: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let _ = self.session_store.move_session(&session_id, section_id);
        self.close_session_menu(cx);
        self.reload_from_store(cx);
        self.ensure_active_terminal(window, cx);
    }

    fn move_session_order(&mut self, session_id: String, delta: isize, cx: &mut Context<Self>) {
        let Some(session) = self.sessions.iter().find(|s| s.id == session_id).cloned() else {
            return;
        };
        let section_id = session.section_id.clone();

        let mut ordered: Vec<SessionRecord> = self
            .sessions
            .iter()
            .filter(|s| s.section_id == section_id)
            .cloned()
            .collect();
        ordered.sort_by(|a, b| {
            a.tab_order
                .unwrap_or(u32::MAX)
                .cmp(&b.tab_order.unwrap_or(u32::MAX))
                .then_with(|| a.created_at.cmp(&b.created_at))
        });

        let idx = ordered.iter().position(|s| s.id == session_id);
        let Some(idx) = idx else { return };
        let new_idx = (idx as isize + delta).clamp(0, ordered.len().saturating_sub(1) as isize);
        if new_idx as usize == idx {
            return;
        }

        let item = ordered.remove(idx);
        ordered.insert(new_idx as usize, item);
        let ids: Vec<String> = ordered.into_iter().map(|s| s.id).collect();
        let _ = self
            .session_store
            .reorder_sessions_in_section(&section_id, &ids);
        self.reload_from_store(cx);
    }

    fn start_sidebar_resize(
        &mut self,
        event: &MouseDownEvent,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.resizing_sidebar = true;
        self.resize_start_x = event.position.x;
        self.resize_start_width = self.sidebar_width;
        cx.notify();
    }

    fn stop_sidebar_resize(
        &mut self,
        _event: &MouseUpEvent,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.resizing_sidebar {
            self.resizing_sidebar = false;
            cx.notify();
        }
    }

    fn update_sidebar_resize(
        &mut self,
        event: &MouseMoveEvent,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if !self.resizing_sidebar || !event.dragging() {
            return;
        }

        let delta = event.position.x - self.resize_start_x;
        let next_width =
            (self.resize_start_width + delta / px(1.0)).clamp(SIDEBAR_MIN_WIDTH, SIDEBAR_MAX_WIDTH);
        if (next_width - self.sidebar_width).abs() > 0.1 {
            self.sidebar_width = next_width;
            cx.notify();
        }
    }

    fn open_mcp_manager(
        &mut self,
        _: &ToggleMcpManager,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.session_menu_open = false;

        let tokio = self.tokio.clone();
        let mcp_manager = self.mcp_manager.clone();

        let session_title = self
            .active_session()
            .map(|s| s.title.clone())
            .unwrap_or_default();
        let project_path = self.active_session().and_then(|s| {
            if s.project_path.is_empty() {
                None
            } else {
                Some(s.project_path.clone())
            }
        });

        let dialog_entity = cx.new(|_cx| {
            let mut dialog =
                McpManagerDialog::new(tokio, mcp_manager, session_title, project_path);
            dialog.load_data();
            dialog
        });

        window.open_dialog(cx, move |dialog, _window, _cx| {
            dialog
                .title("MCP Manager")
                .w(px(720.))
                .close_button(true)
                .child(dialog_entity.clone())
        });

        cx.notify();
    }

    fn new_shell_tab(&mut self, _: &NewShellTab, window: &mut Window, cx: &mut Context<Self>) {
        self.session_menu_open = false;

        let view = cx.entity().clone();
        let tokio = self.tokio.clone();
        let mcp_manager = self.mcp_manager.clone();

        // Create dialog entity with its own state
        let dialog_entity = cx.new(|_cx| {
            let mut dialog = TabPickerDialog::new(view, tokio, mcp_manager);
            dialog.load_data();
            dialog
        });

        window.open_dialog(cx, move |dialog, _window, _cx| {
            dialog
                .title("Create tab")
                .w(px(280.))
                .max_h(px(540.))
                .close_button(true)
                .child(dialog_entity.clone())
        });

        self.ensure_active_terminal(window, cx);
        cx.notify();
    }

    fn create_session_from_tool(
        &mut self,
        tool: SessionTool,
        title: String,
        command: String,
        args: Vec<String>,
        icon: Option<String>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let (section_id, project_path) = self
            .active_section()
            .map(|s| (s.id.clone(), s.path.clone()))
            .unwrap_or_else(|| (DEFAULT_SECTION_ID.to_string(), String::new()));

        let input = NewSessionInput {
            title,
            project_path,
            section_id,
            tool,
            command,
            args: if args.is_empty() { None } else { Some(args) },
            icon,
        };

        match self.session_store.create_session(input) {
            Ok(record) => {
                let _ = self.session_store.set_active_session(Some(record.id.clone()));
                self.reload_from_store(cx);
                self.ensure_active_terminal(window, cx);
            }
            Err(e) => {
                // Log error - dialog handles its own error display
                eprintln!("Failed to create session: {}", e);
            }
        }
        cx.notify();
    }

    fn active_session(&self) -> Option<&SessionRecord> {
        let id = self.active_session_id.as_deref()?;
        self.sessions.iter().find(|s| s.id == id)
    }

    fn active_section(&self) -> Option<&SectionRecord> {
        let session = self.active_session()?;
        self.sections
            .iter()
            .find(|s| s.section.id == session.section_id)
            .map(|s| &s.section)
    }

    fn set_active_session_id(&mut self, id: String, window: &mut Window, cx: &mut Context<Self>) {
        if self.active_session_id.as_deref() == Some(&id) {
            return;
        }
        let _ = self.session_store.set_active_session(Some(id.clone()));
        self.active_session_id = Some(id);
        self.ensure_active_terminal(window, cx);
        cx.notify();
    }

    fn close_session(&mut self, id: String, window: &mut Window, cx: &mut Context<Self>) {
        if let Some(terminal) = self.terminals.remove(&id) {
            terminal.update(cx, |terminal, _| terminal.shutdown());
        }
        self.terminal_views.remove(&id);

        let _ = self.session_store.delete_session(&id);
        self.reload_from_store(cx);
        if self.active_session_id.is_none() {
            self.ensure_active_terminal(window, cx);
        }
    }

    // Action handlers for context menu items
    fn handle_rename_session(
        &mut self,
        action: &RenameSession,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.open_session_rename(action.0.clone(), window, cx);
    }

    fn handle_close_session(
        &mut self,
        action: &CloseSessionAction,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.close_session(action.0.clone(), window, cx);
    }

    fn handle_restart_session(
        &mut self,
        action: &RestartSessionAction,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.restart_session(action.0.clone(), window, cx);
    }

    fn restart_session(&mut self, id: String, window: &mut Window, cx: &mut Context<Self>) {
        // Shutdown the existing terminal if it exists
        if let Some(terminal) = self.terminals.remove(&id) {
            terminal.update(cx, |terminal, _| terminal.shutdown());
        }
        self.terminal_views.remove(&id);

        // Set this session as active and recreate the terminal
        self.active_session_id = Some(id);
        self.ensure_active_terminal(window, cx);
        cx.notify();
    }

    fn handle_edit_section(
        &mut self,
        action: &EditSection,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.open_project_editor(action.0.clone(), window, cx);
    }

    fn handle_remove_section(
        &mut self,
        action: &RemoveSection,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let section_id = action.0.clone();

        // Prevent removing the default section
        if section_id == DEFAULT_SECTION_ID {
            return;
        }

        // Find the section
        let Some(section) = self
            .sections
            .iter()
            .find(|s| s.section.id == section_id)
            .map(|s| s.section.clone())
        else {
            return;
        };

        // Count sessions in this section
        let session_count = self
            .sessions
            .iter()
            .filter(|s| s.section_id == section_id)
            .count();

        // Get session titles for display (max 5)
        let session_titles: Vec<String> = self
            .sessions
            .iter()
            .filter(|s| s.section_id == section_id)
            .take(5)
            .map(|s| s.title.clone())
            .collect();

        let view = cx.entity().clone();
        let section_id_for_delete = section_id.clone();
        let section_name = section.name.clone();

        window.open_dialog(cx, move |dialog, _window, cx| {
            let mut content = v_flex().gap(px(12.));

            // Confirmation message
            content = content.child(
                div()
                    .text_sm()
                    .child(format!("Are you sure you want to remove \"{}\"?", section_name)),
            );

            // Session info warning
            if session_count > 0 {
                let tabs_text = if session_count == 1 {
                    "1 tab".to_string()
                } else {
                    format!("{} tabs", session_count)
                };

                content = content.child(
                    div()
                        .mt(px(8.))
                        .p(px(12.))
                        .rounded(px(6.))
                        .bg(cx.theme().warning.opacity(0.1))
                        .border_1()
                        .border_color(cx.theme().warning.opacity(0.3))
                        .child(
                            v_flex()
                                .gap(px(4.))
                                .child(
                                    div()
                                        .text_sm()
                                        .font_weight(gpui::FontWeight::MEDIUM)
                                        .text_color(cx.theme().warning)
                                        .child(format!("This project has {}", tabs_text)),
                                )
                                .child(
                                    div()
                                        .text_sm()
                                        .text_color(cx.theme().muted_foreground)
                                        .child("These tabs will be moved to the Default section."),
                                ),
                        ),
                );

                // List session titles (max 5)
                if !session_titles.is_empty() {
                    let mut list = v_flex().gap(px(2.)).mt(px(8.));
                    for title in &session_titles {
                        list = list.child(
                            div()
                                .text_sm()
                                .text_color(cx.theme().muted_foreground)
                                .child(format!("• {}", title)),
                        );
                    }
                    if session_count > 5 {
                        list = list.child(
                            div()
                                .text_sm()
                                .text_color(cx.theme().muted_foreground)
                                .child(format!("...and {} more", session_count - 5)),
                        );
                    }
                    content = content.child(list);
                }
            }

            dialog
                .title("Remove Project")
                .w(px(400.))
                .child(content)
                .footer({
                    let view = view.clone();
                    let section_id = section_id_for_delete.clone();
                    move |_ok, cancel, window, cx| {
                        vec![
                            cancel(window, cx),
                            Button::new("remove")
                                .danger()
                                .label("Remove")
                                .on_click({
                                    let view = view.clone();
                                    let section_id = section_id.clone();
                                    move |_, window, cx| {
                                        window.close_dialog(cx);
                                        view.update(cx, |app, cx| {
                                            let _ = app.session_store.delete_section(&section_id);
                                            app.reload_from_store(cx);
                                            app.ensure_active_terminal(window, cx);
                                        });
                                    }
                                })
                                .into_any_element(),
                        ]
                    }
                })
        });
    }

    fn ensure_active_terminal(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let Some(session) = self.active_session().cloned() else {
            return;
        };

        if let Some(view) = self.terminal_views.get(&session.id) {
            let focus_handle = view.read(cx).focus_handle(cx);
            focus_handle.focus(window, cx);
            return;
        }

        let shell = Some(session.command.clone());
        let shell_args = if session.args.is_empty() {
            None
        } else {
            Some(session.args.clone())
        };
        let working_directory = if session.project_path.is_empty() {
            env::current_dir().ok()
        } else {
            Some(PathBuf::from(session.project_path.clone()))
        };

        let mut env_vars: HashMap<String, String> = env::vars().collect();
        env_vars.insert("TERM".to_string(), "xterm-256color".to_string());
        env_vars.insert("COLORTERM".to_string(), "truecolor".to_string());

        let window_id = window.window_handle().window_id().as_u64();
        let terminal_task = TerminalBuilder::new(
            working_directory,
            shell,
            shell_args,
            env_vars,
            None,
            window_id,
            cx,
        );

        let session_id = session.id.clone();
        let window_handle = window.window_handle();
        cx.spawn(move |this: WeakEntity<Self>, cx: &mut AsyncApp| {
            let mut cx = cx.clone();
            async move {
                let builder = match terminal_task.await {
                    Ok(b) => b,
                    Err(_) => return,
                };

                let _ = cx.update_window(window_handle, |_, window, cx| {
                    let _ = this.update(cx, |app, cx| {
                        let terminal = cx.new(|cx| builder.subscribe(cx));
                        let terminal_view =
                            cx.new(|cx| TerminalView::new(terminal.clone(), window, cx));
                        app.terminals.insert(session_id.clone(), terminal);
                        app.terminal_views
                            .insert(session_id.clone(), terminal_view.clone());

                        let focus_handle = terminal_view.read(cx).focus_handle(cx);
                        focus_handle.focus(window, cx);
                        cx.notify();
                    });
                });
            }
        })
        .detach();
    }

    fn sidebar_shadow() -> Vec<BoxShadow> {
        vec![
            BoxShadow {
                // subtle near-edge shadow for elevation
                color: hsla(0., 0., 0., 0.18),
                offset: point(px(0.0), px(1.0)),
                blur_radius: px(6.0),
                spread_radius: px(0.0),
            },
            BoxShadow {
                color: hsla(0., 0., 0., 0.22),
                offset: point(px(0.0), px(8.0)),
                blur_radius: px(22.0),
                spread_radius: px(0.0),
            },
            BoxShadow {
                color: hsla(0., 0., 0., 0.18),
                offset: point(px(0.0), px(22.0)),
                blur_radius: px(54.0),
                spread_radius: px(0.0),
            },
        ]
    }

    fn render_sidebar_shell(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let resizer_hover_bg = cx.theme().foreground.alpha(0.20);
        let base = rgba(rgba_u32(SURFACE_SIDEBAR, SIDEBAR_GLASS_BASE_ALPHA));

        div()
            .id("sidebar-shell")
            .absolute()
            .left(px(SIDEBAR_INSET))
            .top(px(SIDEBAR_INSET))
            .bottom(px(SIDEBAR_INSET))
            .w(px(self.sidebar_width))
            .child(
                div()
                    .id("sidebar-wrapper")
                    .size_full()
                    .rounded(px(16.0))
                    .overflow_hidden()
                    .bg(base)
                    .shadow(Self::sidebar_shadow())
                    .child(
                        div()
                            .id("sidebar-glass")
                            .size_full()
                            .relative()
                            .child(self.render_sidebar_content(cx)),
                    ),
            )
            .child(
                div()
                    .id("sidebar-resizer")
                    .absolute()
                    .top_0()
                    .bottom_0()
                    .left(px(self.sidebar_width - 3.0))
                    .w(px(6.0))
                    .rounded(px(999.0))
                    .bg(gpui::transparent_black())
                    .cursor_col_resize()
                    .hover(move |s| s.bg(resizer_hover_bg))
                    .on_mouse_down(MouseButton::Left, cx.listener(Self::start_sidebar_resize))
                    .on_mouse_up(MouseButton::Left, cx.listener(Self::stop_sidebar_resize)),
            )
    }

    fn render_sidebar_content(&self, cx: &mut Context<Self>) -> impl IntoElement {
        div()
            .id("sidebar-content")
            .size_full()
            .flex()
            .flex_col()
            .child(self.render_sidebar_header(cx))
            .child(self.render_add_project(cx))
            .child(self.render_sections_list(cx))
    }

    fn render_sidebar_header(&self, cx: &mut Context<Self>) -> impl IntoElement {
        div()
            .h(px(44.0))
            .pl(px(SIDEBAR_HEADER_LEFT_PADDING))
            .pr(px(12.0))
            .flex()
            .items_center()
            .justify_between()
            .border_b_1()
            .border_color(rgba(rgba_u32(BORDER_SOFT, BORDER_SOFT_ALPHA)))
            .child(
                div()
                    .text_sm()
                    .font_weight(gpui::FontWeight::SEMIBOLD)
                    .text_color(cx.theme().foreground)
                    .child("AGENT TERM"),
            )
            .child(
                div()
                    .flex()
                    .gap(px(10.0))
                    .child(
                        Button::new("sidebar-new-tab")
                            .label("T")
                            .ghost()
                            .compact()
                            .on_click(cx.listener(|this, _: &ClickEvent, window, cx| {
                                this.new_shell_tab(&NewShellTab, window, cx);
                            })),
                    )
                    .child(
                        Button::new("sidebar-mcp")
                            .label("M")
                            .ghost()
                            .compact()
                            .on_click(cx.listener(|this, _: &ClickEvent, window, cx| {
                                this.open_mcp_manager(&ToggleMcpManager, window, cx);
                            })),
                    ),
            )
    }

    fn render_add_project(&self, cx: &mut Context<Self>) -> impl IntoElement {
        div().px(px(16.0)).py(px(12.0)).child(
            div()
                .id("sidebar-add-project")
                .text_sm()
                .text_color(cx.theme().muted_foreground)
                .cursor_pointer()
                .hover(|s| s.text_color(cx.theme().foreground))
                .on_click(cx.listener(|this, _: &ClickEvent, _window, cx| {
                    let name = "New Project".to_string();
                    let path = String::new();
                    let _ = this.session_store.create_section(name, path);
                    this.reload_from_store(cx);
                }))
                .child("+ Add Project"),
        )
    }

    fn render_sections_list(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let mut list = div()
            .id("sidebar-sections-scroll")
            .flex_1()
            .overflow_y_scroll()
            .px(px(8.0));
        for section in &self.sections {
            list = list.child(self.render_section(section, cx));
        }
        list
    }

    fn render_section(&self, section: &SectionItem, cx: &mut Context<Self>) -> impl IntoElement {
        let sessions: Vec<&SessionRecord> = self
            .sessions
            .iter()
            .filter(|s| s.section_id == section.section.id)
            .collect();

        let section_id = section.section.id.clone();
        let is_collapsed = section.section.collapsed;
        let section_icon = section.section.icon.clone();

        let hover_bg = cx.theme().list_hover;
        let section_header = div()
            .id(format!("section-header-{}", section.section.id))
            .px(px(8.0))
            .py(px(6.0))
            .flex()
            .items_center()
            .gap(px(6.0))
            .rounded(px(6.0))
            .cursor_pointer()
            .hover(move |s| s.bg(hover_bg))
            .on_click(cx.listener({
                let section_id = section.section.id.clone();
                move |this, _, _, cx| {
                    this.toggle_section_collapsed(section_id.clone(), cx);
                    cx.notify();
                }
            }))
            .child(
                Icon::new(if is_collapsed {
                    IconName::ChevronRight
                } else {
                    IconName::ChevronDown
                })
                .size(IconSize::Small)
                .color(cx.theme().muted_foreground),
            )
            .child(
                section_icon
                    .as_ref()
                    .map(|s| icon_from_string(s))
                    .unwrap_or_else(|| Icon::new(IconName::Folder))
                    .size(IconSize::Medium)
                    .color(cx.theme().foreground),
            )
            .child(
                div()
                    .text_sm()
                    .font_weight(gpui::FontWeight::MEDIUM)
                    .text_color(cx.theme().foreground)
                    .flex_1()
                    .child(section.section.name.clone()),
            )
            .context_menu({
                let section_id = section_id.clone();
                move |menu, _window, _cx| {
                    menu.menu("Edit Project...", Box::new(EditSection(section_id.clone())))
                        .separator()
                        .menu("Remove Project", Box::new(RemoveSection(section_id.clone())))
                }
            });

        let mut container = div().py(px(4.0)).child(section_header);

        if is_collapsed {
            return container;
        }

        if sessions.is_empty() {
            container = container.child(
                div()
                    .px(px(12.0))
                    .py(px(4.0))
                    .text_sm()
                    .text_color(cx.theme().muted_foreground)
                    .child("No terminals"),
            );
            return container;
        }

        for session in sessions {
            container = container.child(self.render_session_row(session, cx));
        }

        container
    }

    fn render_session_row(
        &self,
        session: &SessionRecord,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let is_active = self
            .active_session_id
            .as_deref()
            .is_some_and(|id| id == session.id);
        let title = if session.title.is_empty() {
            "Terminal".to_string()
        } else {
            session.title.clone()
        };
        let session_id = session.id.clone();
        let session_icon = session.icon.clone();
        let active_bg = cx.theme().list_hover;
        let hover_bg = cx.theme().list_active;

        div()
            .id(format!("session-row-{}", session.id))
            .px(px(8.0))
            .py(px(4.0))
            .flex()
            .items_center()
            .gap(px(6.0))
            .rounded(px(6.0))
            .cursor_pointer()
            .when(is_active, move |s| s.bg(active_bg))
            .hover(move |s| s.bg(hover_bg))
            .child(
                session_icon
                    .as_ref()
                    .map(|s| icon_from_string(s))
                    .unwrap_or_else(|| Icon::new(IconName::Terminal))
                    .size(IconSize::Small)
                    .color(cx.theme().muted_foreground),
            )
            .child(
                div()
                    .text_sm()
                    .text_color(cx.theme().foreground)
                    .truncate()
                    .flex_1()
                    .child(title.clone()),
            )
            .child({
                let id = session.id.clone();
                Button::new(format!("session-close-{}", session.id))
                    .label("×")
                    .ghost()
                    .compact()
                    .on_click(cx.listener(move |this, _: &ClickEvent, window, cx| {
                        this.close_session(id.clone(), window, cx);
                    }))
            })
            .on_click(cx.listener({
                let id = session_id.clone();
                move |this, _: &ClickEvent, window, cx| {
                    this.set_active_session_id(id.clone(), window, cx);
                }
            }))
            .context_menu({
                let session_id = session_id.clone();
                move |menu, _window, _cx| {
                    menu.menu("Edit Tab...", Box::new(RenameSession(session_id.clone())))
                        .menu("Restart", Box::new(RestartSessionAction(session_id.clone())))
                        .separator()
                        .menu("Close", Box::new(CloseSessionAction(session_id.clone())))
                }
            })
    }

    fn render_terminal_container(&self, cx: &mut Context<Self>) -> impl IntoElement {
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

fn resolve_transport(def: &agentterm_mcp::MCPDef) -> String {
    if !def.transport.is_empty() {
        return def.transport.to_uppercase();
    }
    if !def.url.is_empty() {
        return "HTTP".to_string();
    }
    "STDIO".to_string()
}

fn agentterm_input_field(input: &Entity<GpuiInputState>) -> GpuiInput {
    GpuiInput::new(input)
}

impl Render for AgentTermApp {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        div()
            .id("agentterm-gpui")
            .size_full()
            .relative()
            .bg(gpui::transparent_black())
            .track_focus(&self.focus_handle)
            .on_action(cx.listener(Self::toggle_sidebar))
            .on_action(cx.listener(Self::open_mcp_manager))
            .on_action(cx.listener(Self::new_shell_tab))
            .on_action(cx.listener(Self::open_settings))
            .on_action(cx.listener(Self::handle_rename_session))
            .on_action(cx.listener(Self::handle_close_session))
            .on_action(cx.listener(Self::handle_restart_session))
            .on_action(cx.listener(Self::handle_edit_section))
            .on_action(cx.listener(Self::handle_remove_section))
            .on_action(cx.listener(Self::minimize_window))
            .on_action(cx.listener(Self::zoom_window))
            .on_mouse_move(cx.listener(Self::update_sidebar_resize))
            .on_mouse_up(MouseButton::Left, cx.listener(Self::stop_sidebar_resize))
            .child(self.render_terminal_container(cx))
            .when(self.sidebar_visible, |el| {
                el.child(self.render_sidebar_shell(cx))
            })
            // Dialog and sheet layers for gpui-component
            .children(gpui_component::Root::render_dialog_layer(window, cx))
            .children(gpui_component::Root::render_sheet_layer(window, cx))
    }
}

impl Focusable for AgentTermApp {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus_handle.clone()
    }
}
