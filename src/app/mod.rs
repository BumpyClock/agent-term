//! Main application module for AgentTerm.
//!
//! This module contains the application entry point and coordinates
//! between the various sub-modules.

pub mod actions;
pub mod command_palette_provider;
pub mod constants;
mod handlers;
mod layout_manager;
mod menus;
mod search_manager;
mod sidebar;
mod state;
mod terminal_container;
mod terminal_pool;
mod window_registry;

pub use layout_manager::LayoutManager;
pub use terminal_pool::TerminalPool;
pub use window_registry::WindowRegistry;

pub use actions::*;
pub use state::AgentTermApp;

use gpui::{
    AnyWindowHandle, App, Application, Context, InteractiveElement, KeyBinding, ParentElement,
    Render, Styled, Window, WindowBackgroundAppearance, WindowOptions, div, prelude::*, px,
};
use gpui_component::{NoiseIntensity, WindowLayoutMode, WindowShell, render_noise_overlay};
use gpui_term::{Clear, Copy, FocusOut, Paste, SelectAll, SendShiftTab, SendTab};

use crate::theme;
use crate::ui::ActiveTheme as _;
use constants::SURFACE_ROOT_ALPHA;
use menus::{app_menus, configure_macos_titlebar};

/// Main entry point for the application.
pub fn run() {
    let _ = fix_path_env::fix();

    let settings = crate::settings::AppSettings::load();
    agentterm_mcp::diagnostics::set_enabled(settings.write_diagnostics_logs);

    // Enable diagnostics early, before any other initialization
    // First check config file for debug flag
    if let Ok(config_path) = agentterm_mcp::config::get_config_path() {
        if let Ok(contents) = std::fs::read_to_string(&config_path) {
            if let Ok(config) = toml::from_str::<agentterm_mcp::config::UserConfig>(&contents) {
                if config.debug {
                    agentterm_mcp::diagnostics::set_enabled(true);
                    agentterm_mcp::diagnostics::log("debug_mode_enabled via config");
                }
            }
        }
    }
    // Also check env var as fallback
    if std::env::var("AGENT_TERM_DIAG")
        .map(|v| matches!(v.to_lowercase().as_str(), "1" | "true" | "yes" | "on"))
        .unwrap_or(false)
    {
        agentterm_mcp::diagnostics::set_enabled(true);
    }

    let app = Application::new().with_assets(crate::assets::Assets);

    // Handle dock icon click when app has no visible windows (macOS)
    // Also handles similar scenarios on other platforms
    app.on_reopen(|cx| {
        if cx.windows().is_empty() {
            create_new_window(cx);
        } else if let Some(window) = cx.windows().first() {
            let _ = cx.update_window(*window, |_root, window, _cx| {
                window.activate_window();
            });
        }
    });

    app.run(|cx: &mut App| {
        // Initialize gpui-component (theme, input bindings, dialogs, menus, etc.)
        gpui_component::init(cx);

        // Initialize the new CommandPalette from gpui-component
        gpui_component::command_palette::CommandPalette::init(
            cx,
            gpui_component::command_palette::CommandPaletteConfig {
                placeholder: "Search sessions, workspaces, actions...".into(),
                width: 600.0,
                max_height: 500.0,
                results_section_title: Some("Session History".into()),
                shortcut: Some("alt-k".into()),
                status_provider: Some(std::sync::Arc::new(|_| {
                    if search_manager::search_indexing_in_progress() {
                        Some("Indexing search history...".into())
                    } else {
                        None
                    }
                })),
                ..Default::default()
            },
        );

        // Warm the search index in the background after startup (optional).
        let warm_search_index = crate::settings::AppSettings::load().warm_search_index;
        if warm_search_index {
            search_manager::warm_search_manager_async(std::time::Duration::from_millis(500));
        }

        // Set up key bindings
        cx.bind_keys([
            KeyBinding::new("cmd-q", Quit, None),
            KeyBinding::new("cmd-n", NewWindow, None),
            KeyBinding::new("cmd-w", CloseTab, None),
            KeyBinding::new("ctrl-w", CloseTab, None),
            KeyBinding::new("cmd-shift-w", CloseWindow, None),
            KeyBinding::new("ctrl-shift-w", CloseWindow, None),
            KeyBinding::new("cmd-b", ToggleSidebar, None),
            KeyBinding::new("cmd-m", ToggleMcpManager, None),
            KeyBinding::new("cmd-t", NewShellTab, None),
            KeyBinding::new("cmd-shift-t", ReopenClosed, None),
            KeyBinding::new("ctrl-shift-t", ReopenClosed, None),
            KeyBinding::new("cmd-,", OpenSettings, None),
            KeyBinding::new("cmd-w", CloseTab, Some("Terminal")),
            KeyBinding::new("ctrl-w", CloseTab, Some("Terminal")),
            KeyBinding::new("cmd-c", Copy, Some("Terminal")),
            KeyBinding::new("cmd-v", Paste, Some("Terminal")),
            KeyBinding::new("cmd-a", SelectAll, Some("Terminal")),
            KeyBinding::new("cmd-k", Clear, Some("Terminal")),
            // Terminal tab handling - intercept tab/shift-tab to send to terminal
            KeyBinding::new("tab", SendTab, Some("Terminal")),
            KeyBinding::new("shift-tab", SendShiftTab, Some("Terminal")),
            KeyBinding::new("alt-shift-tab", FocusOut, Some("Terminal")),
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

        // NewWindow action - creates a new window
        cx.on_action(|_: &NewWindow, cx| {
            create_new_window(cx);
        });

        let layout_manager = LayoutManager::global();
        let layout_store = layout_manager.store().clone();
        if let Some(session) = layout_store.load_last_session() {
            layout_store.set_current_session(session.clone());
            let mut windows = session.windows;
            windows.sort_by_key(|window| window.order);
            let next_order = windows
                .iter()
                .map(|window| window.order)
                .max()
                .unwrap_or(0)
                .saturating_add(1);
            layout_manager.set_next_order(next_order);
            if windows.is_empty() {
                create_new_window(cx);
            } else {
                for window in windows {
                    let _ = create_window_from_layout(window.id, cx);
                }
            }
        } else {
            create_new_window(cx);
        }

        cx.activate(true);
    });
}

impl Render for AgentTermApp {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        // Calculate base surface alpha based on transparency setting
        // Higher transparency = lower alpha (more blur shows through)
        // At transparency=0: full tint (SURFACE_ROOT_ALPHA)
        // At transparency=1: no tint (fully transparent, blur shows through)
        let base_alpha = SURFACE_ROOT_ALPHA * (1.0 - self.settings.window_transparency);
        let mode = if cx.theme().is_dark() {
            gpui_component::theme::ThemeMode::Dark
        } else {
            gpui_component::theme::ThemeMode::Light
        };
        let base_bg = theme::surface_background(mode).alpha(base_alpha);

        let window_bounds = window.window_bounds().get_bounds();
        let window_width = window_bounds.size.width;
        let window_height = window_bounds.size.height;
        let scale_factor = window.scale_factor();
        let blur_enabled = self.settings.blur_enabled;
        let sidebar_visible = self.sidebar_visible;

        // Build background noise overlay
        let background = if blur_enabled {
            Some(render_noise_overlay(
                window_width,
                window_height,
                px(0.0), // No corner radius for full-window surface
                NoiseIntensity::Heavy.opacity(),
                scale_factor,
            ))
        } else {
            None
        };

        // Build overlay children (dialog and sheet layers) first to avoid borrow conflicts
        let dialog_layer = gpui_component::Root::render_dialog_layer(window, cx);
        let sheet_layer = gpui_component::Root::render_sheet_layer(window, cx);
        let overlay_children = div()
            .id("agentterm-overlays")
            .children(dialog_layer)
            .children(sheet_layer)
            .into_any_element();

        // Build sidebar (only if visible) - convert to AnyElement to release borrow
        let sidebar_left: Option<gpui::AnyElement> = if sidebar_visible {
            Some(self.render_sidebar_shell(window, cx).into_any_element())
        } else {
            None
        };

        // Build main content after sidebar - convert to AnyElement
        let main_content = self.render_terminal_container(cx).into_any_element();

        // Build WindowShell with FloatingPanels mode
        let window_shell = WindowShell::new()
            .layout_mode(WindowLayoutMode::FloatingPanels)
            .blur_enabled(blur_enabled)
            .bg(base_bg)
            .when_some(background, |shell, bg| shell.background(bg))
            .when_some(sidebar_left, |shell, sidebar| shell.sidebar_left(sidebar))
            .main(main_content)
            .overlay_children(overlay_children)
            .on_mouse_move({
                let entity = cx.entity().clone();
                move |event, window, cx| {
                    entity.update(cx, |this, cx| {
                        this.update_sidebar_resize(event, window, cx);
                    });
                }
            })
            .on_mouse_up({
                let entity = cx.entity().clone();
                move |event, window, cx| {
                    entity.update(cx, |this, cx| {
                        this.stop_sidebar_resize(event, window, cx);
                    });
                }
            });

        // Wrap WindowShell in a focusable div for action handlers
        div()
            .id("agentterm-gpui")
            .size_full()
            .track_focus(&self.focus_handle)
            .on_action(cx.listener(Self::toggle_sidebar))
            .on_action(cx.listener(Self::open_mcp_manager))
            .on_action(cx.listener(Self::new_shell_tab))
            .on_action(cx.listener(Self::open_settings))
            .on_action(cx.listener(Self::handle_rename_session))
            .on_action(cx.listener(Self::handle_close_session))
            .on_action(cx.listener(Self::handle_close_tab))
            .on_action(cx.listener(Self::handle_restart_session))
            .on_action(cx.listener(Self::handle_edit_workspace))
            .on_action(cx.listener(Self::handle_remove_workspace))
            .on_action(cx.listener(Self::minimize_window))
            .on_action(cx.listener(Self::zoom_window))
            .on_action(cx.listener(Self::handle_close_window))
            .on_action(cx.listener(Self::handle_move_session_to_window))
            .on_action(cx.listener(Self::handle_open_session_in_new_window))
            .on_action(cx.listener(Self::handle_move_workspace_to_window))
            .on_action(cx.listener(Self::handle_move_workspace_to_new_window))
            .on_action(cx.listener(Self::reopen_closed))
            .on_action(cx.listener(Self::toggle_command_palette))
            .child(window_shell)
    }
}

/// Creates a new empty AgentTerm window.
///
/// New windows start with no sessions visible (like browser windows).
/// Users can create new sessions or move existing ones from other windows.
///
/// Returns the window handle if successful.
pub fn create_new_window(cx: &mut App) -> Option<AnyWindowHandle> {
    create_new_window_internal(None, None, cx)
}

/// Creates a new AgentTerm window with a specific session.
///
/// Used by "Open in New Window" to move a session to its own window.
///
/// Returns the window handle if successful.
pub fn create_new_window_with_session(
    session_id: String,
    workspace_id: String,
    cx: &mut App,
) -> Option<AnyWindowHandle> {
    create_new_window_internal(Some((session_id, workspace_id)), None, cx)
}

pub fn create_window_from_layout(
    layout_window_id: String,
    cx: &mut App,
) -> Option<AnyWindowHandle> {
    create_new_window_internal(None, Some(layout_window_id), cx)
}

/// Creates a new AgentTerm window.
///
/// Internal helper that handles all window creation including:
/// - Loading settings and applying theme
/// - Configuring window options (titlebar, background, etc.)
/// - Creating the AgentTermApp instance
///
/// Returns the window handle if successful.
fn create_new_window_internal(
    session_info: Option<(String, String)>,
    layout_window_id: Option<String>,
    cx: &mut App,
) -> Option<AnyWindowHandle> {
    let settings = crate::settings::AppSettings::load();
    let resolved_mode = theme::apply_theme_from_settings(&settings, None, cx);
    theme::apply_terminal_scheme(&settings, resolved_mode);
    let background_appearance = if settings.blur_enabled {
        WindowBackgroundAppearance::Blurred
    } else {
        WindowBackgroundAppearance::Transparent
    };

    let registry = WindowRegistry::global();
    let window_count = registry.window_count();
    let title = if window_count == 0 {
        "Agent Term".into()
    } else {
        format!("Agent Term - {}", window_count + 1).into()
    };

    let window_options = WindowOptions {
        titlebar: Some(gpui::TitlebarOptions {
            title: Some(title),
            appears_transparent: true,
            traffic_light_position: Some(gpui::point(px(16.0), px(16.0))),
            ..Default::default()
        }),
        window_background: background_appearance,
        window_decorations: if cfg!(not(target_os = "macos")) {
            Some(gpui::WindowDecorations::Client)
        } else {
            None
        },
        ..Default::default()
    };

    let layout_manager = LayoutManager::global();
    let layout_store = layout_manager.store().clone();
    let layout_window_id = layout_window_id.unwrap_or_else(|| layout_manager.create_window());

    if let Some((session_id, workspace_id)) = session_info.clone() {
        layout_store.update_window(&layout_window_id, |layout| {
            if !layout.workspace_order.contains(&workspace_id) {
                layout.workspace_order.push(workspace_id.clone());
            }
            layout.append_tab(session_id.clone(), workspace_id);
            layout.active_session_id = Some(session_id);
        });
    }

    let result = cx.open_window(window_options, |window, cx| {
        window.set_background_appearance(background_appearance);
        #[cfg(target_os = "macos")]
        configure_macos_titlebar(window);
        let app = cx.new(|cx| {
            AgentTermApp::new_with_layout(
                window,
                cx,
                layout_store.clone(),
                layout_window_id.clone(),
            )
        });
        cx.new(|cx| gpui_component::Root::new(app, window, cx))
    });

    match result {
        Ok(window_handle) => {
            cx.activate(true);
            Some(window_handle.into())
        }
        Err(_) => None,
    }
}
