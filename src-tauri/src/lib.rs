use base64::Engine;
use parking_lot::Mutex;
use portable_pty::{CommandBuilder, NativePtySystem, PtySize, PtySystem};
use serde::Serialize;
use std::collections::HashMap;
use std::io::{Read, Write};
use std::sync::Arc;
use std::thread;
use tauri::{Emitter, State};
use uuid::Uuid;

mod diagnostics;
mod session;

// Terminal session state
struct PtySession {
    master: Box<dyn portable_pty::MasterPty + Send>,
    writer: Box<dyn Write + Send>,
    child: Box<dyn portable_pty::Child + Send + Sync>,
    session_id: String,
}

struct AppState {
    sessions: Mutex<HashMap<String, PtySession>>,
}

#[derive(Clone, Serialize)]
struct TerminalOutput {
    terminal_id: String,
    session_id: String,
    data_base64: String,
}

#[derive(Clone, Serialize)]
struct TerminalExit {
    terminal_id: String,
    session_id: String,
}

#[tauri::command(rename_all = "camelCase")]
fn create_terminal(
    app: tauri::AppHandle,
    state: State<'_, Arc<AppState>>,
    terminal_id: String,
    session_id: String,
    cwd: Option<String>,
    rows: Option<u16>,
    cols: Option<u16>,
) -> Result<String, String> {
    let pty_system = NativePtySystem::default();

    // Get the working directory
    let working_dir = cwd
        .map(std::path::PathBuf::from)
        .unwrap_or_else(|| dirs::home_dir().unwrap_or_else(|| std::path::PathBuf::from("/")));
    let size = PtySize {
        rows: rows.unwrap_or(24),
        cols: cols.unwrap_or(80),
        pixel_width: 0,
        pixel_height: 0,
    };

    // Create the PTY
    let pair = pty_system
        .openpty(size)
        .map_err(|e| format!("Failed to open pty: {}", e))?;

    // Build the shell command
    let mut cmd = CommandBuilder::new(detect_default_shell());
    cmd.cwd(working_dir);
    if cfg!(not(target_os = "windows")) {
        cmd.arg("-i");
        cmd.env("TERM", "xterm-256color");
        cmd.env("COLORTERM", "truecolor");
    }
    // Spawn the child process
    let child = pair
        .slave
        .spawn_command(cmd)
        .map_err(|e| format!("Failed to spawn command: {}", e))?;

    // Get reader and writer
    let mut reader = pair
        .master
        .try_clone_reader()
        .map_err(|e| format!("Failed to clone reader: {}", e))?;
    let writer = pair
        .master
        .take_writer()
        .map_err(|e| format!("Failed to get writer: {}", e))?;

    let terminal_id_clone = terminal_id.clone();
    let session_id_clone = session_id.clone();
    let app_clone = app.clone();

    // Spawn a thread to read from the PTY and emit events
    thread::spawn(move || {
        let mut buf = vec![0u8; 16384];
        let mut pending: Vec<u8> = Vec::with_capacity(65536);
        let emit_threshold = 65536;
        loop {
            match reader.read(&mut buf) {
                Ok(0) => {
                    // EOF - terminal closed
                    if !pending.is_empty() {
                        let payload = TerminalOutput {
                            terminal_id: terminal_id_clone.clone(),
                            session_id: session_id_clone.clone(),
                            data_base64: base64::engine::general_purpose::STANDARD
                                .encode(&pending),
                        };
                        let _ = app_clone.emit("terminal-output", payload);
                        pending.clear();
                    }
                    let _ = app_clone.emit(
                        "terminal-exit",
                        TerminalExit {
                            terminal_id: terminal_id_clone.clone(),
                            session_id: session_id_clone.clone(),
                        },
                    );
                    break;
                }
                Ok(n) => {
                    pending.extend_from_slice(&buf[..n]);
                    if pending.len() >= emit_threshold || n < buf.len() {
                        let payload = TerminalOutput {
                            terminal_id: terminal_id_clone.clone(),
                            session_id: session_id_clone.clone(),
                            data_base64: base64::engine::general_purpose::STANDARD
                                .encode(&pending),
                        };
                        let _ = app_clone.emit("terminal-output", payload);
                        pending.clear();
                    }
                }
                Err(_) => {
                    if !pending.is_empty() {
                        let payload = TerminalOutput {
                            terminal_id: terminal_id_clone.clone(),
                            session_id: session_id_clone.clone(),
                            data_base64: base64::engine::general_purpose::STANDARD
                                .encode(&pending),
                        };
                        let _ = app_clone.emit("terminal-output", payload);
                        pending.clear();
                    }
                    break;
                }
            }
        }
    });

    // Store the session
    let session = PtySession {
        master: pair.master,
        writer,
        child,
        session_id: session_id.clone(),
    };
    state.sessions.lock().insert(terminal_id.clone(), session);

    Ok(terminal_id)
}

