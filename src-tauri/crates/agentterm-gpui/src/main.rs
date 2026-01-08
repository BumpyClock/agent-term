use std::collections::HashMap;
use std::env;
use std::path::PathBuf;

use agentterm_mcp::{McpManager, McpScope};
use agentterm_session::{
    DEFAULT_SECTION_ID, NewSessionInput, SectionRecord, SessionRecord, SessionStore, SessionTool,
};
use agentterm_tools::{ShellInfo, ShellType, ToolInfo};
mod assets;
mod icons;
mod settings;
mod settings_dialog;
mod text_input;
mod ui;
use crate::icons::{Icon, IconName, IconSize, icon_from_string};
use settings::AppSettings;
use settings_dialog::SettingsDialog;
use ui::ContextMenuExt;
use gpui::{
    App, Application, AsyncApp, Bounds, BoxShadow, ClickEvent, Context, Entity, FocusHandle,
    Focusable, InteractiveElement, IntoElement, KeyBinding, MouseButton, MouseDownEvent,
    MouseMoveEvent, MouseUpEvent, ParentElement, Pixels, Render, SharedString,
    StatefulInteractiveElement, Styled, WeakEntity, Window, WindowBackgroundAppearance,
    WindowBounds, WindowOptions, actions, div, hsla, point, prelude::*, px, rgb, rgba, size,
};
use gpui_term::{Clear, Copy, Paste, SelectAll, Terminal, TerminalBuilder, TerminalView};
use text_input::TextInput;

actions!(
    agentterm_gpui,
    [Quit, ToggleSidebar, ToggleMcpManager, NewShellTab, OpenSettings]
);

// Actions with data for context menu items
#[derive(Clone, PartialEq, serde::Deserialize, schemars::JsonSchema, gpui::Action)]
pub struct RenameSession(pub String);

#[derive(Clone, PartialEq, serde::Deserialize, schemars::JsonSchema, gpui::Action)]
pub struct CloseSessionAction(pub String);

#[derive(Clone, PartialEq, serde::Deserialize, schemars::JsonSchema, gpui::Action)]
pub struct EditSection(pub String);

