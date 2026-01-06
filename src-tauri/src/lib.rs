use tauri::{Manager, RunEvent};

#[cfg(target_os = "macos")]
use window_vibrancy::{apply_vibrancy, NSVisualEffectMaterial};

#[cfg(target_os = "windows")]
use window_vibrancy::apply_blur;

mod diagnostics;
mod mcp;
mod search;
mod session;

#[tauri::command(rename_all = "camelCase")]
fn get_home_dir() -> Option<String> {
    let home = dirs::home_dir().map(|p| p.to_string_lossy().to_string());
    home
}

fn detect_default_shell() -> String {
    #[cfg(not(target_os = "windows"))]
    {
        use users::os::unix::UserExt;

        // Priority 1: SHELL env var (fastest, most common)
        if let Ok(shell) = std::env::var("SHELL") {
            if !shell.trim().is_empty() {
                return shell;
            }
        }

        // Priority 2: O(1) getpwuid() via users crate
        if let Some(user) = users::get_user_by_uid(users::get_current_uid()) {
            if let Some(shell_path) = user.shell().to_str() {
                if !shell_path.is_empty() {
                    return shell_path.to_string();
                }
            }
        }

        "/bin/bash".to_string()
    }

    #[cfg(target_os = "windows")]
    {
        std::env::var("COMSPEC").unwrap_or_else(|_| "cmd.exe".to_string())
    }
}

#[tauri::command(rename_all = "camelCase")]
fn get_default_shell() -> String {
    detect_default_shell()
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let session_manager = session::build_session_manager()
        .expect("failed to build session manager");

    let mcp_manager = mcp::build_mcp_manager()
        .expect("failed to build mcp manager");

    let search_manager = search::build_search_manager()
        .expect("failed to build search manager");

    let app = tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .manage(session_manager)
        .manage(mcp_manager)
        .manage(search_manager)
        .invoke_handler(tauri::generate_handler![
            get_home_dir,
            get_default_shell,
            session::list_sessions,
            session::list_sections,
            session::get_session,
            session::create_session,
            session::rename_session,
            session::set_session_command,
            session::set_session_icon,
            session::delete_session,
            session::move_session,
            session::set_active_session,
            session::create_section,
            session::rename_section,
            session::set_section_path,
            session::set_section_icon,
            session::delete_section,
            session::start_session,
            session::stop_session,
            session::restart_session,
            session::write_session_input,
            session::resize_session,
            session::acknowledge_session,
            session::set_tool_session_id,
            mcp::mcp_list,
            mcp::mcp_get_settings,
            mcp::mcp_set_settings,
            mcp::mcp_attached,
            mcp::mcp_attach,
            mcp::mcp_detach,
            search::search_index_status,
            search::search_reindex,
            search::search_query,
        ])
        .setup(|app| {
            let window = app.get_webview_window("main").expect("failed to get main window");

            #[cfg(target_os = "macos")]
            apply_vibrancy(&window, NSVisualEffectMaterial::HudWindow, None, None)
                .expect("Failed to apply vibrancy");

            #[cfg(target_os = "windows")]
            apply_blur(&window, Some((18, 18, 18, 125)))
                .expect("Failed to apply blur");

            if cfg!(unix) {
                if let Ok(config) = app.state::<mcp::McpManager>().load_config() {
                    if config.mcp_pool.enabled {
                        if let Err(err) = mcp::pool_manager::initialize_global_pool(&config) {
                            let msg = err.to_string().replace('.', "");
                            diagnostics::log(format!("pool_init_failed error={}", msg));
                        }
                    }
                }
            }

            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("error while building tauri application");

    app.run(|app_handle, event| {
        if matches!(event, RunEvent::ExitRequested { .. } | RunEvent::Exit) {
            if let Ok(config) = app_handle.state::<mcp::McpManager>().load_config() {
                if config.mcp_pool.shutdown_on_exit {
                    let _ = mcp::pool_manager::shutdown_global_pool();
                }
            }
        }
    });
}
