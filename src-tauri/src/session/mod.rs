// ABOUTME: Coordinates session metadata, runtime management, and Tauri commands for terminals.
// ABOUTME: Starts, stops, and persists sessions while emitting events to the frontend.

use std::collections::{HashMap, HashSet};
use std::io::Read;
use std::sync::mpsc;
use std::thread;

use parking_lot::Mutex;
use portable_pty::{CommandBuilder, NativePtySystem, PtySize, PtySystem};
use serde::Serialize;
use tauri::{AppHandle, Emitter, Manager, State};
use uuid::Uuid;

use crate::diagnostics;
use crate::mcp::{McpManager, McpScope};

mod error;
mod model;
mod runtime;
mod status;
mod storage;
mod tools;

pub use model::{NewSessionInput, SectionRecord, SessionRecord, SessionStatus};
use runtime::SessionRuntime;
use status::{extract_session_id, prompt_detector, status_tracker, ExtractedSessionId};
use storage::{default_storage_root, DebouncedStorage, Storage, StorageSnapshot};
use tools::build_command;

/// Validate a path is safe (no traversal, exists)
fn validate_path(path: &str) -> Result<std::path::PathBuf, String> {
    if path.contains("..") {
        return Err("Path traversal not allowed".to_string());
    }
    if path.is_empty() {
        return Ok(std::path::PathBuf::new());
    }
    std::fs::canonicalize(path)
        .map_err(|e| format!("Invalid path '{}': {}", path, e))
}