#[tauri::command(rename_all = "camelCase")]
fn write_terminal(
    state: State<'_, Arc<AppState>>,
    terminal_id: String,
    data: String,
) -> Result<(), String> {
    let mut sessions = state.sessions.lock();
    if let Some(session) = sessions.get_mut(&terminal_id) {
        session
            .writer
            .write_all(data.as_bytes())
            .map_err(|e| format!("Failed to write: {}", e))?;
        session
            .writer
            .flush()
            .map_err(|e| format!("Failed to flush: {}", e))?;
    } else {
        return Err("Terminal not found".to_string());
    }
    Ok(())
}

#[tauri::command(rename_all = "camelCase")]
fn resize_terminal(
    state: State<'_, Arc<AppState>>,
    terminal_id: String,
    rows: u16,
    cols: u16,
) -> Result<(), String> {
    let mut sessions = state.sessions.lock();
    if let Some(session) = sessions.get_mut(&terminal_id) {
        session
            .master
            .resize(PtySize {
                rows,
                cols,
                pixel_width: 0,
                pixel_height: 0,
            })
            .map_err(|e| format!("Failed to resize pty: {}", e))?;
        Ok(())
    } else {
        Err("Terminal not found".to_string())
    }
}

#[tauri::command(rename_all = "camelCase")]
fn close_terminal(state: State<'_, Arc<AppState>>, terminal_id: String) -> Result<(), String> {
    let mut sessions = state.sessions.lock();
    if let Some(mut session) = sessions.remove(&terminal_id) {
        if let Err(err) = session.child.kill() {
            let _ = err;
        }
        if let Err(err) = session.child.try_wait() {
            let _ = err;
        }
        Ok(())
    } else {
        Err("Terminal not found".to_string())
    }
}

#[tauri::command(rename_all = "camelCase")]
fn generate_id() -> String {
    let id = Uuid::new_v4().to_string();
    id
}

#[tauri::command(rename_all = "camelCase")]
fn get_home_dir() -> Option<String> {
    let home = dirs::home_dir().map(|p| p.to_string_lossy().to_string());
    home
}

fn detect_default_shell() -> String {
    #[cfg(not(target_os = "windows"))]
    {
        if let Ok(shell) = std::env::var("SHELL") {
            if !shell.trim().is_empty() {
                return shell;
            }
        }
        if let Ok(user) = std::env::var("USER") {
            if let Ok(passwd) = std::fs::read_to_string("/etc/passwd") {
                for line in passwd.lines() {
                    if line.starts_with(&format!("{}:", user)) {
                        let parts: Vec<&str> = line.split(':').collect();
                        if let Some(shell) = parts.get(6) {
                            if !shell.trim().is_empty() {
                                return shell.to_string();
                            }
                        }
                    }
                }
            }
        }
    }
    #[cfg(target_os = "windows")]
    {
        std::env::var("COMSPEC").unwrap_or_else(|_| "cmd.exe".to_string())
    }
    #[cfg(not(target_os = "windows"))]
    {
        "/bin/bash".to_string()
    }
}

#[tauri::command(rename_all = "camelCase")]
fn get_default_shell() -> String {
    detect_default_shell()
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let app_state = Arc::new(AppState {
        sessions: Mutex::new(HashMap::new()),
    });

    let session_manager = session::build_session_manager()
        .expect("failed to build session manager");

    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .manage(app_state)
        .manage(session_manager)
        .invoke_handler(tauri::generate_handler![
            create_terminal,
            write_terminal,
            resize_terminal,
            close_terminal,
            generate_id,
            get_home_dir,
            get_default_shell,
            session::list_sessions,
            session::list_sections,
            session::get_session,
            session::create_session,
            session::rename_session,
            session::delete_session,
            session::move_session,
            session::set_active_session,
            session::create_section,
            session::rename_section,
            session::delete_section,
            session::start_session,
            session::stop_session,
            session::restart_session,
            session::write_session_input,
            session::write_session_input_base64,
            session::resize_session,
            session::acknowledge_session,
            session::set_tool_session_id,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
