//! Application menu bar configuration.

use gpui::{Menu, MenuItem, Window};
use gpui_term::{Clear, Copy, Paste, SelectAll};

use super::actions::*;

/// Create the application menu bar.
/// Uses conditional compilation for platform-specific items.
pub fn app_menus() -> Vec<Menu> {
    vec![
        Menu {
            name: "Agent Term".into(),
            items: vec![
                MenuItem::action("About Agent Term", About),
                MenuItem::separator(),
                MenuItem::action("Check for Updates...", CheckForUpdates),
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
                MenuItem::action("New Window", NewWindow),
                MenuItem::action("Close Window", CloseWindow),
                MenuItem::action("Reopen Closed", ReopenClosed),
                MenuItem::separator(),
                MenuItem::action("Minimize", Minimize),
                MenuItem::action("Zoom", Zoom),
            ],
        },
    ]
}

#[cfg(target_os = "macos")]
#[allow(unexpected_cfgs)]
pub fn configure_macos_titlebar(window: &mut Window) {
    use objc::{msg_send, sel, sel_impl};
    use raw_window_handle::{HasWindowHandle, RawWindowHandle};

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

#[cfg(not(target_os = "macos"))]
pub fn configure_macos_titlebar(_window: &mut Window) {
    // No-op on non-macOS platforms
}
