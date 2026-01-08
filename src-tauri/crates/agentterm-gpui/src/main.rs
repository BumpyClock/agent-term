use std::collections::HashMap;
use std::env;
use std::path::PathBuf;

use agentterm_mcp::{McpManager, McpScope};
use agentterm_session::{
    DEFAULT_SECTION_ID, NewSessionInput, SectionRecord, SessionRecord, SessionStore, SessionTool,
};
use agentterm_tools::{ShellInfo, ShellType, ToolInfo};
use gpui::{
    App, Application, AsyncApp, BoxShadow, ClickEvent, Context, Entity, FocusHandle, Focusable,
    InteractiveElement, IntoElement, KeyBinding, MouseButton, MouseDownEvent, MouseMoveEvent,
    MouseUpEvent, ParentElement, Pixels, Render, SharedString, StatefulInteractiveElement, Styled,
    WeakEntity, Window, WindowBackgroundAppearance, WindowOptions, actions, div, hsla, point,
    prelude::*, px, rgb, rgba,
};
use gpui_term::{Clear, Copy, Paste, SelectAll, Terminal, TerminalBuilder, TerminalView};

actions!(
    agentterm_gpui,
    [Quit, ToggleSidebar, ToggleMcpManager, NewShellTab]
);

const SIDEBAR_INSET: f32 = 8.0;
const SIDEBAR_GAP: f32 = 16.0;
const SIDEBAR_MIN_WIDTH: f32 = 200.0;
const SIDEBAR_MAX_WIDTH: f32 = 420.0;
const SIDEBAR_HEADER_LEFT_PADDING: f32 = 68.0;

const TEXT_PRIMARY: u32 = 0xd8d8d8;
const TEXT_SUBTLE: u32 = 0xa6a6a6;
const TEXT_FAINT: u32 = 0x5a5a5a;

const SURFACE_ROOT: u32 = 0x000000;
const SURFACE_SIDEBAR: u32 = 0x202020;
const BORDER_SOFT: u32 = 0x3a3a3a;

const SURFACE_ROOT_ALPHA: f32 = 0.05;
const SURFACE_SIDEBAR_ALPHA: f32 = 0.32;
const BORDER_SOFT_ALPHA: f32 = 0.50;

const ENABLE_BLUR: bool = true;

fn rgba_u32(rgb: u32, alpha: f32) -> u32 {
    let a = (alpha.clamp(0.0, 1.0) * 255.0).round() as u32;
    (rgb << 8) | a
}

fn main() {
    Application::new().run(|cx: &mut App| {
        cx.bind_keys([
            KeyBinding::new("cmd-q", Quit, None),
            KeyBinding::new("cmd-b", ToggleSidebar, None),
            KeyBinding::new("cmd-m", ToggleMcpManager, None),
            KeyBinding::new("cmd-t", NewShellTab, None),
            KeyBinding::new("cmd-c", Copy, Some("Terminal")),
            KeyBinding::new("cmd-v", Paste, Some("Terminal")),
            KeyBinding::new("cmd-a", SelectAll, Some("Terminal")),
            KeyBinding::new("cmd-k", Clear, Some("Terminal")),
        ]);

        cx.on_action(|_: &Quit, cx| cx.quit());

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
            cx.new(|cx| AgentTermApp::new(window, cx))
        })
        .unwrap();
    });
}

#[derive(Clone)]
struct SectionItem {
    section: SectionRecord,
    is_default: bool,
}

#[derive(Clone)]
struct McpItem {
    name: SharedString,
    description: SharedString,
    transport: SharedString,
    is_orphan: bool,
}

struct AgentTermApp {
    focus_handle: FocusHandle,

    session_store: SessionStore,
    mcp_manager: McpManager,
    tokio: tokio::runtime::Runtime,

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

    tab_picker_open: bool,
    tab_picker_loading: bool,
    tab_picker_error: Option<SharedString>,
    tab_picker_tools: Vec<ToolInfo>,
    tab_picker_shells: Vec<ShellInfo>,
    tab_picker_pinned_shell_ids: Vec<String>,

