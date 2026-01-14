//! Core AgentTermApp state and lifecycle methods.

use std::collections::HashMap;
use std::env;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

use agentterm_layout::{LayoutStore, WindowSnapshot, closed_tab_from};
use agentterm_mcp::McpManager;
use agentterm_session::{
    DEFAULT_SECTION_ID, NewSessionInput, SectionRecord, SessionRecord, SessionStore, SessionTool,
};
use agentterm_tools::{ShellInfo, ToolInfo};
use gpui::{
    AnyWindowHandle, App, AsyncApp, Context, Entity, FocusHandle, Focusable, Pixels, WeakEntity,
    Window, prelude::*,
};
use gpui_term::{TerminalBuilder, TerminalView};
use smol::Timer;

use super::layout_manager::LayoutManager;
use super::terminal_pool::TerminalPool;
use super::window_registry::WindowRegistry;

use crate::settings::AppSettings;
use crate::theme;
use crate::ui::SectionItem;

use gpui_component::command_palette::CommandPaletteState;

const RECENTLY_CLOSED_TERMINAL_TTL: Duration = Duration::from_secs(120);

/// The main application state for AgentTerm.
pub struct AgentTermApp {
    pub(crate) focus_handle: FocusHandle,

    /// Weak reference to self for callbacks and external access.
    pub(crate) weak_self: WeakEntity<Self>,
    /// Handle to this app's window for cross-window operations.
    pub(crate) window_handle: AnyWindowHandle,

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

    // Terminal views are window-specific; terminals live in global TerminalPool
    pub(crate) terminal_views: HashMap<String, Entity<TerminalView>>,

    pub(crate) session_menu_open: bool,
    pub(crate) session_menu_session_id: Option<String>,

    pub(crate) settings: AppSettings,

    // Cached data for tab picker dropdown
    pub(crate) cached_shells: Vec<ShellInfo>,
    pub(crate) cached_tools: Vec<ToolInfo>,
    pub(crate) cached_pinned_shell_ids: Vec<String>,

    /// Shared layout store for multi-window session management.
    /// Created once and shared across all windows via Arc.
    pub(crate) layout_store: Arc<LayoutStore>,

    /// This window's unique ID in the layout store.
    /// Used to look up window-specific layout (tabs, section order, etc.).
    pub(crate) layout_window_id: String,

    /// Command palette state entity when open.
    pub(crate) command_palette: Option<Entity<CommandPaletteState>>,
}

impl AgentTermApp {
    /// Creates a new AgentTermApp with all sessions visible (for first window on app launch).
    /// This is the primary window constructor that uses the shared LayoutManager.
    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let layout_manager = LayoutManager::global();
        let layout_store = layout_manager.store().clone();
        let layout_window_id = layout_manager.create_window();

