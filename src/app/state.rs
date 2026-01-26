//! Core AgentTermApp state and lifecycle methods.

use std::collections::{HashMap, HashSet};
use std::env;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use agentterm_layout::{LayoutStore, WindowSnapshot, closed_tab_from};
use agentterm_mcp::McpManager;
use agentterm_session::{
    DEFAULT_WORKSPACE_ID, NewSessionInput, SessionRecord, SessionStore, SessionTool,
    WorkspaceRecord,
};
use agentterm_tools::{ShellInfo, ToolInfo, get_resolved_shell_with_args, quote_shell_command};
use git2::{DiffOptions, Repository};
use gpui::{
    AnyWindowHandle, App, AsyncApp, Bounds, Context, Entity, FocusHandle, Focusable, Pixels, Point,
    WeakEntity, Window, prelude::*,
};
use gpui_term::{TerminalBuilder, TerminalView};
use notify::{EventKind, RecommendedWatcher, RecursiveMode, Watcher as _};
use smol::{Timer, channel};

use super::layout_manager::LayoutManager;
use super::terminal_pool::TerminalPool;
use super::window_registry::WindowRegistry;

use crate::settings::AppSettings;
use crate::theme;
use crate::ui::WorkspaceItem;
use crate::updater::UpdateManager;

use gpui_component::command_palette::CommandPaletteState;

const RECENTLY_CLOSED_TERMINAL_TTL: Duration = Duration::from_secs(120);
const GIT_STATUS_DEBOUNCE: Duration = Duration::from_millis(500);

/// State for an active drag operation on a session row.
#[derive(Clone, Debug)]
pub(crate) struct DraggingSession {
    pub session_id: String,
    pub workspace_id: String,
    pub start_position: Point<Pixels>,
    pub mouse_position: Point<Pixels>,
    pub drag_offset: Point<Pixels>,
    pub has_moved: bool,
}

#[derive(Clone)]
pub(crate) struct DragSnapshot {
    pub sidebar_bounds: Option<Bounds<Pixels>>,
    pub session_row_bounds: HashMap<String, Bounds<Pixels>>,
    pub workspace_order: Vec<String>,
    pub workspace_session_order: HashMap<String, Vec<String>>,
}

/// Target location for a drop operation.
#[derive(Clone, Debug, PartialEq)]
#[allow(dead_code)]
pub(crate) enum DropTarget {
    /// Drop before this session (insert above)
    BeforeSession { session_id: String, workspace_id: String },
    /// Drop after this session (insert below)
    AfterSession { session_id: String, workspace_id: String },
    /// Drop into a different workspace (at the end)
    IntoWorkspace { workspace_id: String },
}

#[derive(Clone, Copy, Debug)]
pub(crate) struct GitDiffCounts {
    pub(crate) additions: u64,
    pub(crate) deletions: u64,
}

struct GitRepoWatcher {
    _watcher: RecommendedWatcher,
}

/// The main application state for AgentTerm.
pub struct AgentTermApp {
    pub(crate) focus_handle: FocusHandle,

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

    pub(crate) workspaces: Vec<WorkspaceItem>,
    pub(crate) sessions: Vec<SessionRecord>,
    pub(crate) active_session_id: Option<String>,

    /// Drag state for session reordering
    pub(crate) dragging_session: Option<DraggingSession>,
    pub(crate) drop_target: Option<DropTarget>,
    pub(crate) session_row_bounds: HashMap<String, Bounds<Pixels>>,
    pub(crate) sidebar_bounds: Option<Bounds<Pixels>>,
    pub(crate) drag_snapshot: Option<DragSnapshot>,

    // Terminal views are window-specific; terminals live in global TerminalPool
    pub(crate) terminal_views: HashMap<String, Entity<TerminalView>>,

    pub(crate) settings: AppSettings,

    // Cached data for tab picker dropdown
    pub(crate) cached_shells: Vec<ShellInfo>,
    pub(crate) cached_tools: Vec<ToolInfo>,
    pub(crate) cached_pinned_shell_ids: Vec<String>,

    pub(crate) git_repo_cache: HashMap<PathBuf, Option<PathBuf>>,
    pub(crate) git_session_repo_roots: HashMap<String, PathBuf>,
    pub(crate) git_status_counts: HashMap<PathBuf, GitDiffCounts>,
    git_repo_watchers: HashMap<PathBuf, GitRepoWatcher>,