    mcp_dialog_open: bool,
    mcp_scope: McpScope,
    mcp_attached: Vec<SharedString>,
    mcp_available: Vec<McpItem>,
    mcp_error: Option<SharedString>,
}

impl AgentTermApp {
    fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let focus_handle = cx.focus_handle();
        focus_handle.focus(window, cx);

        let tokio = tokio::runtime::Builder::new_multi_thread()
            .enable_all()
            .build()
            .expect("tokio runtime");

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
            tab_picker_open: false,
            tab_picker_loading: false,
            tab_picker_error: None,
            tab_picker_tools: Vec::new(),
            tab_picker_shells: Vec::new(),
            tab_picker_pinned_shell_ids: Vec::new(),
            mcp_dialog_open: false,
            mcp_scope: McpScope::Global,
            mcp_attached: Vec::new(),
            mcp_available: Vec::new(),
            mcp_error: None,
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
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.tab_picker_open = false;
        self.mcp_dialog_open = !self.mcp_dialog_open;
        self.mcp_error = None;
        if self.mcp_dialog_open {
            self.refresh_mcp_data();
        }
        cx.notify();
    }

    fn new_shell_tab(&mut self, _: &NewShellTab, window: &mut Window, cx: &mut Context<Self>) {
        self.tab_picker_open = !self.tab_picker_open;
        self.tab_picker_error = None;
        if self.tab_picker_open {
            self.mcp_dialog_open = false;
            self.refresh_tab_picker_data();
        } else {
            self.tab_picker_loading = false;
        }
        self.ensure_active_terminal(window, cx);
        cx.notify();
    }

    fn refresh_tab_picker_data(&mut self) {
        self.tab_picker_loading = true;
        self.tab_picker_tools.clear();
        self.tab_picker_shells.clear();
        self.tab_picker_pinned_shell_ids.clear();

        let tools = match self.tokio.block_on(agentterm_tools::tools_list(&self.mcp_manager)) {
            Ok(list) => list.into_iter().filter(|t| t.enabled).collect(),
            Err(e) => {
                self.tab_picker_error = Some(e.to_string().into());
                Vec::new()
            }
        };

        let pinned =
            match self.tokio.block_on(agentterm_tools::get_pinned_shells(&self.mcp_manager)) {
                Ok(list) => list,
                Err(e) => {
                    self.tab_picker_error = Some(e.to_string().into());
                    Vec::new()
                }
            };

        self.tab_picker_tools = tools;
        self.tab_picker_shells = agentterm_tools::available_shells();
        self.tab_picker_pinned_shell_ids = pinned;
        self.tab_picker_loading = false;
    }

    fn toggle_pin_shell(&mut self, shell_id: String, cx: &mut Context<Self>) {
        match self
            .tokio
            .block_on(agentterm_tools::toggle_pin_shell(&self.mcp_manager, shell_id))
        {
            Ok(pinned) => self.tab_picker_pinned_shell_ids = pinned,
            Err(e) => self.tab_picker_error = Some(e.to_string().into()),
        }
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
                self.tab_picker_open = false;
                self.reload_from_store(cx);
                self.ensure_active_terminal(window, cx);
            }
            Err(e) => {
                self.tab_picker_error = Some(e.to_string().into());
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

    fn refresh_mcp_data(&mut self) {
        let Some(session) = self.active_session() else {
            self.mcp_error = Some("Select a tab to manage MCPs".into());
            self.mcp_attached.clear();
            self.mcp_available.clear();
            return;
        };

        let project_path = if session.project_path.is_empty() {
            None
        } else {
            Some(session.project_path.as_str())
        };

        if self.mcp_scope == McpScope::Local && project_path.is_none() {
            self.mcp_error = Some("Project path is required for local MCPs".into());
            self.mcp_attached.clear();
            self.mcp_available.clear();
            return;
        }

        let attached = self
            .tokio
            .block_on(
                self.mcp_manager
                    .get_attached_mcps(self.mcp_scope, project_path),
            )
            .unwrap_or_default();

        let available = self
            .tokio
            .block_on(self.mcp_manager.get_available_mcps())
            .unwrap_or_default();

        let available_map: HashMap<String, agentterm_mcp::MCPDef> = available;

        let mut items: Vec<McpItem> = available_map
            .iter()
            .map(|(name, def)| McpItem {
                name: name.clone().into(),
                description: def.description.clone().into(),
                transport: resolve_transport(def).into(),
                is_orphan: false,
            })
            .collect();
        items.sort_by(|a, b| a.name.cmp(&b.name));

        self.mcp_attached = attached.into_iter().map(SharedString::from).collect();
        self.mcp_available = items;
        self.mcp_error = None;
    }

    fn mcp_attach(&mut self, name: SharedString, cx: &mut Context<Self>) {
        let Some(session) = self.active_session() else {
            return;
        };
        let project_path = if session.project_path.is_empty() {
            None
        } else {
            Some(session.project_path.as_str())
        };
        if self.mcp_scope == McpScope::Local && project_path.is_none() {
            self.mcp_error = Some("Project path is required for local MCPs".into());
            cx.notify();
            return;
        }
        let res = self.tokio.block_on(self.mcp_manager.attach_mcp(
            self.mcp_scope,
            project_path,
            &name,
        ));
        if let Err(e) = res {
            self.mcp_error = Some(e.to_string().into());
        }
        self.refresh_mcp_data();
        cx.notify();
    }

    fn mcp_detach(&mut self, name: SharedString, cx: &mut Context<Self>) {
        let Some(session) = self.active_session() else {
            return;
        };
        let project_path = if session.project_path.is_empty() {
            None
        } else {
            Some(session.project_path.as_str())
        };
        if self.mcp_scope == McpScope::Local && project_path.is_none() {
            self.mcp_error = Some("Project path is required for local MCPs".into());
            cx.notify();
            return;
        }
        let res = self.tokio.block_on(self.mcp_manager.detach_mcp(
            self.mcp_scope,
            project_path,
            &name,
        ));
        if let Err(e) = res {
            self.mcp_error = Some(e.to_string().into());
        }
        self.refresh_mcp_data();
        cx.notify();
    }

    fn sidebar_shadow() -> Vec<BoxShadow> {
        vec![
            BoxShadow {
                color: hsla(0., 0., 0., 0.25),
                offset: point(px(0.0), px(18.0)),
                blur_radius: px(45.0),
                spread_radius: px(0.0),
            },
            BoxShadow {
                color: hsla(0., 0., 0., 0.15),
                offset: point(px(0.0), px(6.0)),
                blur_radius: px(18.0),
                spread_radius: px(0.0),
            },
        ]
    }

    fn render_sidebar_shell(&self, cx: &mut Context<Self>) -> impl IntoElement {
        div()
            .id("sidebar-shell")
            .absolute()
            .left(px(SIDEBAR_INSET))
            .top(px(SIDEBAR_INSET))
            .bottom(px(SIDEBAR_INSET))
            .w(px(self.sidebar_width))
            .relative()
            .child(
                div()
                    .id("sidebar-wrapper")
                    .size_full()
                    .rounded(px(16.0))
                    .overflow_hidden()
                    .bg(rgba(rgba_u32(SURFACE_SIDEBAR, SURFACE_SIDEBAR_ALPHA)))
                    .border_1()
                    .border_color(rgba(rgba_u32(BORDER_SOFT, BORDER_SOFT_ALPHA)))
                    .shadow(Self::sidebar_shadow())
                    .child(self.render_sidebar_content(cx)),
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
                    .hover(|s| s.bg(rgba(rgba_u32(TEXT_PRIMARY, 0.20))))
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
                    .text_color(rgb(TEXT_PRIMARY))
                    .child("AGENT TERM"),
            )
            .child(
                div()
                    .flex()
                    .gap(px(10.0))
                    .child(icon_button("T").id("sidebar-new-tab").on_click(cx.listener(
                        |this, _: &ClickEvent, window, cx| {
                            this.new_shell_tab(&NewShellTab, window, cx);
                        },
                    )))
                    .child(icon_button("M").id("sidebar-mcp").on_click(cx.listener(
                        |this, _: &ClickEvent, window, cx| {
                            this.open_mcp_manager(&ToggleMcpManager, window, cx);
                        },
                    ))),
            )
    }

    fn render_add_project(&self, cx: &mut Context<Self>) -> impl IntoElement {
        div().px(px(16.0)).py(px(12.0)).child(
            div()
                .id("sidebar-add-project")
                .text_sm()
                .text_color(rgb(TEXT_SUBTLE))
                .cursor_pointer()
                .hover(|s| s.text_color(rgb(TEXT_PRIMARY)))
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

        let mut container = div().py(px(4.0)).child(
            div()
                .px(px(8.0))
                .py(px(6.0))
                .flex()
                .items_center()
                .gap(px(6.0))
                .rounded(px(6.0))
                .hover(|s| s.bg(rgba(0xffffff10)))
                .child(
                    div()
                        .text_sm()
                        .font_weight(gpui::FontWeight::MEDIUM)
                        .text_color(rgb(TEXT_PRIMARY))
                        .child(section.section.name.clone()),
                ),
        );

        if sessions.is_empty() {
            container = container.child(
                div()
                    .px(px(12.0))
                    .py(px(4.0))
                    .text_sm()
                    .text_color(rgb(TEXT_FAINT))
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
            "Terminal"
        } else {
            session.title.as_str()
        };

        div()
            .id(format!("session-row-{}", session.id))
            .px(px(8.0))
            .py(px(4.0))
            .flex()
            .items_center()
            .justify_between()
            .rounded(px(6.0))
            .cursor_pointer()
            .when(is_active, |s| s.bg(rgba(0xffffff10)))
            .hover(|s| s.bg(rgba(0xffffff15)))
            .child(
                div()
                    .text_sm()
                    .text_color(rgb(TEXT_PRIMARY))
                    .truncate()
                    .child(title.to_string()),
            )
            .child(
                icon_button("×")
                    .id(format!("session-close-{}", session.id))
                    .on_click(cx.listener({
                        let id = session.id.clone();
                        move |this, _: &ClickEvent, window, cx| {
                            this.close_session(id.clone(), window, cx);
                        }
                    })),
            )
            .on_click(cx.listener({
                let id = session.id.clone();
                move |this, _: &ClickEvent, window, cx| {
                    this.set_active_session_id(id.clone(), window, cx);
                }
            }))
    }

    fn render_terminal_container(&self) -> impl IntoElement {
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
                        .text_color(rgb(TEXT_FAINT))
                        .child("No terminal selected"),
                )
            })
    }

    fn render_tab_picker_dialog(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let pinned_set: std::collections::HashSet<&str> = self
            .tab_picker_pinned_shell_ids
            .iter()
            .map(|s| s.as_str())
            .collect();

        let mut pinned_shells: Vec<ShellInfo> = self
            .tab_picker_shells
            .iter()
            .cloned()
            .filter(|s| pinned_set.contains(s.id.as_str()))
            .collect();
        pinned_shells.sort_by(|a, b| a.name.cmp(&b.name));

        let mut native_shells: Vec<ShellInfo> = self
            .tab_picker_shells
            .iter()
            .cloned()
            .filter(|s| s.shell_type == ShellType::Native && !pinned_set.contains(s.id.as_str()))
            .collect();
        native_shells.sort_by(|a, b| a.name.cmp(&b.name));

        let mut wsl_shells: Vec<ShellInfo> = self
            .tab_picker_shells
            .iter()
            .cloned()
            .filter(|s| s.shell_type == ShellType::Wsl && !pinned_set.contains(s.id.as_str()))
            .collect();
        wsl_shells.sort_by(|a, b| a.name.cmp(&b.name));

        let builtin_tools: Vec<ToolInfo> = self
            .tab_picker_tools
            .iter()
            .cloned()
            .filter(|t| t.is_builtin)
            .collect();

        let custom_tools: Vec<ToolInfo> = self
            .tab_picker_tools
            .iter()
            .cloned()
            .filter(|t| !t.is_builtin)
            .collect();

        let mut body = div().flex().flex_col().gap(px(6.0));

        if self.tab_picker_loading {
            body = body.child(
                div()
                    .py(px(10.0))
                    .text_sm()
                    .text_color(rgb(TEXT_SUBTLE))
                    .child("Loading..."),
            );
        } else {
            if !pinned_shells.is_empty() {
                body = body.child(
                    div()
                        .pt(px(8.0))
                        .text_xs()
                        .text_color(rgb(TEXT_SUBTLE))
                        .child("Pinned shells"),
                );
                for shell in pinned_shells {
                    body = body.child(self.render_shell_picker_row(shell, true, cx));
                }
                body = body.child(div().pt(px(2.0)).border_b_1().border_color(rgba(0xffffff10)));
            }

            body = body.child(
                div()
                    .pt(px(8.0))
                    .text_xs()
                    .text_color(rgb(TEXT_SUBTLE))
                    .child("Shells"),
            );
            for shell in native_shells {
                body = body.child(self.render_shell_picker_row(shell, false, cx));
            }
            for shell in wsl_shells {
                body = body.child(self.render_shell_picker_row(shell, false, cx));
            }

            body = body.child(div().pt(px(2.0)).border_b_1().border_color(rgba(0xffffff10)));

            body = body.child(
                div()
                    .pt(px(8.0))
                    .text_xs()
                    .text_color(rgb(TEXT_SUBTLE))
                    .child("Tools"),
            );

            for tool in builtin_tools {
                body = body.child(self.render_tool_picker_row(tool, cx));
            }

            if !custom_tools.is_empty() {
                body = body.child(div().pt(px(2.0)).border_b_1().border_color(rgba(0xffffff10)));
                body = body.child(
                    div()
                        .pt(px(8.0))
                        .text_xs()
                        .text_color(rgb(TEXT_SUBTLE))
                        .child("Custom tools"),
                );
                for tool in custom_tools {
                    body = body.child(self.render_tool_picker_row(tool, cx));
                }
            }
        }

        div()
            .id("tab-picker-overlay")
            .absolute()
            .top_0()
            .left_0()
            .size_full()
            .bg(rgba(rgba_u32(0x000000, 0.25)))
            .on_click(cx.listener(|this, _: &ClickEvent, _window, cx| {
                this.tab_picker_open = false;
                cx.notify();
            }))
            .child(
                div()
                    .id("tab-picker")
                    .absolute()
                    .top(px(SIDEBAR_INSET + 54.0))
                    .left(px(SIDEBAR_INSET + 12.0))
                    .w(px(280.0))
                    .max_h(px(540.0))
                    .rounded(px(12.0))
                    .border_1()
                    .border_color(rgba(rgba_u32(BORDER_SOFT, BORDER_SOFT_ALPHA)))
                    .bg(rgba(rgba_u32(SURFACE_SIDEBAR, 0.92)))
                    .shadow(Self::sidebar_shadow())
                    .px(px(14.0))
                    .py(px(12.0))
                    .overflow_y_scroll()
                    .on_click(|_: &ClickEvent, _, _| {})
                    .child(
                        div()
                            .flex()
                            .items_center()
                            .justify_between()
                            .child(
                                div()
                                    .text_sm()
                                    .font_weight(gpui::FontWeight::SEMIBOLD)
                                    .text_color(rgb(TEXT_PRIMARY))
                                    .child("Create tab"),
                            )
                            .child(icon_button("×").id("tab-picker-close").on_click(cx.listener(
                                |this, _: &ClickEvent, _w, cx| {
                                    this.tab_picker_open = false;
                                    cx.notify();
                                },
                            ))),
                    )
                    .child(body)
                    .when_some(self.tab_picker_error.as_ref(), |el, err| {
                        el.child(
                            div()
                                .pt(px(10.0))
                                .text_sm()
                                .text_color(rgb(0xffaaaa))
                                .child(err.clone()),
                        )
                    }),
            )
    }

    fn render_tool_picker_row(&self, tool: ToolInfo, cx: &mut Context<Self>) -> impl IntoElement {
        let label = tool.name.clone();
        let icon_letter = label.chars().next().unwrap_or('T').to_string();
        div()
            .id(format!("tab-picker-tool-{}", tool.id))
            .px(px(10.0))
            .py(px(8.0))
            .flex()
            .items_center()
            .gap(px(10.0))
            .rounded(px(8.0))
            .cursor_pointer()
            .hover(|s| s.bg(rgba(0xffffff10)))
            .on_click(cx.listener(move |this, _: &ClickEvent, window, cx| {
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

                this.create_session_from_tool(
                    session_tool,
                    tool.name.clone(),
                    tool.command.clone(),
                    tool.args.clone(),
                    icon,
                    window,
                    cx,
                );
            }))
            .child(
                div()
                    .w(px(22.0))
                    .h(px(22.0))
                    .rounded(px(6.0))
                    .bg(rgba(0xffffff10))
                    .flex()
                    .items_center()
                    .justify_center()
                    .text_color(rgb(TEXT_PRIMARY))
                    .text_sm()
                    .child(icon_letter),
            )
            .child(
                div()
                    .text_sm()
                    .text_color(rgb(TEXT_PRIMARY))
                    .truncate()
                    .child(label),
            )
    }

    fn render_shell_picker_row(
        &self,
        shell: ShellInfo,
        pinned: bool,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let label = shell.name.clone();
        let icon_letter = label.chars().next().unwrap_or('S').to_string();
        let shell_id = shell.id.clone();

        div()
            .id(format!("tab-picker-shell-{}", shell.id))
            .px(px(10.0))
            .py(px(8.0))
            .flex()
            .items_center()
            .justify_between()
            .rounded(px(8.0))
            .hover(|s| s.bg(rgba(0xffffff10)))
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap(px(10.0))
                    .flex_1()
                    .cursor_pointer()
                    .on_mouse_down(MouseButton::Left, cx.listener(move |this, _: &MouseDownEvent, window, cx| {
                        let icon = if shell.icon.is_empty() {
                            None
                        } else {
                            Some(shell.icon.clone())
                        };

                        this.create_session_from_tool(
                            SessionTool::Shell,
                            shell.name.clone(),
                            shell.command.clone(),
                            shell.args.clone(),
                            icon,
                            window,
                            cx,
                        );
                    }))
                    .child(
                        div()
                            .w(px(22.0))
                            .h(px(22.0))
                            .rounded(px(6.0))
                            .bg(rgba(0xffffff10))
                            .flex()
                            .items_center()
                            .justify_center()
                            .text_color(rgb(TEXT_PRIMARY))
                            .text_sm()
                            .child(icon_letter),
                    )
                    .child(
                        div()
                            .text_sm()
                            .text_color(rgb(TEXT_PRIMARY))
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
                    .text_color(rgb(TEXT_SUBTLE))
                    .hover(|s| s.text_color(rgb(TEXT_PRIMARY)).bg(rgba(0xffffff10)))
                    .child(if pinned { "★" } else { "☆" })
                    .on_mouse_down(
                        MouseButton::Left,
                        cx.listener(move |this, _: &MouseDownEvent, _w, cx| {
                            this.toggle_pin_shell(shell_id.clone(), cx);
                        }),
                    ),
            )
    }

    fn render_mcp_dialog(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let attached_set: std::collections::HashSet<&str> =
            self.mcp_attached.iter().map(|s| s.as_ref()).collect();

        let mut attached: Vec<McpItem> = self
            .mcp_attached
            .iter()
            .map(|name| {
                self.mcp_available
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
            .mcp_available
            .iter()
            .filter(|m| !attached_set.contains(m.name.as_ref()))
            .cloned()
            .collect();
        available.sort_by(|a, b| a.name.cmp(&b.name));

        let active_session_title = self
            .active_session()
            .map(|s| s.title.clone())
            .unwrap_or_default();

        div()
            .id("mcp-overlay")
            .absolute()
            .top_0()
            .left_0()
            .size_full()
            .bg(rgba(rgba_u32(0x000000, 0.35)))
            .on_click(cx.listener(|this, _: &ClickEvent, _window, cx| {
                this.mcp_dialog_open = false;
                cx.notify();
            }))
            .child(
                div()
                    .id("mcp-dialog")
                    .absolute()
                    .top(px(90.0))
                    .left(px(90.0))
                    .w(px(720.0))
                    .rounded(px(12.0))
                    .border_1()
                    .border_color(rgba(rgba_u32(BORDER_SOFT, BORDER_SOFT_ALPHA)))
                    .bg(rgba(rgba_u32(SURFACE_SIDEBAR, 0.92)))
                    .shadow(Self::sidebar_shadow())
                    .px(px(16.0))
                    .py(px(14.0))
                    .on_click(|_: &ClickEvent, _, _| {})
                    .child(
                        div()
                            .text_lg()
                            .font_weight(gpui::FontWeight::SEMIBOLD)
                            .text_color(rgb(TEXT_PRIMARY))
                            .child("MCP Manager"),
                    )
                    .child(
                        div()
                            .pt(px(2.0))
                            .pb(px(10.0))
                            .text_sm()
                            .text_color(rgb(TEXT_SUBTLE))
                            .child(active_session_title),
                    )
                    .child(
                        div()
                            .flex()
                            .gap(px(8.0))
                            .pb(px(10.0))
                            .items_center()
                            .child(self.render_mcp_scope_tab(cx, McpScope::Global, "Shared"))
                            .child(self.render_mcp_scope_tab(cx, McpScope::Local, "Project"))
                            .child(div().flex_1())
                            .child(icon_button("×").id("mcp-close").on_click(cx.listener(
                                |this, _: &ClickEvent, _w, cx| {
                                    this.mcp_dialog_open = false;
                                    cx.notify();
                                },
                            ))),
                    )
                    .child(
                        div()
                            .flex()
                            .gap(px(12.0))
                            .child(self.render_mcp_column(cx, "Attached", attached, true))
                            .child(self.render_mcp_column(cx, "Available", available, false)),
                    )
                    .when_some(self.mcp_error.as_ref(), |el, err| {
                        el.child(
                            div()
                                .pt(px(10.0))
                                .text_sm()
                                .text_color(rgb(0xffaaaa))
                                .child(err.clone()),
                        )
                    }),
            )
    }

    fn render_mcp_scope_tab(
        &self,
        cx: &mut Context<Self>,
        scope: McpScope,
        label: &'static str,
    ) -> impl IntoElement {
        let has_project_path = self
            .active_session()
            .is_some_and(|s| !s.project_path.is_empty());
        let disabled = scope == McpScope::Local && !has_project_path;
        let is_active = self.mcp_scope == scope;
        div()
            .id(format!("mcp-scope-{}", label))
            .px(px(10.0))
            .py(px(6.0))
            .rounded(px(8.0))
            .bg(if is_active {
                rgba(0xffffff12)
            } else {
                rgba(0xffffff05)
            })
            .hover(|s| s.bg(rgba(0xffffff14)))
            .text_sm()
            .text_color(rgb(if is_active { TEXT_PRIMARY } else { TEXT_SUBTLE }))
            .child(label)
            .when(disabled, |s| s.opacity(0.5).cursor_default())
            .when(!disabled, |s| {
                s.cursor_pointer()
                    .on_click(cx.listener(move |this, _: &ClickEvent, _w, cx| {
                        this.mcp_scope = scope;
                        this.refresh_mcp_data();
                        cx.notify();
                    }))
            })
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
                    .bg(rgba(0xffffff07))
                    .text_sm()
                    .font_weight(gpui::FontWeight::SEMIBOLD)
                    .text_color(rgb(TEXT_PRIMARY))
                    .child(title),
            );

        if items.is_empty() {
            return col.child(
                div()
                    .px(px(12.0))
                    .py(px(12.0))
                    .text_sm()
                    .text_color(rgb(TEXT_FAINT))
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
                                    .text_color(rgb(TEXT_PRIMARY))
                                    .child(name.clone()),
                            )
                            .child(
                                div()
                                    .pt(px(2.0))
                                    .text_xs()
                                    .text_color(rgb(TEXT_SUBTLE))
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
                                    .bg(rgba(0xffffff10))
                                    .text_xs()
                                    .text_color(rgb(TEXT_SUBTLE))
                                    .child(transport),
                            )
                            .child(
                                div()
                                    .px(px(10.0))
                                    .py(px(6.0))
                                    .rounded(px(8.0))
                                    .cursor_pointer()
                                    .bg(if attached {
                                        rgba(0xff444410)
                                    } else {
                                        rgba(0x5eead410)
                                    })
                                    .hover(|s| {
                                        s.bg(if attached {
                                            rgba(0xff444418)
                                        } else {
                                            rgba(0x5eead418)
                                        })
                                    })
                                    .text_sm()
                                    .text_color(rgb(TEXT_PRIMARY))
                                    .child(if attached { "Detach" } else { "Attach" })
                                    .id(format!(
                                        "mcp-{}-{}",
                                        if attached { "detach" } else { "attach" },
                                        name
                                    ))
                                    .on_click(cx.listener(move |this, _: &ClickEvent, _w, cx| {
                                        if attached {
                                            this.mcp_detach(name.clone(), cx);
                                        } else {
                                            this.mcp_attach(name.clone(), cx);
                                        }
                                    })),
                            ),
                    ),
            );
        }

        col
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

