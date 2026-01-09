//! Main application module for AgentTerm.
//!
//! This module contains the application entry point and coordinates
//! between the various sub-modules.

pub mod actions;
pub mod constants;
mod handlers;
mod menus;
mod sidebar;
mod state;
mod terminal_container;

pub use actions::*;
pub use state::AgentTermApp;

use gpui::{
    App, Application, Context, InteractiveElement, KeyBinding, MouseButton, ParentElement, Render,
    StatefulInteractiveElement, Styled, Window, WindowBackgroundAppearance, WindowOptions, div,
    prelude::*, px, rgba,
};
use gpui_component::{TitleBar, theme::{Theme as GpuiTheme, ThemeMode as GpuiThemeMode}};
use gpui_term::{Clear, Copy, Paste, SelectAll};

use constants::{ENABLE_BLUR, rgba_u32, SURFACE_SIDEBAR, SIDEBAR_GLASS_BASE_ALPHA};
use menus::{app_menus, configure_macos_titlebar};

/// Main entry point for the application.
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
            // TitleBar for window controls and dragging (Windows/Linux)
            .child(
                TitleBar::new()
                    .bg(gpui::transparent_black())
                    .border_b_0()
            )
            // Main content (opacity controlled via surface background alpha)
            .child(
                div()
                    .size_full()
                    .absolute()
                    .top_0()
                    .left_0()
                    .child(self.render_terminal_container(cx))
                    .when(self.sidebar_visible, |el| {
                        el.child(self.render_sidebar_shell(cx))
                    })
            )
            // Dialog and sheet layers at full opacity
            .children(gpui_component::Root::render_dialog_layer(window, cx))
            .children(gpui_component::Root::render_sheet_layer(window, cx))
    }
}