#[derive(Clone, PartialEq, serde::Deserialize, schemars::JsonSchema, gpui::Action)]
pub struct RemoveSection(pub String);

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
    Application::new().with_assets(assets::Assets).run(|cx: &mut App| {
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
        text_input::bind_keys(cx);

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

    project_editor_open: bool,
    project_editor_section_id: Option<String>,
    project_editor_name_input: Option<Entity<TextInput>>,
    project_editor_path_input: Option<Entity<TextInput>>,
    project_editor_icon: Option<String>,
    project_editor_error: Option<SharedString>,

    session_menu_open: bool,
    session_menu_session_id: Option<String>,

    session_rename_open: bool,
    session_rename_session_id: Option<String>,
    session_rename_input: Option<Entity<TextInput>>,
    session_rename_error: Option<SharedString>,

    mcp_dialog_open: bool,
    mcp_scope: McpScope,
    mcp_attached: Vec<SharedString>,
    mcp_available: Vec<McpItem>,
    mcp_error: Option<SharedString>,

    settings: AppSettings,
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
            project_editor_open: false,
            project_editor_section_id: None,
            project_editor_name_input: None,
            project_editor_path_input: None,
            project_editor_icon: None,
            project_editor_error: None,
            session_menu_open: false,
            session_menu_session_id: None,
            session_rename_open: false,
            session_rename_session_id: None,
            session_rename_input: None,
            session_rename_error: None,
            mcp_dialog_open: false,
            mcp_scope: McpScope::Global,
            mcp_attached: Vec::new(),
            mcp_available: Vec::new(),
            mcp_error: None,
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

        self.project_editor_open = true;
        self.project_editor_section_id = Some(section_id);
        self.project_editor_icon = section.icon.clone();
        self.project_editor_error = None;
        self.session_menu_open = false;
        self.session_rename_open = false;

        let name_input = cx.new(|cx| TextInput::new("Project name", section.name, cx));
        let path_input = cx.new(|cx| TextInput::new("Project path", section.path, cx));
        self.project_editor_name_input = Some(name_input.clone());
        self.project_editor_path_input = Some(path_input);

        let focus_handle = { name_input.read(cx).focus_handle_clone() };
        window.focus(&focus_handle, cx);
        cx.notify();
    }

    fn close_project_editor(&mut self, cx: &mut Context<Self>) {
        self.project_editor_open = false;
        self.project_editor_section_id = None;
        self.project_editor_name_input = None;
        self.project_editor_path_input = None;
        self.project_editor_icon = None;
        self.project_editor_error = None;
        cx.notify();
    }

    fn save_project_editor(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let Some(section_id) = self.project_editor_section_id.clone() else {
            return;
        };
        let Some(name_input) = self.project_editor_name_input.as_ref() else {
            return;
        };
        let Some(path_input) = self.project_editor_path_input.as_ref() else {
            return;
        };

        let name = name_input.read(cx).text().trim().to_string();
        if name.is_empty() {
            self.project_editor_error = Some("Project name is required".into());
            cx.notify();
            return;
        }

        let path = path_input.read(cx).text().trim().to_string();

        if let Err(e) = self.session_store.rename_section(&section_id, name) {
            self.project_editor_error = Some(e.into());
            cx.notify();
            return;
        }

        if let Err(e) = self.session_store.set_section_path(&section_id, path) {
            self.project_editor_error = Some(e.into());
            cx.notify();
            return;
        }

        self.close_project_editor(cx);
        self.reload_from_store(cx);
        self.ensure_active_terminal(window, cx);
    }

    fn choose_project_path(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.project_editor_path_input.is_none() {
            return;
        };

        let rx = cx.prompt_for_paths(gpui::PathPromptOptions {
            prompt: Some("Select Project Folder".into()),
            directories: true,
            files: false,
            multiple: false,
        });

        let window_handle = window.window_handle();
        cx.spawn(async move |this: WeakEntity<Self>, cx: &mut AsyncApp| {
            let mut cx = cx.clone();
            async move {
                let Ok(result) = rx.await else {
                    return;
                };
                let Ok(Some(paths)) = result else {
                    return;
                };
                let Some(path) = paths.first() else {
                    return;
                };
                let path_str = path.to_string_lossy().to_string();
                let _ = cx.update_window(window_handle, |_, _window, cx| {
                    let _ = this.update(cx, |app, cx| {
                        if let Some(input) = app.project_editor_path_input.as_ref() {
                            input.update(cx, |ti, cx| ti.set_text(path_str.clone(), cx));
                        }
                        cx.notify();
                    });
                });
            }
        })
        .detach();
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
            .set_section_collapsed(&section_id, next)
            .map_err(|e| {
                self.project_editor_error = Some(e.into());
            });
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
        self.tab_picker_open = false;
        self.mcp_dialog_open = false;
        self.project_editor_open = false;
        self.session_rename_open = false;
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
        self.session_rename_open = true;
        self.session_rename_session_id = Some(session_id);
        self.session_rename_error = None;
        self.session_menu_open = false;

        let input = cx.new(|cx| TextInput::new("Tab title", session.title, cx));
        self.session_rename_input = Some(input.clone());
        let focus_handle = { input.read(cx).focus_handle_clone() };
        window.focus(&focus_handle, cx);
        cx.notify();
    }

    fn close_session_rename(&mut self, cx: &mut Context<Self>) {
        self.session_rename_open = false;
        self.session_rename_session_id = None;
        self.session_rename_input = None;
        self.session_rename_error = None;
        cx.notify();
    }

    fn save_session_rename(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let Some(session_id) = self.session_rename_session_id.clone() else {
            return;
        };
        let Some(input) = self.session_rename_input.as_ref() else {
            return;
        };
        let title = input.read(cx).text().trim().to_string();
        if title.is_empty() {
            self.session_rename_error = Some("Title is required".into());
            cx.notify();
            return;
        }

        if let Err(e) = self.session_store.rename_session(&session_id, title, true) {
            self.session_rename_error = Some(e.into());
            cx.notify();
            return;
        }

        self.close_session_rename(cx);
        self.reload_from_store(cx);
        self.ensure_active_terminal(window, cx);
    }

    fn move_session_to_section(
        &mut self,
        session_id: String,
        section_id: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if let Err(e) = self.session_store.move_session(&session_id, section_id) {
            self.session_rename_error = Some(e.into());
        }
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
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.tab_picker_open = false;
        self.project_editor_open = false;
        self.session_menu_open = false;
        self.session_rename_open = false;
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
            self.project_editor_open = false;
            self.session_menu_open = false;
            self.session_rename_open = false;
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
        if self.mcp_dialog_open {
            self.refresh_mcp_data();
        }
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
        if self.mcp_dialog_open {
            self.refresh_mcp_data();
        }
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
        _action: &RemoveSection,
        _window: &mut Window,
        _cx: &mut Context<Self>,
    ) {
        // TODO: Implement section removal
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

        let section_id = section.section.id.clone();
        let is_collapsed = section.section.collapsed;
        let section_icon = section.section.icon.clone();

        let section_header = div()
            .id(format!("section-header-{}", section.section.id))
            .px(px(8.0))
            .py(px(6.0))
            .flex()
            .items_center()
            .gap(px(6.0))
            .rounded(px(6.0))
            .cursor_pointer()
            .hover(|s| s.bg(rgba(0xffffff10)))
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
                .color(rgb(TEXT_SUBTLE)),
            )
            .child(
                section_icon
                    .as_ref()
                    .map(|s| icon_from_string(s))
                    .unwrap_or_else(|| Icon::new(IconName::Folder))
                    .size(IconSize::Medium)
                    .color(rgb(TEXT_PRIMARY)),
            )
            .child(
                div()
                    .text_sm()
                    .font_weight(gpui::FontWeight::MEDIUM)
                    .text_color(rgb(TEXT_PRIMARY))
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
            "Terminal".to_string()
        } else {
            session.title.clone()
        };
        let session_id = session.id.clone();
        let session_icon = session.icon.clone();

        div()
            .id(format!("session-row-{}", session.id))
            .px(px(8.0))
            .py(px(4.0))
            .flex()
            .items_center()
            .gap(px(6.0))
            .rounded(px(6.0))
            .cursor_pointer()
            .when(is_active, |s| s.bg(rgba(0xffffff10)))
            .hover(|s| s.bg(rgba(0xffffff15)))
            .child(
                session_icon
                    .as_ref()
                    .map(|s| icon_from_string(s))
                    .unwrap_or_else(|| Icon::new(IconName::Terminal))
                    .size(IconSize::Small)
                    .color(rgb(TEXT_SUBTLE)),
            )
            .child(
                div()
                    .text_sm()
                    .text_color(rgb(TEXT_PRIMARY))
                    .truncate()
                    .flex_1()
                    .child(title.clone()),
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
                let id = session_id.clone();
                move |this, _: &ClickEvent, window, cx| {
                    this.set_active_session_id(id.clone(), window, cx);
                }
            }))
            .context_menu({
                let session_id = session_id.clone();
                move |menu, _window, _cx| {
                    menu.menu("Rename", Box::new(RenameSession(session_id.clone())))
                        .separator()
                        .menu("Close", Box::new(CloseSessionAction(session_id.clone())))
                }
            })
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

    fn render_project_editor_dialog(
        &mut self,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let name_input = self.project_editor_name_input.clone();
        let path_input = self.project_editor_path_input.clone();
        let current_icon = self.project_editor_icon.clone();
        let error = self.project_editor_error.clone();

        div()
            .id("project-editor-overlay")
            .absolute()
            .inset_0()
            .bg(rgba(0x00000080))
            .flex()
            .items_center()
            .justify_center()
            .on_mouse_down(MouseButton::Left, cx.listener(|this, _, _, cx| {
                this.close_project_editor(cx);
            }))
            .child(
                div()
                    .id("project-editor-dialog")
                    .w(px(400.))
                    .bg(rgb(0x1a1a1a))
                    .border_1()
                    .border_color(rgb(0x3a3a3a))
                    .rounded(px(8.))
                    .flex()
                    .flex_col()
                    .overflow_hidden()
                    .on_mouse_down(MouseButton::Left, |_, _, cx| {
                        cx.stop_propagation();
                    })
                    .child(
                        div()
                            .px(px(16.))
                            .py(px(12.))
                            .border_b_1()
                            .border_color(rgb(0x3a3a3a))
                            .text_lg()
                            .font_weight(gpui::FontWeight::SEMIBOLD)
                            .text_color(rgb(0xd8d8d8))
                            .child("Edit Project"),
                    )
                    .child(
                        div()
                            .px(px(16.))
                            .py(px(16.))
                            .flex()
                            .flex_col()
                            .gap(px(16.))
                            .child(
                                div()
                                    .flex()
                                    .flex_col()
                                    .gap(px(8.))
                                    .child(
                                        div()
                                            .text_sm()
                                            .text_color(rgb(0xa0a0a0))
                                            .child("Icon"),
                                    )
                                    .child(
                                        div()
                                            .flex()
                                            .items_center()
                                            .gap(px(8.))
                                            .child(
                                                div()
                                                    .size(px(40.))
                                                    .rounded(px(4.))
                                                    .bg(rgb(0x2a2a2a))
                                                    .flex()
                                                    .items_center()
                                                    .justify_center()
                                                    .child(
                                                        current_icon
                                                            .as_ref()
                                                            .map(|s| icon_from_string(s))
                                                            .unwrap_or_else(|| {
                                                                Icon::new(IconName::Folder)
                                                            })
                                                            .size(IconSize::Large)
                                                            .color(rgb(0xd8d8d8)),
                                                    ),
                                            )
                                            .child(
                                                div()
                                                    .id("change-icon-btn")
                                                    .px(px(12.))
                                                    .py(px(6.))
                                                    .rounded(px(4.))
                                                    .bg(rgb(0x2a2a2a))
                                                    .text_sm()
                                                    .text_color(rgb(0xd8d8d8))
                                                    .cursor_pointer()
                                                    .hover(|s| s.bg(rgb(0x3a3a3a)))
                                                    .child("Change..."),
                                            ),
                                    ),
                            )
                            .child(
                                div()
                                    .flex()
                                    .flex_col()
                                    .gap(px(8.))
                                    .child(
                                        div()
                                            .text_sm()
                                            .text_color(rgb(0xa0a0a0))
                                            .child("Name"),
                                    )
                                    .when_some(name_input, |el, input| el.child(input)),
                            )
                            .child(
                                div()
                                    .flex()
                                    .flex_col()
                                    .gap(px(8.))
                                    .child(
                                        div()
                                            .text_sm()
                                            .text_color(rgb(0xa0a0a0))
                                            .child("Path"),
                                    )
                                    .when_some(path_input, |el, input| el.child(input)),
                            )
                            .when_some(error, |el, err| {
                                el.child(
                                    div().text_sm().text_color(rgb(0xff6b6b)).child(err),
                                )
                            }),
                    )
                    .child(
                        div()
                            .px(px(16.))
                            .py(px(12.))
                            .border_t_1()
                            .border_color(rgb(0x3a3a3a))
                            .flex()
                            .justify_end()
                            .gap(px(8.))
                            .child(
                                div()
                                    .id("cancel-btn")
                                    .px(px(16.))
                                    .py(px(8.))
                                    .rounded(px(6.))
                                    .bg(rgb(0x2a2a2a))
                                    .text_color(rgb(0xd8d8d8))
                                    .cursor_pointer()
                                    .hover(|s| s.bg(rgb(0x3a3a3a)))
                                    .on_click(cx.listener(|this, _, _, cx| {
                                        this.close_project_editor(cx);
                                    }))
                                    .child("Cancel"),
                            )
                            .child(
                                div()
                                    .id("save-btn")
                                    .px(px(16.))
                                    .py(px(8.))
                                    .rounded(px(6.))
                                    .bg(rgb(0x5eead4))
                                    .text_color(rgb(0x000000))
                                    .font_weight(gpui::FontWeight::MEDIUM)
                                    .cursor_pointer()
                                    .hover(|s| s.opacity(0.9))
                                    .on_click(cx.listener(|this, _, window, cx| {
                                        this.save_project_editor(window, cx);
                                    }))
                                    .child("Save"),
                            ),
                    ),
            )
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
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let project_editor_open = self.project_editor_open;

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
            .on_action(cx.listener(Self::open_settings))
            .on_action(cx.listener(Self::handle_rename_session))
            .on_action(cx.listener(Self::handle_close_session))
            .on_action(cx.listener(Self::handle_edit_section))
            .on_action(cx.listener(Self::handle_remove_section))
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
            .when(project_editor_open, |el| {
                el.child(self.render_project_editor_dialog(window, cx))
            })
    }
}

impl Focusable for AgentTermApp {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus_handle.clone()
    }
}
