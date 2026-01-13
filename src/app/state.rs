//! Core AgentTermApp state and lifecycle methods.

use std::collections::HashMap;
use std::env;
use std::path::PathBuf;
use std::sync::Arc;

use agentterm_mcp::McpManager;
use agentterm_session::{
    DEFAULT_SECTION_ID, NewSessionInput, SectionRecord, SessionRecord, SessionStore, SessionTool,
};
use agentterm_tools::{ShellInfo, ToolInfo};
use gpui::{
    App, AsyncApp, Context, Entity, FocusHandle, Focusable, Pixels, WeakEntity, Window, prelude::*,
};
use gpui_term::{Terminal, TerminalBuilder, TerminalView};

use crate::settings::AppSettings;
use crate::theme;
use crate::ui::SectionItem;

/// The main application state for AgentTerm.
pub struct AgentTermApp {
    pub(crate) focus_handle: FocusHandle,

    pub session_store: SessionStore,
    pub(crate) mcp_manager: McpManager,
    pub(crate) tokio: Arc<tokio::runtime::Runtime>,

    pub(crate) sidebar_visible: bool,
    pub(crate) sidebar_width: f32,

    // Sidebar resize state (consumer-managed for SidebarShell)
    pub(crate) resizing_sidebar: bool,
    pub(crate) resize_start_x: Pixels,
    pub(crate) resize_start_width: f32,

    pub(crate) sections: Vec<SectionItem>,
    pub(crate) sessions: Vec<SessionRecord>,
    pub(crate) active_session_id: Option<String>,

    pub(crate) terminals: HashMap<String, Entity<Terminal>>,
    pub(crate) terminal_views: HashMap<String, Entity<TerminalView>>,

    pub(crate) session_menu_open: bool,
    pub(crate) session_menu_session_id: Option<String>,

    pub(crate) settings: AppSettings,

    // Cached data for tab picker dropdown
    pub(crate) cached_shells: Vec<ShellInfo>,
    pub(crate) cached_tools: Vec<ToolInfo>,
    pub(crate) cached_pinned_shell_ids: Vec<String>,
}

impl AgentTermApp {
    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
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

        // Initialize MCP pool on startup if auto_start is enabled
        if let Err(e) = tokio.block_on(mcp_manager.initialize_pool()) {
            agentterm_mcp::diagnostics::log(format!("pool_startup_init_failed error={}", e));
        }

        let mut this = Self {
            focus_handle,
            session_store,
            mcp_manager,
            tokio,
            sidebar_visible: true,
            sidebar_width: 250.0,
            resizing_sidebar: false,
            resize_start_x: gpui::px(0.0),
            resize_start_width: 0.0,
            sections: Vec::new(),
            sessions: Vec::new(),
            active_session_id: None,
            terminals: HashMap::new(),
            terminal_views: HashMap::new(),
            session_menu_open: false,
            session_menu_session_id: None,
            settings: AppSettings::load(),
            cached_shells: Vec::new(),
            cached_tools: Vec::new(),
            cached_pinned_shell_ids: Vec::new(),
        };