fn redact_path(path: &str) -> String {
    if path.is_empty() {
        return String::new();
    }
    std::path::Path::new(path)
        .file_name()
        .map(|n| format!(".../{}", n.to_string_lossy()))
        .unwrap_or_else(|| "[redacted]".to_string())
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct SessionOutput {
    session_id: String,
    data: Vec<u8>,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct SessionExit {
    session_id: String,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct SessionStatusEvent {
    session_id: String,
    status: SessionStatus,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ToolSessionIdEvent {
    session_id: String,
    tool_session_id: String,
    tool: String,
}

/// Coordinates session metadata and runtime state.
///
/// Example:
/// ```rust,ignore
/// let manager = build_session_manager()?;
/// let sessions = manager.list_sessions();
/// ```
pub struct SessionManager {
    storage: DebouncedStorage,
    snapshot: Mutex<StorageSnapshot>,
    runtimes: Mutex<HashMap<String, SessionRuntime>>,
}

pub fn build_session_manager() -> Result<SessionManager, String> {
    let storage = Storage::new(default_storage_root(), "default".to_string());
    let snapshot = storage.load()?;
    let debounced = DebouncedStorage::new(storage, 500); // 500ms debounce

    Ok(SessionManager {
        storage: debounced,
        snapshot: Mutex::new(snapshot),
        runtimes: Mutex::new(HashMap::new()),
    })
}

impl SessionManager {
    pub fn list_sessions(&self) -> Vec<SessionRecord> {
        let snapshot = self.snapshot.lock();
        snapshot.sessions.clone()
    }

    pub fn list_sections(&self) -> Vec<SectionRecord> {
        let snapshot = self.snapshot.lock();
        snapshot.sections.clone()
    }

    pub fn get_session(&self, id: &str) -> Result<SessionRecord, String> {
        let snapshot = self.snapshot.lock();
        snapshot
            .sessions
            .iter()
            .find(|session| session.id == id)
            .cloned()
            .ok_or_else(|| "Session not found".to_string())
    }

    pub fn create_session(&self, input: NewSessionInput) -> Result<SessionRecord, String> {
        validate_path(&input.project_path)?;
        let id = Uuid::new_v4().to_string();
        let record = SessionRecord {
            id: id.clone(),
            title: input.title,
            project_path: input.project_path,
            section_id: input.section_id,
            tool: input.tool,
            command: input.command,
            args: input.args.unwrap_or_default(),
            icon: input.icon,
            status: SessionStatus::Idle,
            created_at: chrono_now(),
            last_accessed_at: None,
            claude_session_id: None,
            gemini_session_id: None,
            loaded_mcp_names: Vec::new(),
            is_open: true,
            tab_order: Some(self.next_tab_order()),
            is_custom_title: false,
            dynamic_title: None,
        };
        diagnostics::log(format!(
            "create_session id={} title={} tool={:?} command={} project_path={} section_id={}",
            record.id,
            record.title,
            record.tool,
            record.command,
            record.project_path,
            record.section_id
        ));
        let mut snapshot = self.snapshot.lock();
        snapshot.sessions.push(record.clone());
        self.storage.save(&snapshot).map_err(|e| e.to_string())?;
        Ok(record)
    }

    pub fn rename_session(&self, id: &str, title: String) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        let session = snapshot
            .sessions
            .iter_mut()
            .find(|session| session.id == id)
            .ok_or_else(|| "Session not found".to_string())?;
        session.title = title;
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    /// Set a custom (user-provided) title and lock it from dynamic updates
    pub fn set_session_custom_title(
        &self,
        id: &str,
        title: String,
        is_custom: bool,
    ) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        let session = snapshot
            .sessions
            .iter_mut()
            .find(|session| session.id == id)
            .ok_or_else(|| "Session not found".to_string())?;
        session.title = title;
        session.is_custom_title = is_custom;
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    /// Set a dynamic title from OSC escape sequences (only if not custom-locked)
    pub fn set_session_dynamic_title(&self, id: &str, title: String) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        let session = snapshot
            .sessions
            .iter_mut()
            .find(|session| session.id == id)
            .ok_or_else(|| "Session not found".to_string())?;
        session.dynamic_title = Some(title.clone());
        if !session.is_custom_title {
            session.title = title;
        }
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    pub fn set_session_command(&self, id: &str, command: String) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        let session = snapshot
            .sessions
            .iter_mut()
            .find(|session| session.id == id)
            .ok_or_else(|| "Session not found".to_string())?;
        session.command = command;
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    pub fn set_session_icon(&self, id: &str, icon: Option<String>) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        let session = snapshot
            .sessions
            .iter_mut()
            .find(|session| session.id == id)
            .ok_or_else(|| "Session not found".to_string())?;
        session.icon = icon;
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    pub fn delete_session(&self, id: &str) -> Result<(), String> {
        diagnostics::log(format!(
            "delete_session begin id={} os={}",
            id,
            std::env::consts::OS
        ));

        if let Some(mut runtime) = self.runtimes.lock().remove(id) {
            diagnostics::log(format!(
                "delete_session runtime_found id={} os={}",
                id,
                std::env::consts::OS
            ));
            runtime.shutdown();
        } else {
            diagnostics::log(format!(
                "delete_session runtime_missing id={} os={}",
                id,
                std::env::consts::OS
            ));
        }

        let mut snapshot = self.snapshot.lock();
        snapshot.sessions.retain(|session| session.id != id);
        let result = self.storage.save(&snapshot).map_err(|e| e.to_string());
        if let Err(ref err) = result {
            diagnostics::log(format!(
                "delete_session save_error id={} os={} err={}",
                id,
                std::env::consts::OS,
                err
            ));
        } else {
            diagnostics::log(format!(
                "delete_session complete id={} os={}",
                id,
                std::env::consts::OS
            ));
        }

        result
    }

    pub fn move_session(&self, id: &str, section_id: String) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        let session = snapshot
            .sessions
            .iter_mut()
            .find(|session| session.id == id)
            .ok_or_else(|| "Session not found".to_string())?;
        session.section_id = section_id;
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    pub fn set_active_session(&self, id: Option<String>) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        snapshot.active_session_id = id;
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    pub fn create_section(&self, name: String, path: String) -> Result<SectionRecord, String> {
        let section = SectionRecord {
            id: Uuid::new_v4().to_string(),
            name,
            path,
            icon: None,
            collapsed: false,
            order: self.next_section_order(),
        };
        let mut snapshot = self.snapshot.lock();
        snapshot.sections.push(section.clone());
        self.storage.save(&snapshot).map_err(|e| e.to_string())?;
        Ok(section)
    }

    pub fn rename_section(&self, id: &str, name: String) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        let section = snapshot
            .sections
            .iter_mut()
            .find(|section| section.id == id)
            .ok_or_else(|| "Section not found".to_string())?;
        section.name = name;
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    pub fn set_section_path(&self, id: &str, path: String) -> Result<(), String> {
        validate_path(&path)?;
        let mut snapshot = self.snapshot.lock();
        let section = snapshot
            .sections
            .iter_mut()
            .find(|section| section.id == id)
            .ok_or_else(|| "Section not found".to_string())?;
        section.path = path;
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    pub fn set_section_icon(&self, id: &str, icon: Option<String>) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        let section = snapshot
            .sections
            .iter_mut()
            .find(|section| section.id == id)
            .ok_or_else(|| "Section not found".to_string())?;
        section.icon = icon;
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    pub fn delete_section(&self, id: &str) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        snapshot.sections.retain(|section| section.id != id);
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    pub fn start_session(
        &self,
        app: &AppHandle,
        id: &str,
        rows: Option<u16>,
        cols: Option<u16>,
    ) -> Result<(), String> {
        let record = self.get_session(id)?;
        diagnostics::log(format!(
            "start_session id={} tool={:?} cmd={} rows={:?} cols={:?} project_path={}",
            id, record.tool, record.command, rows, cols, redact_path(&record.project_path)
        ));

        {
            let runtimes = self.runtimes.lock();
            if runtimes.contains_key(id) {
                // Session already running - this is OK, just return success
                // This makes start_session idempotent
                diagnostics::log(format!(
                    "start_session id={} already running (idempotent)",
                    id
                ));
                return Ok(());
            }
        }

        let cmd_spec = build_command(&record)?;
        diagnostics::log(format!(
            "start_session id={} command_spec program={} args={:?} env_keys={:?}",
            id,
            cmd_spec.program,
            cmd_spec.args,
            cmd_spec
                .env
                .iter()
                .map(|(key, _)| key.as_str())
                .collect::<Vec<_>>()
        ));
        if let Some((_, value)) = cmd_spec
            .env
            .iter()
            .find(|(key, _)| key == "CLAUDE_CONFIG_DIR")
        {
            diagnostics::log(format!(
                "start_session id={} claude_config_dir={}",
                id, value
            ));
        }
        let pty_system = NativePtySystem::default();
        let size = PtySize {
            rows: rows.unwrap_or(24),
            cols: cols.unwrap_or(80),
            pixel_width: 0,
            pixel_height: 0,
        };

        let pair = pty_system
            .openpty(size)
            .map_err(|e| {
                let msg = format!("failed to open pty: {}", e);
                diagnostics::log(format!("start_session id={} {}", id, msg));
                msg
            })?;

        let working_dir = if record.project_path.is_empty() {
            dirs::home_dir().unwrap_or_else(|| std::path::PathBuf::from("/"))
        } else {
            std::path::PathBuf::from(&record.project_path)
        };
        diagnostics::log(format!(
            "start_session id={} working_dir={}",
            id,
            working_dir.display()
        ));

        let mut cmd = CommandBuilder::new(&cmd_spec.program);
        cmd.args(&cmd_spec.args);
        cmd.cwd(&working_dir);
        for (key, value) in &cmd_spec.env {
            cmd.env(key, value);
        }
        if cfg!(not(target_os = "windows")) {
            cmd.env("TERM", "xterm-256color");
            cmd.env("COLORTERM", "truecolor");
        }

        let child = pair
            .slave
            .spawn_command(cmd)
            .map_err(|e| {
                let msg = format!("failed to spawn: {}", e);
                diagnostics::log(format!("start_session id={} {}", id, msg));
                msg
            })?;

        let mut reader = pair
            .master
            .try_clone_reader()
            .map_err(|e| format!("failed to clone reader: {}", e))?;
        let writer = pair
            .master
            .take_writer()
            .map_err(|e| format!("failed to get writer: {}", e))?;

        let session_id = id.to_string();
        let app_clone = app.clone();
        let tool = record.tool.clone();
        let tool_for_extract = record.tool.clone();
        let (shutdown_tx, shutdown_rx) = mpsc::channel();

        let scrollback = std::sync::Arc::new(parking_lot::Mutex::new(
            runtime::ScrollbackBuffer::new(10 * 1024 * 1024),
        ));
        let scrollback_for_reader = scrollback.clone();

        let subscribers = std::sync::Arc::new(parking_lot::Mutex::new(HashSet::<String>::new()));
        let subscribers_for_reader = subscribers.clone();

        let reader_thread = thread::spawn(move || {
            diagnostics::log(format!(
                "session_reader_started id={} tool={:?}",
                session_id, tool
            ));
            let mut buf = vec![0u8; 32768];
            let mut pending: Vec<u8> = Vec::with_capacity(65536);
            let emit_threshold: usize = 8192;
            let detector = prompt_detector(tool);
            let mut tracker = status_tracker();
            let mut last_status = SessionStatus::Running;
            let mut status_buffer = String::with_capacity(8192);
            let status_buffer_max = 4096;
            let mut tool_session_id_extracted = false;
            let mut output_events: u64 = 0;

            let emit_output = |data: &[u8],
                               app: &AppHandle,
                               sid: &str,
                               subs: &parking_lot::Mutex<HashSet<String>>| {
                let payload = SessionOutput {
                    session_id: sid.to_string(),
                    data: data.to_vec(),
                };
                let subscriber_labels: Vec<String> = subs.lock().iter().cloned().collect();
                // If no subscribers, emit globally for backward compatibility
                if subscriber_labels.is_empty() {
                    let _ = app.emit("session-output", payload);
                } else {
                    for window_label in subscriber_labels {
                        if let Some(window) = app.get_webview_window(&window_label) {
                            let _ = window.emit("session-output", payload.clone());
                        }
                    }
                }
            };

            let check_status = |buffer: &str,
                                detector: &status::PromptDetector,
                                tracker: &mut status::StatusTracker,
                                last: &mut SessionStatus,
                                app: &AppHandle,
                                sid: &str| {
                let has_prompt = detector.has_prompt(buffer);
                let new_status = tracker.update(buffer, has_prompt);
                if new_status != *last {
                    *last = new_status;
                    let _ = app.emit(
                        "session-status",
                        SessionStatusEvent {
                            session_id: sid.to_string(),
                            status: new_status,
                        },
                    );
                }
            };

            loop {
                if shutdown_rx.try_recv().is_ok() {
                    break;
                }
                match reader.read(&mut buf) {
                    Ok(0) => {
                        if !pending.is_empty() {
                            emit_output(&pending, &app_clone, &session_id, &subscribers_for_reader);
                            pending.clear();
                        }
                        diagnostics::log(format!(
                            "session_reader_eof id={} output_events={}",
                            session_id, output_events
                        ));
                        let _ = app_clone.emit(
                            "session-exit",
                            SessionExit {
                                session_id: session_id.clone(),
                            },
                        );
                        break;
                    }
                    Ok(n) => {
                        scrollback_for_reader.lock().append(&buf[..n]);
                        pending.extend_from_slice(&buf[..n]);
                        output_events += 1;
                        if output_events <= 3 {
                            diagnostics::log(format!(
                                "session_reader_output id={} bytes={} event={}",
                                session_id, n, output_events
                            ));
                        }
                        let text = String::from_utf8_lossy(&buf[..n]);
                        status_buffer.push_str(&text);
                        if status_buffer.len() > status_buffer_max {
                            let mut drain_to = status_buffer.len() - status_buffer_max;
                            while drain_to < status_buffer.len()
                                && !status_buffer.is_char_boundary(drain_to)
                            {
                                drain_to += 1;
                            }
                            if drain_to > 0 {
                                status_buffer.drain(..drain_to);
                            }
                        }

                        // Try to extract tool session ID once during startup
                        if !tool_session_id_extracted {
                            if let Some(extracted) =
                                extract_session_id(&tool_for_extract, &status_buffer)
                            {
                                tool_session_id_extracted = true;
                                let (tool_id, tool_name) = match extracted {
                                    ExtractedSessionId::Claude(id) => (id, "claude"),
                                    ExtractedSessionId::Gemini(id) => (id, "gemini"),
                                };
                                let _ = app_clone.emit(
                                    "tool-session-id",
                                    ToolSessionIdEvent {
                                        session_id: session_id.clone(),
                                        tool_session_id: tool_id,
                                        tool: tool_name.to_string(),
                                    },
                                );
                            }
                        }
                        let should_emit = pending.len() >= emit_threshold || n < buf.len();
                        if should_emit {
                            emit_output(&pending, &app_clone, &session_id, &subscribers_for_reader);
                            pending.clear();
                        }
                        check_status(
                            &status_buffer,
                            &detector,
                            &mut tracker,
                            &mut last_status,
                            &app_clone,
                            &session_id,
                        );
                    }
                    Err(_) => {
                        if !pending.is_empty() {
                            emit_output(&pending, &app_clone, &session_id, &subscribers_for_reader);
                            pending.clear();
                        }
                        diagnostics::log(format!(
                            "session_reader_error id={} output_events={}",
                            session_id, output_events
                        ));
                        break;
                    }
                }
            }
        });

        let runtime = SessionRuntime::new(
            pair.master,
            writer,
            child,
            reader_thread,
            shutdown_tx,
            id.to_string(),
            scrollback,
            subscribers,
        );

        self.runtimes.lock().insert(id.to_string(), runtime);
        self.update_session_status(id, SessionStatus::Running)?;
        Ok(())
    }

    pub fn stop_session(&self, id: &str) -> Result<(), String> {
        diagnostics::log(format!(
            "stop_session begin id={} os={}",
            id,
            std::env::consts::OS
        ));

        let runtime = {
            let mut runtimes = self.runtimes.lock();
            runtimes.remove(id)
        };

        if let Some(mut runtime) = runtime {
            diagnostics::log(format!(
                "stop_session runtime_found id={} os={}",
                id,
                std::env::consts::OS
            ));
            runtime.shutdown();
        } else {
            diagnostics::log(format!(
                "stop_session runtime_missing id={} os={}",
                id,
                std::env::consts::OS
            ));
        }

        let status_result = self.update_session_status(id, SessionStatus::Idle);
        if let Err(ref err) = status_result {
            diagnostics::log(format!(
                "stop_session update_status_error id={} os={} err={}",
                id,
                std::env::consts::OS,
                err
            ));
        }

        status_result
    }

    pub fn restart_session(
        &self,
        app: &AppHandle,
        id: &str,
        rows: Option<u16>,
        cols: Option<u16>,
    ) -> Result<(), String> {
        let _ = self.stop_session(id);
        self.start_session(app, id, rows, cols)
    }

    pub fn restart_session_with_mcp(
        &self,
        app: &AppHandle,
        id: &str,
        rows: Option<u16>,
        cols: Option<u16>,
        mcp_manager: &McpManager,
    ) -> Result<(), String> {
        if let Ok(record) = self.get_session(id) {
            if matches!(record.tool, model::SessionTool::Claude) {
                // Use block_on to call async MCP methods from sync context
                let _ = tauri::async_runtime::block_on(
                    self.regenerate_local_mcp_config(mcp_manager, &record)
                );
            }
        }
        self.restart_session(app, id, rows, cols)
    }

    async fn regenerate_local_mcp_config(
        &self,
        mcp_manager: &McpManager,
        record: &SessionRecord,
    ) -> Result<(), String> {
        diagnostics::log(format!(
            "mcp_regenerate_requested session_id={} project_path={}",
            record.id, record.project_path
        ));
        let project_path = record.project_path.trim();
        if project_path.is_empty() {
            return Ok(());
        }

        let attached = mcp_manager
            .get_attached_mcps(McpScope::Local, Some(project_path))
            .await
            .map_err(|e| e.to_string())?;

        if attached.is_empty() {
            return Ok(());
        }

        let available = mcp_manager
            .get_available_mcps()
            .await
            .map_err(|e| e.to_string())?;

        let filtered: Vec<String> = attached
            .into_iter()
            .filter(|name| available.contains_key(name))
            .collect();

        mcp_manager
            .set_mcps(McpScope::Local, Some(project_path), &filtered)
            .await
            .map_err(|e| e.to_string())?;
        diagnostics::log(format!(
            "mcp_regenerate_completed session_id={} count={}",
            record.id,
            filtered.len()
        ));
        Ok(())
    }

    pub fn write_session_input(&self, id: &str, data: &[u8]) -> Result<(), String> {
        if data.len() > 4096 {
            diagnostics::log(format!(
                "write_session_input large payload id={} bytes={} (consider base64)",
                id,
                data.len()
            ));
        }
        let mut runtimes = self.runtimes.lock();
        let runtime = runtimes
            .get_mut(id)
            .ok_or_else(|| "session not running".to_string())?;
        runtime.write(data)
    }

    pub fn resize_session(&self, id: &str, rows: u16, cols: u16) -> Result<(), String> {
        let mut runtimes = self.runtimes.lock();
        let runtime = runtimes
            .get_mut(id)
            .ok_or_else(|| "session not running".to_string())?;
        runtime.resize(rows, cols)
    }

    pub fn acknowledge_session(&self, _id: &str) -> Result<(), String> {
        Ok(())
    }

    pub fn set_tool_session_id(
        &self,
        id: &str,
        tool: &str,
        tool_session_id: String,
    ) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        if let Some(session) = snapshot.sessions.iter_mut().find(|s| s.id == id) {
            match tool {
                "claude" => session.claude_session_id = Some(tool_session_id),
                "gemini" => session.gemini_session_id = Some(tool_session_id),
                _ => return Err(format!("unknown tool: {}", tool)),
            }
        }
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    fn update_session_status(&self, id: &str, status: SessionStatus) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        if let Some(session) = snapshot.sessions.iter_mut().find(|s| s.id == id) {
            session.status = status;
            session.last_accessed_at = Some(chrono_now());
        }
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    fn next_tab_order(&self) -> u32 {
        let snapshot = self.snapshot.lock();
        snapshot
            .sessions
            .iter()
            .filter_map(|session| session.tab_order)
            .max()
            .unwrap_or(0)
            .saturating_add(1)
    }

    fn next_section_order(&self) -> u32 {
        let snapshot = self.snapshot.lock();
        snapshot
            .sections
            .iter()
            .map(|section| section.order)
            .max()
            .unwrap_or(0)
            .saturating_add(1)
    }

    fn is_ai_tool(tool: &model::SessionTool) -> bool {
        matches!(
            tool,
            model::SessionTool::Claude
                | model::SessionTool::Gemini
                | model::SessionTool::Codex
                | model::SessionTool::OpenCode
        )
    }

    fn get_running_session_ids(&self) -> Vec<String> {
        let runtimes = self.runtimes.lock();
        runtimes.keys().cloned().collect()
    }

    pub fn find_running_ai_sessions(&self, project_path: Option<&str>) -> Vec<String> {
        let snapshot = self.snapshot.lock();
        let running_ids = self.get_running_session_ids();

        snapshot
            .sessions
            .iter()
            .filter(|session| {
                running_ids.contains(&session.id)
                    && Self::is_ai_tool(&session.tool)
                    && project_path.map_or(true, |path| session.project_path == path)
            })
            .map(|session| session.id.clone())
            .collect()
    }
}

#[tauri::command(rename_all = "camelCase")]
pub fn list_sessions(state: State<'_, SessionManager>) -> Result<Vec<SessionRecord>, String> {
    Ok(state.list_sessions())
}

#[tauri::command(rename_all = "camelCase")]
pub fn list_sections(state: State<'_, SessionManager>) -> Result<Vec<SectionRecord>, String> {
    Ok(state.list_sections())
}

#[tauri::command(rename_all = "camelCase")]
pub fn get_session(state: State<'_, SessionManager>, id: String) -> Result<SessionRecord, String> {
    state.get_session(&id)
}

#[tauri::command(rename_all = "camelCase")]
pub fn create_session(
    state: State<'_, SessionManager>,
    input: NewSessionInput,
) -> Result<SessionRecord, String> {
    state.create_session(input)
}

#[tauri::command(rename_all = "camelCase")]
pub fn rename_session(
    state: State<'_, SessionManager>,
    id: String,
    title: String,
) -> Result<(), String> {
    state.rename_session(&id, title)
}

#[tauri::command(rename_all = "camelCase")]
pub fn set_session_custom_title(
    state: State<'_, SessionManager>,
    id: String,
    title: String,
    is_custom: bool,
) -> Result<(), String> {
    state.set_session_custom_title(&id, title, is_custom)
}

#[tauri::command(rename_all = "camelCase")]
pub fn set_session_dynamic_title(
    state: State<'_, SessionManager>,
    id: String,
    title: String,
) -> Result<(), String> {
    state.set_session_dynamic_title(&id, title)
}

#[tauri::command(rename_all = "camelCase")]
pub fn set_session_command(
    state: State<'_, SessionManager>,
    id: String,
    command: String,
) -> Result<(), String> {
    state.set_session_command(&id, command)
}

#[tauri::command(rename_all = "camelCase")]
pub fn set_session_icon(
    state: State<'_, SessionManager>,
    id: String,
    icon: Option<String>,
) -> Result<(), String> {
    state.set_session_icon(&id, icon)
}

#[tauri::command(rename_all = "camelCase")]
pub fn delete_session(state: State<'_, SessionManager>, id: String) -> Result<(), String> {
    state.delete_session(&id)
}

#[tauri::command(rename_all = "camelCase")]
pub fn move_session(
    state: State<'_, SessionManager>,
    id: String,
    section_id: String,
) -> Result<(), String> {
    state.move_session(&id, section_id)
}

#[tauri::command(rename_all = "camelCase")]
pub fn set_active_session(
    state: State<'_, SessionManager>,
    id: Option<String>,
) -> Result<(), String> {
    state.set_active_session(id)
}

#[tauri::command(rename_all = "camelCase")]
pub fn create_section(
    state: State<'_, SessionManager>,
    name: String,
    path: String,
) -> Result<SectionRecord, String> {
    state.create_section(name, path)
}

#[tauri::command(rename_all = "camelCase")]
pub fn rename_section(
    state: State<'_, SessionManager>,
    id: String,
    name: String,
) -> Result<(), String> {
    state.rename_section(&id, name)
}

#[tauri::command(rename_all = "camelCase")]
pub fn set_section_path(
    state: State<'_, SessionManager>,
    id: String,
    path: String,
) -> Result<(), String> {
    state.set_section_path(&id, path)
}

#[tauri::command(rename_all = "camelCase")]
pub fn set_section_icon(
    state: State<'_, SessionManager>,
    id: String,
    icon: Option<String>,
) -> Result<(), String> {
    state.set_section_icon(&id, icon)
}

#[tauri::command(rename_all = "camelCase")]
pub fn delete_section(state: State<'_, SessionManager>, id: String) -> Result<(), String> {
    state.delete_section(&id)
}

#[tauri::command(rename_all = "camelCase")]
pub fn start_session(
    app: AppHandle,
    state: State<'_, SessionManager>,
    id: String,
    rows: Option<u16>,
    cols: Option<u16>,
) -> Result<(), String> {
    state.start_session(&app, &id, rows, cols)
}

#[tauri::command(rename_all = "camelCase")]
pub fn stop_session(state: State<'_, SessionManager>, id: String) -> Result<(), String> {
    state.stop_session(&id)
}

#[tauri::command(rename_all = "camelCase")]
pub fn restart_session(
    app: AppHandle,
    state: State<'_, SessionManager>,
    mcp_state: State<'_, crate::mcp::McpManager>,
    id: String,
    rows: Option<u16>,
    cols: Option<u16>,
) -> Result<(), String> {
    state.restart_session_with_mcp(&app, &id, rows, cols, &mcp_state)
}

#[tauri::command(rename_all = "camelCase")]
pub fn write_session_input(
    state: State<'_, SessionManager>,
    id: String,
    data: String,
) -> Result<(), String> {
    state.write_session_input(&id, data.as_bytes())
}

#[tauri::command(rename_all = "camelCase")]
pub fn resize_session(
    state: State<'_, SessionManager>,
    id: String,
    rows: u16,
    cols: u16,
) -> Result<(), String> {
    state.resize_session(&id, rows, cols)
}

#[tauri::command(rename_all = "camelCase")]
pub fn acknowledge_session(state: State<'_, SessionManager>, id: String) -> Result<(), String> {
    state.acknowledge_session(&id)
}

#[tauri::command(rename_all = "camelCase")]
pub fn set_tool_session_id(
    state: State<'_, SessionManager>,
    id: String,
    tool: String,
    tool_session_id: String,
) -> Result<(), String> {
    state.set_tool_session_id(&id, &tool, tool_session_id)
}

#[tauri::command(rename_all = "camelCase")]
pub fn get_scrollback(
    state: State<'_, SessionManager>,
    session_id: String,
) -> Result<Vec<u8>, String> {
    let runtimes = state.runtimes.lock();
    let runtime = runtimes
        .get(&session_id)
        .ok_or_else(|| "session not running".to_string())?;
    Ok(runtime.get_scrollback())
}

/// Subscribe a window to receive output events from a session.
/// This enables mirror mode where multiple windows can view the same session.
#[tauri::command(rename_all = "camelCase")]
pub fn subscribe_to_session(
    state: State<'_, SessionManager>,
    session_id: String,
    window_label: String,
) -> Result<(), String> {
    let runtimes = state.runtimes.lock();
    let runtime = runtimes
        .get(&session_id)
        .ok_or_else(|| "session not running".to_string())?;
    runtime.add_subscriber(window_label);
    Ok(())
}

/// Unsubscribe a window from receiving output events from a session.
#[tauri::command(rename_all = "camelCase")]
pub fn unsubscribe_from_session(
    state: State<'_, SessionManager>,
    session_id: String,
    window_label: String,
) -> Result<(), String> {
    let runtimes = state.runtimes.lock();
    if let Some(runtime) = runtimes.get(&session_id) {
        runtime.remove_subscriber(window_label);
    }
    Ok(())
}

/// Get the number of windows currently subscribed to a session.
/// Used by the UI to show mirror badges when count > 1.
#[tauri::command(rename_all = "camelCase")]
pub fn get_session_subscriber_count(
    state: State<'_, SessionManager>,
    session_id: String,
) -> Result<usize, String> {
    let runtimes = state.runtimes.lock();
    let runtime = runtimes
        .get(&session_id)
        .ok_or_else(|| "session not running".to_string())?;
    Ok(runtime.subscriber_count())
}

fn chrono_now() -> String {
    time::OffsetDateTime::now_utc()
        .format(&time::format_description::well_known::Rfc3339)
        .unwrap_or_else(|_| String::new())
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::TempDir;

    fn test_manager() -> (TempDir, SessionManager) {
        let temp = TempDir::new().unwrap();
        let storage = Storage::new(temp.path().to_path_buf(), "test".to_string());
        let snapshot = storage.load().unwrap();
        let debounced = DebouncedStorage::new(storage, 50); // 50ms debounce for tests
        let manager = SessionManager {
            storage: debounced,
            snapshot: Mutex::new(snapshot),
            runtimes: Mutex::new(HashMap::new()),
        };
        (temp, manager)
    }

    #[test]
    fn test_create_and_list_sessions() {
        let (_temp, manager) = test_manager();

        let input = NewSessionInput {
            title: "Test Session".to_string(),
            project_path: "/tmp".to_string(),
            section_id: "default".to_string(),
            tool: model::SessionTool::Shell,
            command: "/bin/bash".to_string(),
            args: None,
            icon: None,
        };

        let session = manager.create_session(input).unwrap();
        assert_eq!(session.title, "Test Session");
        assert_eq!(session.project_path, "/tmp");

        let sessions = manager.list_sessions();
        assert_eq!(sessions.len(), 1);
        assert_eq!(sessions[0].id, session.id);
    }

    #[test]
    fn test_rename_session() {
        let (_temp, manager) = test_manager();

        let input = NewSessionInput {
            title: "Original".to_string(),
            project_path: "".to_string(),
            section_id: "default".to_string(),
            tool: model::SessionTool::Shell,
            command: "/bin/bash".to_string(),
            args: None,
            icon: None,
        };

        let session = manager.create_session(input).unwrap();
        manager.rename_session(&session.id, "Renamed".to_string()).unwrap();

        let updated = manager.get_session(&session.id).unwrap();
        assert_eq!(updated.title, "Renamed");
    }

    #[test]
    fn test_delete_session() {
        let (_temp, manager) = test_manager();

        let input = NewSessionInput {
            title: "To Delete".to_string(),
            project_path: "".to_string(),
            section_id: "default".to_string(),
            tool: model::SessionTool::Shell,
            command: "/bin/bash".to_string(),
            args: None,
            icon: None,
        };

        let session = manager.create_session(input).unwrap();
        assert_eq!(manager.list_sessions().len(), 1);

        manager.delete_session(&session.id).unwrap();
        assert_eq!(manager.list_sessions().len(), 0);
    }

    #[test]
    fn test_create_and_list_sections() {
        let (_temp, manager) = test_manager();

        let section = manager.create_section("Project A".to_string(), "/home/user/project-a".to_string()).unwrap();
        assert_eq!(section.name, "Project A");
        assert_eq!(section.path, "/home/user/project-a");
        assert!(section.icon.is_none());

        let sections = manager.list_sections();
        assert_eq!(sections.len(), 1);
        assert_eq!(sections[0].id, section.id);
    }

    #[test]
    fn test_move_session_to_section() {
        let (_temp, manager) = test_manager();

        let section = manager.create_section("New Section".to_string(), "".to_string()).unwrap();

        let input = NewSessionInput {
            title: "Movable".to_string(),
            project_path: "".to_string(),
            section_id: "default".to_string(),
            tool: model::SessionTool::Shell,
            command: "/bin/bash".to_string(),
            args: None,
            icon: None,
        };

        let session = manager.create_session(input).unwrap();
        assert_eq!(session.section_id, "default");

        manager.move_session(&session.id, section.id.clone()).unwrap();

        let updated = manager.get_session(&session.id).unwrap();
        assert_eq!(updated.section_id, section.id);
    }

    #[test]
    fn test_set_tool_session_id() {
        let (_temp, manager) = test_manager();

        let input = NewSessionInput {
            title: "Claude Session".to_string(),
            project_path: "".to_string(),
            section_id: "default".to_string(),
            tool: model::SessionTool::Claude,
            command: "claude".to_string(),
            args: None,
            icon: None,
        };

        let session = manager.create_session(input).unwrap();
        assert!(session.claude_session_id.is_none());

        manager.set_tool_session_id(&session.id, "claude", "abc-123".to_string()).unwrap();

        let updated = manager.get_session(&session.id).unwrap();
        assert_eq!(updated.claude_session_id, Some("abc-123".to_string()));
    }

    #[test]
    fn test_session_tab_order() {
        let (_temp, manager) = test_manager();

        for i in 1..=3 {
            let input = NewSessionInput {
                title: format!("Session {}", i),
                project_path: "".to_string(),
                section_id: "default".to_string(),
                tool: model::SessionTool::Shell,
                command: "/bin/bash".to_string(),
                args: None,
                icon: None,
            };
            manager.create_session(input).unwrap();
        }

        let sessions = manager.list_sessions();
        assert_eq!(sessions.len(), 3);

        // Tab orders should be sequential
        let orders: Vec<u32> = sessions.iter().filter_map(|s| s.tab_order).collect();
        assert_eq!(orders, vec![1, 2, 3]);
    }

    #[test]
    fn test_get_running_session_ids_returns_only_running() {
        let (_temp, manager) = test_manager();

        let session1 = manager
            .create_session(NewSessionInput {
                title: "Session 1".to_string(),
                project_path: "/tmp".to_string(),
                section_id: "default".to_string(),
                tool: model::SessionTool::Claude,
                command: "claude".to_string(),
                args: None,
                icon: None,
            })
            .unwrap();

        let _session2 = manager
            .create_session(NewSessionInput {
                title: "Session 2".to_string(),
                project_path: "/tmp".to_string(),
                section_id: "default".to_string(),
                tool: model::SessionTool::Gemini,
                command: "gemini".to_string(),
                args: None,
                icon: None,
            })
            .unwrap();

        let results = manager.get_running_session_ids();
        assert_eq!(
            results.len(),
            0,
            "should return empty when no sessions are running"
        );
    }

    #[test]
    fn test_is_ai_tool_returns_true_for_ai_tools() {
        assert!(SessionManager::is_ai_tool(&model::SessionTool::Claude));
        assert!(SessionManager::is_ai_tool(&model::SessionTool::Gemini));
        assert!(SessionManager::is_ai_tool(&model::SessionTool::Codex));
        assert!(SessionManager::is_ai_tool(&model::SessionTool::OpenCode));
    }

    #[test]
    fn test_is_ai_tool_returns_false_for_non_ai_tools() {
        assert!(!SessionManager::is_ai_tool(&model::SessionTool::Shell));
        assert!(!SessionManager::is_ai_tool(
            &model::SessionTool::Custom("custom".to_string())
        ));
    }
}