        Self::new_with_layout(window, cx, layout_store, layout_window_id)
    }

    /// Creates a new AgentTermApp with a specific layout window ID and shared layout store.
    ///
    /// - `layout_store` - Shared layout store (created by first window, passed to subsequent windows)
    /// - `layout_window_id` - Unique ID for this window in the layout store
    pub fn new_with_layout(
        window: &mut Window,
        cx: &mut Context<Self>,
        layout_store: Arc<LayoutStore>,
        layout_window_id: String,
    ) -> Self {
        let focus_handle = cx.focus_handle();
        focus_handle.focus(window, cx);

        let weak_self = cx.entity().downgrade();
        let window_handle: AnyWindowHandle = window.window_handle().into();

        LayoutManager::global().register_handle(layout_window_id.clone(), window_handle);
        WindowRegistry::global().register(window_handle, weak_self.clone());

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
            weak_self,
            window_handle,
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
            terminal_views: HashMap::new(),
            session_menu_open: false,
            session_menu_session_id: None,
            settings: AppSettings::load(),
            cached_shells: Vec::new(),
            cached_tools: Vec::new(),
            cached_pinned_shell_ids: Vec::new(),
            layout_store,
            layout_window_id,
            command_palette: None,
        };

        this.reload_from_store(cx);
        this.load_tab_picker_cache();

        if !this
            .window_layout()
            .map(|w| !w.tabs.is_empty())
            .unwrap_or(false)
        {
            cx.defer_in(window, |app, window, cx| {
                app.create_default_shell_tab(window, cx);
            });
        }

        // Ensure active session is visible in this window (for multi-window support)
        if let Some(active_id) = &this.active_session_id {
            if !this.is_session_visible(active_id) {
                // Active session is not visible, choose first visible session instead
                this.active_session_id = this
                    .sessions
                    .iter()
                    .find(|s| this.is_session_visible(&s.id))
                    .map(|s| s.id.clone());
            }
        }

        // Defer terminal initialization to avoid focus calls during window creation
        // (focus during event processing causes "method can only be called during layout" panic)
        cx.defer_in(window, |app, window, cx| {
            app.ensure_active_terminal(window, cx);
        });

        this
    }

    /// Returns the current window's layout snapshot.
    pub fn window_layout(&self) -> Option<WindowSnapshot> {
        let window = self.layout_store.get_window(&self.layout_window_id);
        if window.is_some() {
            return window;
        }
        if self.layout_store.ensure_window(&self.layout_window_id) {
            return self.layout_store.get_window(&self.layout_window_id);
        }
        None
    }

    /// Checks if a session is visible in this window.
    ///
    /// A session is visible if it has a tab in this window's layout.
    pub fn is_session_visible(&self, session_id: &str) -> bool {
        self.window_layout()
            .map(|w| w.tabs.iter().any(|t| t.session_id == session_id))
            .unwrap_or(false)
    }

    /// Adds a session to this window's layout.
    pub fn add_session_to_layout(&self, session_id: &str, section_id: &str) {
        let session_id = session_id.to_string();
        let section_id = section_id.to_string();
        self.layout_store
            .update_window(&self.layout_window_id, |window| {
                if !window.section_order.contains(&section_id) {
                    window.section_order.push(section_id.clone());
                }
                window.append_tab(session_id, section_id);
            });
    }

    /// Returns sections ordered by this window's layout.
    pub fn ordered_sections(&self) -> Vec<SectionItem> {
        let Some(window) = self.window_layout() else {
            return self.sections.clone();
        };

        let mut visible_sections: Vec<String> = window
            .tabs
            .iter()
            .map(|tab| tab.section_id.clone())
            .collect();
        visible_sections.sort();
        visible_sections.dedup();

        let mut ordered = Vec::new();
        for section_id in window.section_order {
            if let Some(section) = self.sections.iter().find(|s| s.section.id == section_id) {
                ordered.push(section.clone());
            }
        }

        for section_id in visible_sections {
            if !ordered.iter().any(|s| s.section.id == section_id) {
                if let Some(section) = self.sections.iter().find(|s| s.section.id == section_id) {
                    ordered.push(section.clone());
                }
            }
        }

        if ordered.is_empty() {
            self.sections
                .iter()
                .filter(|s| s.is_default)
                .cloned()
                .collect()
        } else {
            ordered
        }
    }

    /// Returns session records ordered by this window's layout for a section.
    pub fn ordered_sessions_for_section(&self, section_id: &str) -> Vec<&SessionRecord> {
        let Some(window) = self.window_layout() else {
            return self
                .sessions
                .iter()
                .filter(|s| s.section_id == section_id)
                .collect();
        };

        let mut tabs: Vec<_> = window
            .tabs
            .iter()
            .filter(|t| t.section_id == section_id)
            .collect();
        tabs.sort_by_key(|tab| tab.order);

        let mut ordered = Vec::new();
        for tab in tabs {
            if let Some(session) = self.sessions.iter().find(|s| s.id == tab.session_id) {
                ordered.push(session);
            }
        }

        ordered
    }

    /// Returns whether a section is collapsed in this window.
    pub fn is_section_collapsed(&self, section_id: &str) -> bool {
        let Some(window) = self.window_layout() else {
            return self
                .sections
                .iter()
                .find(|s| s.section.id == section_id)
                .map(|s| s.section.collapsed)
                .unwrap_or(false);
        };

        window.collapsed_sections.contains(&section_id.to_string())
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
        let layout_active = self
            .window_layout()
            .and_then(|window| window.active_session_id);
        let active_session_id = layout_active
            .or_else(|| self.session_store.active_session_id())
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
        let current_session = self.layout_store.current_session();
        let tab_count: usize = current_session
            .windows
            .iter()
            .map(|window| window.tabs.len())
            .sum();
        if tab_count == 1 {
            let _ = self.layout_store.save_closed_session(current_session);
        }

        if let Some(window_layout) = self.window_layout() {
            if let Some(tab) = window_layout.tabs.iter().find(|tab| tab.session_id == id) {
                let closed = closed_tab_from(tab, Some(self.layout_window_id.clone()));
                let _ = self.layout_store.push_closed_tab(closed);
            }
        }

        self.terminal_views.remove(&id);

        if TerminalPool::global().contains(&id) {
            let session_id = id.clone();
            cx.spawn_in(window, async move |this, window| {
                Timer::after(RECENTLY_CLOSED_TERMINAL_TTL).await;
                let result = this.update_in(window, |app, _window, cx| {
                    let still_open = app
                        .layout_store
                        .current_session()
                        .windows
                        .iter()
                        .any(|window| {
                            window
                                .tabs
                                .iter()
                                .any(|tab| tab.session_id == session_id)
                        });
                    agentterm_session::diagnostics::log(format!(
                        "recently_closed_terminal session_id={} still_open={}",
                        session_id, still_open
                    ));
                    if still_open {
                        return;
                    }
                    if let Some(terminal) = TerminalPool::global().remove(&session_id) {
                        agentterm_session::diagnostics::log(format!(
                            "recently_closed_terminal shutdown session_id={}",
                            session_id
                        ));
                        terminal.update(cx, |terminal, _| terminal.shutdown());
                    }
                });
                if let Err(e) = result {
                    agentterm_session::diagnostics::log(format!(
                        "recently_closed_terminal update_error session_id={} error={}",
                        session_id, e
                    ));
                }
            })
            .detach();
        }

        self.layout_store
            .update_window(&self.layout_window_id, |window_layout| {
                window_layout.remove_tab(&id);
                if window_layout.active_session_id.as_deref() == Some(&id) {
                    window_layout.active_session_id =
                        window_layout.tabs.first().map(|tab| tab.session_id.clone());
                }
            });

        let next_active_id = self
            .window_layout()
            .and_then(|layout| layout.active_session_id.clone())
            .or_else(|| {
                self.sessions
                    .iter()
                    .find(|s| self.is_session_visible(&s.id))
                    .map(|s| s.id.clone())
            });

        let _ = self.session_store.set_active_session(next_active_id.clone());
        self.active_session_id = next_active_id;
        self.reload_from_store(cx);
        if self.active_session_id.is_some() {
            self.ensure_active_terminal(window, cx);
        }
    }

    pub fn restart_session(&mut self, id: String, window: &mut Window, cx: &mut Context<Self>) {
        self.terminal_views.remove(&id);

        if let Some(terminal) = TerminalPool::global().remove(&id) {
            terminal.update(cx, |terminal, _| terminal.shutdown());
        }

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
                // Add to this window's layout
                self.add_session_to_layout(&record.id, &record.section_id);
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
                // Add to this window's layout
                self.add_session_to_layout(&record.id, &record.section_id);
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

    /// Creates a default shell tab for this window.
    ///
    /// Uses the user's default shell preference or falls back to the system default.
    pub fn create_default_shell_tab(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let shells = agentterm_tools::available_shells();
        let default_shell = self
            .settings
            .default_shell_id
            .as_ref()
            .and_then(|id| shells.iter().find(|s| &s.id == id))
            .or_else(|| shells.iter().find(|s| s.is_default))
            .or_else(|| shells.first())
            .cloned();

        if let Some(shell) = default_shell {
            let icon = if shell.icon.is_empty() {
                None
            } else {
                Some(shell.icon.clone())
            };

            self.create_session_from_tool(
                SessionTool::Shell,
                shell.name.clone(),
                shell.command.clone(),
                shell.args.clone(),
                icon,
                window,
                cx,
            );
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

        let pool = TerminalPool::global();
        if let Some(terminal) = pool.get(&session.id) {
            let font_family = self.settings.font_family.clone();
            let font_size = self.settings.font_size;
            let terminal_view = cx.new(|cx| {
                TerminalView::with_settings(terminal.clone(), window, cx, font_family, font_size)
            });
            self.terminal_views
                .insert(session.id.clone(), terminal_view.clone());
            let focus_handle = terminal_view.read(cx).focus_handle(cx);
            focus_handle.focus(window, cx);
            cx.notify();
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
                        TerminalPool::global().insert(session_id.clone(), terminal.clone());

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

impl Drop for AgentTermApp {
    fn drop(&mut self) {
        let layout_manager = LayoutManager::global();
        let layout_store = layout_manager.store().clone();
        layout_store.remove_window(&self.layout_window_id);
        layout_manager.unregister(&self.layout_window_id);
        WindowRegistry::global().unregister(&self.window_handle);

        if layout_manager.window_count() == 0 {
            let session = layout_store.current_session();
            let _ = layout_store.save_closed_session(session.clone());
            let _ = layout_store.save_last_session(session);
        }
    }
}