        this.reload_from_store(cx);
        this.load_tab_picker_cache();
        this.ensure_active_terminal(window, cx);
        this
    }

    pub fn reload_from_store(&mut self, cx: &mut Context<Self>) {
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

    pub fn active_session(&self) -> Option<&SessionRecord> {
        let id = self.active_session_id.as_deref()?;
        self.sessions.iter().find(|s| s.id == id)
    }

    pub fn active_section(&self) -> Option<&SectionRecord> {
        let session = self.active_session()?;
        self.sections
            .iter()
            .find(|s| s.section.id == session.section_id)
            .map(|s| &s.section)
    }

    pub fn set_active_session_id(
        &mut self,
        id: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.active_session_id.as_deref() == Some(&id) {
            return;
        }
        let _ = self.session_store.set_active_session(Some(id.clone()));
        self.active_session_id = Some(id);
        self.ensure_active_terminal(window, cx);
        cx.notify();
    }

    pub fn close_session(&mut self, id: String, window: &mut Window, cx: &mut Context<Self>) {
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

    pub fn restart_session(&mut self, id: String, window: &mut Window, cx: &mut Context<Self>) {
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

    pub fn create_session_from_tool(
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

        // Inject --mcp-config for Claude if project has managed MCP config
        let final_args = self.maybe_inject_mcp_config(&tool, args, &project_path);

        let input = NewSessionInput {
            title,
            project_path,
            section_id,
            tool,
            command,
            args: if final_args.is_empty() {
                None
            } else {
                Some(final_args)
            },
            icon,
        };

        match self.session_store.create_session(input) {
            Ok(record) => {
                let _ = self
                    .session_store
                    .set_active_session(Some(record.id.clone()));
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

    /// Create a new session in a specific section (project).
    /// Similar to `create_session_from_tool` but allows specifying the target section.
    pub fn create_session_in_section(
        &mut self,
        section_id: String,
        tool: SessionTool,
        title: String,
        command: String,
        args: Vec<String>,
        icon: Option<String>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        // Look up the section to get its project path
        let project_path = self
            .sections
            .iter()
            .find(|s| s.section.id == section_id)
            .map(|s| s.section.path.clone())
            .unwrap_or_default();

        // Inject --mcp-config for Claude if project has managed MCP config
        let final_args = self.maybe_inject_mcp_config(&tool, args, &project_path);

        let input = NewSessionInput {
            title,
            project_path,
            section_id,
            tool,
            command,
            args: if final_args.is_empty() {
                None
            } else {
                Some(final_args)
            },
            icon,
        };

        match self.session_store.create_session(input) {
            Ok(record) => {
                let _ = self
                    .session_store
                    .set_active_session(Some(record.id.clone()));
                self.reload_from_store(cx);
                self.ensure_active_terminal(window, cx);
            }
            Err(e) => {
                eprintln!("Failed to create session: {}", e);
            }
        }
        cx.notify();
    }

    /// Inject --mcp-config argument for supported tools if a managed MCP config exists
    fn maybe_inject_mcp_config(
        &self,
        tool: &SessionTool,
        mut args: Vec<String>,
        project_path: &str,
    ) -> Vec<String> {
        if matches!(tool, SessionTool::Claude) {
            if let Some(config_path) = self.mcp_manager.get_project_mcp_config_path(project_path) {
                args.push("--mcp-config".to_string());
                args.push(config_path.to_string_lossy().to_string());
            }
        }
        args
    }

    /// Load cached shells and tools data for tab picker dropdown menus.
    /// Called once at startup and can be refreshed if needed.
    pub fn load_tab_picker_cache(&mut self) {
        // Load available shells (synchronous, no async needed)
        self.cached_shells = agentterm_tools::available_shells();

        // Load enabled tools (async, block on tokio)
        match self
            .tokio
            .block_on(agentterm_tools::tools_list(&self.mcp_manager))
        {
            Ok(list) => {
                self.cached_tools = list.into_iter().filter(|t| t.enabled).collect();
            }
            Err(e) => {
                eprintln!("Failed to load tools: {}", e);
                self.cached_tools = Vec::new();
            }
        }

        // Load pinned shell IDs
        match self
            .tokio
            .block_on(agentterm_tools::get_pinned_shells(&self.mcp_manager))
        {
            Ok(list) => {
                self.cached_pinned_shell_ids = list;
            }
            Err(e) => {
                eprintln!("Failed to load pinned shells: {}", e);
                self.cached_pinned_shell_ids = Vec::new();
            }
        }
    }

    pub fn ensure_active_terminal(&mut self, window: &mut Window, cx: &mut Context<Self>) {
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
                        let font_family = app.settings.font_family.clone();
                        let font_size = app.settings.font_size;
                        let terminal_view = cx.new(|cx| {
                            TerminalView::with_settings(
                                terminal.clone(),
                                window,
                                cx,
                                font_family,
                                font_size,
                            )
                        });
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

    /// Updates the application settings and propagates changes to terminal views.
    pub fn update_settings(
        &mut self,
        settings: AppSettings,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        // Update font settings for all terminal views
        let font_family = settings.font_family.clone();
        let font_size = settings.font_size;
        for terminal_view in self.terminal_views.values() {
            terminal_view.update(cx, |view, _| {
                view.set_font_settings(font_family.clone(), font_size);
            });
        }

        // Update window background appearance if blur setting changed
        if settings.blur_enabled != self.settings.blur_enabled {
            let appearance = if settings.blur_enabled {
                gpui::WindowBackgroundAppearance::Blurred
            } else {
                gpui::WindowBackgroundAppearance::Transparent
            };
            window.set_background_appearance(appearance);
        }

        let resolved_mode = theme::apply_theme_from_settings(&settings, Some(window), cx);
        theme::apply_terminal_scheme(&settings, resolved_mode);

        self.settings = settings;

        // Trigger re-render for transparency and other changes
        cx.notify();
    }
}

impl Focusable for AgentTermApp {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus_handle.clone()
    }
}