    /// Shared layout store for multi-window session management.
    /// Created once and shared across all windows via Arc.
    pub(crate) layout_store: Arc<LayoutStore>,

    /// This window's unique ID in the layout store.
    /// Used to look up window-specific layout (tabs, workspace order, etc.).
    pub(crate) layout_window_id: String,

    /// Command palette state entity when open.
    pub(crate) command_palette: Option<Entity<CommandPaletteState>>,

    /// Update manager for auto-update functionality.
    pub(crate) update_manager: Entity<UpdateManager>,
}

impl AgentTermApp {
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
        let window_handle: AnyWindowHandle = window.window_handle();

        LayoutManager::global().register_handle(layout_window_id.clone(), window_handle);
        WindowRegistry::global().register(window_handle, weak_self);

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

        // Initialize update manager
        let update_manager = cx.new(|cx| UpdateManager::new(cx));

        let mut this = Self {
            focus_handle,
            window_handle,
            session_store,
            mcp_manager,
            tokio,
            sidebar_visible: true,
            sidebar_width: 250.0,
            resizing_sidebar: false,
            resize_start_x: gpui::px(0.0),
            resize_start_width: 0.0,
            workspaces: Vec::new(),
            sessions: Vec::new(),
            active_session_id: None,
            dragging_session: None,
            drop_target: None,
            session_row_bounds: HashMap::new(),
            sidebar_bounds: None,
            drag_snapshot: None,
            terminal_views: HashMap::new(),
            settings: AppSettings::load(),
            cached_shells: Vec::new(),
            cached_tools: Vec::new(),
            cached_pinned_shell_ids: Vec::new(),
            git_repo_cache: HashMap::new(),
            git_session_repo_roots: HashMap::new(),
            git_status_counts: HashMap::new(),
            git_repo_watchers: HashMap::new(),
            layout_store,
            layout_window_id,
            command_palette: None,
            update_manager,
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
    pub fn add_session_to_layout(&self, session_id: &str, workspace_id: &str) {
        let session_id = session_id.to_string();
        let workspace_id = workspace_id.to_string();
        self.layout_store
            .update_window(&self.layout_window_id, |window| {
                if !window.workspace_order.contains(&workspace_id) {
                    window.workspace_order.push(workspace_id.clone());
                }
                window.append_tab(session_id.clone(), workspace_id);
                window.active_session_id = Some(session_id);
            });
    }

    /// Returns workspaces ordered by this window's layout.
    pub fn ordered_workspaces(&self) -> Vec<WorkspaceItem> {
        let Some(window) = self.window_layout() else {
            return self.workspaces.clone();
        };

        let mut visible_workspaces: Vec<String> = window
            .tabs
            .iter()
            .map(|tab| tab.workspace_id.clone())
            .collect();
        visible_workspaces.sort();
        visible_workspaces.dedup();

        let mut ordered = Vec::new();
        for workspace_id in window.workspace_order {
            if let Some(workspace) = self
                .workspaces
                .iter()
                .find(|s| s.workspace.id == workspace_id)
            {
                ordered.push(workspace.clone());
            }
        }

        for workspace_id in visible_workspaces {
            if !ordered.iter().any(|s| s.workspace.id == workspace_id) {
                if let Some(workspace) = self
                    .workspaces
                    .iter()
                    .find(|s| s.workspace.id == workspace_id)
                {
                    ordered.push(workspace.clone());
                }
            }
        }

        if ordered.is_empty() {
            self.workspaces
                .iter()
                .filter(|s| s.is_default)
                .cloned()
                .collect()
        } else {
            ordered
        }
    }

    /// Returns session records ordered by this window's layout for a workspace.
    pub fn ordered_sessions_for_workspace(&self, workspace_id: &str) -> Vec<&SessionRecord> {
        let Some(window) = self.window_layout() else {
            return self
                .sessions
                .iter()
                .filter(|s| s.workspace_id == workspace_id)
                .collect();
        };

        let mut tabs: Vec<_> = window
            .tabs
            .iter()
            .filter(|t| t.workspace_id == workspace_id)
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

    /// Returns whether a workspace is collapsed in this window.
    pub fn is_workspace_collapsed(&self, workspace_id: &str) -> bool {
        let Some(window) = self.window_layout() else {
            return self
                .workspaces
                .iter()
                .find(|s| s.workspace.id == workspace_id)
                .map(|s| s.workspace.collapsed)
                .unwrap_or(false);
        };

        window
            .collapsed_workspaces
            .contains(&workspace_id.to_string())
    }
    pub fn reload_from_store(&mut self, cx: &mut Context<Self>) {
        let mut workspaces: Vec<WorkspaceItem> = self
            .session_store
            .list_workspaces()
            .into_iter()
            .map(|workspace| WorkspaceItem {
                workspace,
                is_default: false,
            })
            .collect();

        workspaces.sort_by_key(|s| s.workspace.order);
        workspaces.insert(
            0,
            WorkspaceItem {
                workspace: WorkspaceRecord {
                    id: DEFAULT_WORKSPACE_ID.to_string(),
                    name: "Default Workspace".to_string(),
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

        self.workspaces = workspaces;
        self.sessions = sessions;
        self.active_session_id = active_session_id;
        self.refresh_git_status(cx);
        cx.notify();
    }

    fn refresh_git_status(&mut self, cx: &mut Context<Self>) {
        self.git_repo_cache.clear();
        self.git_session_repo_roots.clear();
        let repo_roots = self.collect_git_repo_roots();
        self.sync_git_repo_watchers(repo_roots, cx);
    }

    fn collect_git_repo_roots(&mut self) -> Vec<PathBuf> {
        let mut roots = Vec::new();
        let workspace_paths: HashMap<String, String> = self
            .workspaces
            .iter()
            .map(|workspace| {
                (
                    workspace.workspace.id.clone(),
                    workspace.workspace.path.clone(),
                )
            })
            .collect();
        let sessions: Vec<(String, String, String)> = self
            .sessions
            .iter()
            .map(|session| {
                (
                    session.id.clone(),
                    session.workspace_path.clone(),
                    session.workspace_id.clone(),
                )
            })
            .collect();
        for (session_id, workspace_path, workspace_id) in sessions {
            let mut resolved_path = if workspace_path.is_empty() {
                workspace_paths
                    .get(&workspace_id)
                    .cloned()
                    .unwrap_or_default()
            } else {
                workspace_path
            };
            if resolved_path.is_empty() {
                if let Ok(current_dir) = env::current_dir() {
                    resolved_path = current_dir.to_string_lossy().to_string();
                }
            }
            if resolved_path.is_empty() {
                continue;
            }
            if let Some(root) = self.repo_root_for_path(&resolved_path) {
                self.git_session_repo_roots.insert(session_id, root.clone());
                roots.push(root);
            }
        }
        roots.sort();
        roots.dedup();
        roots
    }

    fn repo_root_for_path(&mut self, workspace_path: &str) -> Option<PathBuf> {
        if workspace_path.is_empty() {
            return None;
        }
        let path = PathBuf::from(workspace_path);
        if let Some(root) = self.git_repo_cache.get(&path) {
            return root.clone();
        }
        let root = Repository::discover(&path)
            .ok()
            .and_then(|repo| repo.workdir().map(|dir| dir.to_path_buf()));
        self.git_repo_cache.insert(path, root.clone());
        root
    }

    fn sync_git_repo_watchers(&mut self, repo_roots: Vec<PathBuf>, cx: &mut Context<Self>) {
        let repo_root_set: HashSet<PathBuf> = repo_roots.iter().cloned().collect();
        self.git_repo_watchers
            .retain(|root, _| repo_root_set.contains(root));
        self.git_status_counts
            .retain(|root, _| repo_root_set.contains(root));
        for root in repo_roots {
            if !self.git_repo_watchers.contains_key(&root) {
                if let Some(watcher) = self.start_git_repo_watcher(root.clone(), cx) {
                    self.git_repo_watchers.insert(root.clone(), watcher);
                }
            }
            self.queue_git_status_refresh(root.clone(), cx);
        }
    }

    fn start_git_repo_watcher(
        &self,
        repo_root: PathBuf,
        cx: &mut Context<Self>,
    ) -> Option<GitRepoWatcher> {
        let (tx, rx) = channel::bounded(100);
        let repo_root_for_watch = repo_root.clone();
        let mut watcher = notify::recommended_watcher(move |res: notify::Result<notify::Event>| {
            if let Ok(event) = res {
                match event.kind {
                    EventKind::Create(_) | EventKind::Modify(_) | EventKind::Remove(_) => {
                        let _ = tx.send_blocking(());
                    }
                    _ => {}
                }
            }
        })
        .ok()?;
        if watcher
            .watch(&repo_root_for_watch, RecursiveMode::Recursive)
            .is_err()
        {
            return None;
        }

        let window_handle = self.window_handle;
        let repo_root_for_task = repo_root;
        cx.spawn(move |this: WeakEntity<Self>, cx: &mut AsyncApp| {
            let mut cx = cx.clone();
            async move {
                loop {
                    if rx.recv().await.is_err() {
                        break;
                    }
                    loop {
                        Timer::after(GIT_STATUS_DEBOUNCE).await;
                        let mut had_more = false;
                        while rx.try_recv().is_ok() {
                            had_more = true;
                        }
                        if !had_more {
                            break;
                        }
                    }
                    let repo_root = repo_root_for_task.clone();
                    let _ = cx.update_window(window_handle, |_, _window, cx| {
                        let _ = this.update(cx, |app, cx| {
                            app.queue_git_status_refresh(repo_root.clone(), cx);
                        });
                    });
                }
            }
        })
        .detach();

        Some(GitRepoWatcher { _watcher: watcher })
    }

    fn queue_git_status_refresh(&mut self, repo_root: PathBuf, cx: &mut Context<Self>) {
        let window_handle = self.window_handle;
        let repo_root_for_task = repo_root.clone();
        let repo_root_for_update = repo_root;
        cx.spawn(move |this: WeakEntity<Self>, cx: &mut AsyncApp| {
            let mut cx = cx.clone();
            async move {
                let counts =
                    smol::unblock(move || Self::compute_git_diff_counts(&repo_root_for_task)).await;
                let _ = cx.update_window(window_handle, |_, _window, cx| {
                    let _ = this.update(cx, |app, cx| {
                        match counts {
                            Some(counts) if counts.additions > 0 || counts.deletions > 0 => {
                                app.git_status_counts
                                    .insert(repo_root_for_update.clone(), counts);
                            }
                            _ => {
                                app.git_status_counts.remove(&repo_root_for_update);
                            }
                        }
                        cx.notify();
                    });
                });
            }
        })
        .detach();
    }

    fn compute_git_diff_counts(repo_root: &Path) -> Option<GitDiffCounts> {
        let repo = Repository::open(repo_root).ok()?;
        let mut staged_options = DiffOptions::new();
        staged_options.include_untracked(false);
        staged_options.recurse_untracked_dirs(false);
        let index = repo.index().ok()?;
        let tree = repo.head().ok().and_then(|head| head.peel_to_tree().ok());
        let staged_diff = repo
            .diff_tree_to_index(tree.as_ref(), Some(&index), Some(&mut staged_options))
            .ok()?;
        let staged_stats = staged_diff.stats().ok()?;

        let mut unstaged_options = DiffOptions::new();
        unstaged_options.include_untracked(false);
        unstaged_options.recurse_untracked_dirs(false);
        let unstaged_diff = repo
            .diff_index_to_workdir(Some(&index), Some(&mut unstaged_options))
            .ok()?;
        let unstaged_stats = unstaged_diff.stats().ok()?;

        let additions = (staged_stats.insertions() + unstaged_stats.insertions()) as u64;
        let deletions = (staged_stats.deletions() + unstaged_stats.deletions()) as u64;
        Some(GitDiffCounts {
            additions,
            deletions,
        })
    }

    pub(crate) fn git_diff_counts_for_session(&self, session_id: &str) -> Option<GitDiffCounts> {
        let repo_root = self.git_session_repo_roots.get(session_id)?;
        self.git_status_counts.get(repo_root).copied()
    }

    pub fn active_session(&self) -> Option<&SessionRecord> {
        let id = self.active_session_id.as_deref()?;
        self.sessions.iter().find(|s| s.id == id)
    }

    pub fn active_workspace(&self) -> Option<&WorkspaceRecord> {
        let session = self.active_session()?;
        self.workspaces
            .iter()
            .find(|s| s.workspace.id == session.workspace_id)
            .map(|s| &s.workspace)
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
                        .any(|window| window.tabs.iter().any(|tab| tab.session_id == session_id));
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
            .and_then(|layout| layout.active_session_id)
            .or_else(|| {
                self.sessions
                    .iter()
                    .find(|s| self.is_session_visible(&s.id))
                    .map(|s| s.id.clone())
            });

        let _ = self
            .session_store
            .set_active_session(next_active_id.clone());
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
        let (workspace_id, workspace_path) = self
            .active_workspace()
            .map(|s| (s.id.clone(), s.path.clone()))
            .unwrap_or_else(|| (DEFAULT_WORKSPACE_ID.to_string(), String::new()));

        // Inject --mcp-config for Claude if workspace has managed MCP config
        let final_args = self.maybe_inject_mcp_config(&tool, args, &workspace_path);

        let input = NewSessionInput {
            title,
            workspace_path,
            workspace_id,
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
                self.add_session_to_layout(&record.id, &record.workspace_id);
                let _ = self
                    .session_store
                    .set_active_session(Some(record.id));
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

    /// Create a new session in a specific workspace.
    /// Similar to `create_session_from_tool` but allows specifying the target workspace.
    pub fn create_session_in_workspace(
        &mut self,
        workspace_id: String,
        tool: SessionTool,
        title: String,
        command: String,
        args: Vec<String>,
        icon: Option<String>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        // Look up the workspace to get its workspace path
        let workspace_path = self
            .workspaces
            .iter()
            .find(|s| s.workspace.id == workspace_id)
            .map(|s| s.workspace.path.clone())
            .unwrap_or_default();

        // Inject --mcp-config for Claude if workspace has managed MCP config
        let final_args = self.maybe_inject_mcp_config(&tool, args, &workspace_path);

        let input = NewSessionInput {
            title,
            workspace_path,
            workspace_id,
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
                self.add_session_to_layout(&record.id, &record.workspace_id);
                let _ = self
                    .session_store
                    .set_active_session(Some(record.id));
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
        workspace_path: &str,
    ) -> Vec<String> {
        if matches!(tool, SessionTool::Claude) {
            if let Some(config_path) = self
                .mcp_manager
                .get_workspace_mcp_config_path(workspace_path)
            {
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
                shell.args,
                icon,
                window,
                cx,
            );
        }
    }

    fn resolve_shell_command(
        &self,
        session: &SessionRecord,
    ) -> (Option<String>, Option<Vec<String>>) {
        if matches!(session.tool, SessionTool::Shell) {
            let shell_args = if session.args.is_empty() {
                None
            } else {
                Some(session.args.clone())
            };
            return (Some(session.command.clone()), shell_args);
        }

        let resolved_shell = get_resolved_shell_with_args(
            self.settings.default_shell_id.as_deref(),
        )
        .unwrap_or_else(|| ShellInfo {
            id: "default".to_string(),
            name: "Shell".to_string(),
            command: agentterm_tools::detect_default_shell(),
            args: Vec::new(),
            icon: String::new(),
            shell_type: agentterm_tools::ShellType::Native,
            is_default: true,
        });

        let full_command = quote_shell_command(&session.command, &session.args);

        #[cfg(not(target_os = "windows"))]
        {
            let mut args = resolved_shell.args.clone();
            args.push("-c".to_string());
            args.push(full_command);
            return (Some(resolved_shell.command), Some(args));
        }

        #[cfg(target_os = "windows")]
        {
            let shell_lower = resolved_shell.command.to_lowercase();
            if shell_lower.contains("pwsh") || shell_lower.contains("powershell") {
                let mut args = vec!["-NoLogo".to_string(), "-Command".to_string()];
                args.push(format!("& {}", full_command));
                return (Some(resolved_shell.command), Some(args));
            }

            if matches!(resolved_shell.shell_type, agentterm_tools::ShellType::Wsl) {
                let mut args = resolved_shell.args.clone();
                args.push("--".to_string());
                args.push(full_command);
                return (Some(resolved_shell.command), Some(args));
            }

            if shell_lower.contains("cmd") {
                let mut args = resolved_shell.args.clone();
                args.push("/c".to_string());
                args.push(full_command);
                return (Some(resolved_shell.command), Some(args));
            }

            let mut args = resolved_shell.args.clone();
            args.push("-c".to_string());
            args.push(full_command);
            return (Some(resolved_shell.command), Some(args));
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

        let (shell, shell_args) = self.resolve_shell_command(&session);
        let working_directory = if session.workspace_path.is_empty() {
            dirs::home_dir().or_else(|| env::current_dir().ok())
        } else {
            Some(PathBuf::from(session.workspace_path.clone()))
        };
        if let Some(ref working_directory) = working_directory {
            agentterm_mcp::diagnostics::log(format!(
                "terminal_start session_id={} tool={:?} cwd={} shell={:?}",
                session.id,
                session.tool,
                working_directory.display(),
                shell
            ));
        }

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

        let session_id = session.id;
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

    /// Completes a drag-and-drop operation by reordering sessions.
    pub fn complete_drag_drop(
        &mut self,
        dragging: DraggingSession,
        target: DropTarget,
        cx: &mut Context<Self>,
    ) {
        let source_session_id = dragging.session_id.clone();
        eprintln!("complete_drag_drop: source={}, target={:?}", source_session_id, target);

        match &target {
            DropTarget::BeforeSession { session_id: target_id, workspace_id } |
            DropTarget::AfterSession { session_id: target_id, workspace_id } => {
                let before = matches!(target, DropTarget::BeforeSession { .. });
                eprintln!("  dragging.workspace_id={}, target workspace_id={}", dragging.workspace_id, workspace_id);
                if dragging.workspace_id == *workspace_id {
                    eprintln!("  calling reorder_session_in_workspace");
                    self.reorder_session_in_workspace(&source_session_id, target_id, workspace_id, before);
                } else {
                    eprintln!("  calling move_session_to_workspace");
                    self.move_session_to_workspace(&source_session_id, workspace_id, Some(target_id), before, cx);
                }
            }
            DropTarget::IntoWorkspace { workspace_id } => {
                self.move_session_to_workspace(&source_session_id, workspace_id, None, false, cx);
            }
        }

        cx.notify();
    }

    /// Reorders a session within the same workspace.
    fn reorder_session_in_workspace(
        &mut self,
        session_id: &str,
        target_session_id: &str,
        _workspace_id: &str,
        before: bool,
    ) {
        eprintln!("reorder_session_in_workspace: session={}, target={}, before={}", session_id, target_session_id, before);
        if session_id == target_session_id {
            eprintln!("  skipping - same session");
            return;
        }

        let updated = self.layout_store.update_window(&self.layout_window_id, |window| {
            let tabs = &mut window.tabs;
            eprintln!("  tabs before: {:?}", tabs.iter().map(|t| (&t.session_id, t.order)).collect::<Vec<_>>());

            let source_idx = tabs.iter().position(|t| t.session_id == session_id);
            let target_idx = tabs.iter().position(|t| t.session_id == target_session_id);
            eprintln!("  source_idx={:?}, target_idx={:?}", source_idx, target_idx);

            if let (Some(src), Some(tgt)) = (source_idx, target_idx) {
                let tab = tabs.remove(src);
                let insert_idx = if src < tgt {
                    if before { tgt - 1 } else { tgt }
                } else if before { tgt } else { tgt + 1 };
                eprintln!("  insert_idx={}", insert_idx);
                tabs.insert(insert_idx.min(tabs.len()), tab);

                for (i, tab) in tabs.iter_mut().enumerate() {
                    tab.order = i as u32;
                }
                eprintln!("  tabs after: {:?}", tabs.iter().map(|t| (&t.session_id, t.order)).collect::<Vec<_>>());
            }
        });
        eprintln!("  update_window returned: {}", updated);
    }

    /// Moves a session to a different workspace.
    fn move_session_to_workspace(
        &mut self,
        session_id: &str,
        target_workspace_id: &str,
        target_session_id: Option<&str>,
        before: bool,
        cx: &mut Context<Self>,
    ) {
        self.layout_store.update_window(&self.layout_window_id, |window| {
            if let Some(tab) = window.tabs.iter_mut().find(|t| t.session_id == session_id) {
                tab.workspace_id = target_workspace_id.to_string();
            }

            if let Some(target_id) = target_session_id {
                let tabs = &mut window.tabs;
                let source_idx = tabs.iter().position(|t| t.session_id == session_id);
                let target_idx = tabs.iter().position(|t| t.session_id == target_id);

                if let (Some(src), Some(tgt)) = (source_idx, target_idx) {
                    let tab = tabs.remove(src);
                    let insert_idx = if src < tgt {
                        if before { tgt - 1 } else { tgt }
                    } else if before { tgt } else { tgt + 1 };
                    tabs.insert(insert_idx.min(tabs.len()), tab);
                }
            }

            for (i, tab) in window.tabs.iter_mut().enumerate() {
                tab.order = i as u32;
            }
        });

        if self.sessions.iter().any(|s| s.id == session_id) {
            let _ = self.session_store.move_session(session_id, target_workspace_id.to_string());
        }

        self.reload_from_store(cx);
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