fn icon_button(label: &'static str) -> gpui::Div {
    div()
        .w(px(22.0))
        .h(px(22.0))
        .flex()
        .items_center()
        .justify_center()
        .rounded(px(6.0))
        .cursor_pointer()
        .text_color(rgb(TEXT_SUBTLE))
        .hover(|s| s.text_color(rgb(TEXT_PRIMARY)).bg(rgba(0xffffff10)))
        .child(label)
}

impl Render for AgentTermApp {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        div()
            .id("agentterm-gpui")
            .absolute()
            .top_0()
            .left_0()
            .size_full()
            .relative()
            .bg(rgba(rgba_u32(SURFACE_ROOT, SURFACE_ROOT_ALPHA)))
            .track_focus(&self.focus_handle)
            .on_action(cx.listener(Self::toggle_sidebar))
            .on_action(cx.listener(Self::open_mcp_manager))
            .on_action(cx.listener(Self::new_shell_tab))
            .on_mouse_move(cx.listener(Self::update_sidebar_resize))
            .on_mouse_up(MouseButton::Left, cx.listener(Self::stop_sidebar_resize))
            .child(self.render_terminal_container())
            .when(self.sidebar_visible, |el| {
                el.child(self.render_sidebar_shell(cx))
            })
            .when(self.tab_picker_open, |el| {
                el.child(self.render_tab_picker_dialog(cx))
            })
            .when(self.mcp_dialog_open, |el| {
                el.child(self.render_mcp_dialog(cx))
            })
    }
}

impl Focusable for AgentTermApp {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus_handle.clone()
    }
}
