use std::fs;
use std::io::BufWriter;
use std::path::{Path, PathBuf};
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::Arc;
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use parking_lot::Mutex as PLMutex;
use serde::{Deserialize, Serialize};

pub const SCHEMA_VERSION: u32 = 1;

/// WindowRecord represents a single window's state and metadata.
///
/// Example:
/// ```rust,ignore
/// let record = WindowRecord {
///     id: "win-1".to_string(),
///     label: "main".to_string(),
///     title: "Agent Term".to_string(),
///     x: 100,
///     y: 100,
///     width: 1024,
///     height: 768,
///     is_maximized: false,
///     session_ids: vec!["session-1".to_string()],
///     active_session_id: Some("session-1".to_string()),
/// };
/// ```
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct WindowRecord {
    pub id: String,
    pub label: String,
    pub title: String,
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
    pub is_maximized: bool,
    pub session_ids: Vec<String>,
    pub active_session_id: Option<String>,
}

/// WindowSnapshot contains the complete state of all windows for persistence.
///
/// Example:
/// ```rust,ignore
/// let snapshot = WindowSnapshot {
///     schema_version: 1,
///     windows: vec![record],
///     main_window_id: "win-1".to_string(),
/// };
/// ```
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WindowSnapshot {
    #[serde(default = "default_schema_version")]
    pub schema_version: u32,
    pub windows: Vec<WindowRecord>,
    pub main_window_id: String,
}

fn default_schema_version() -> u32 {
    SCHEMA_VERSION
}

#[derive(Debug, Clone)]
pub struct WindowStorage {
    root: PathBuf,
    profile: String,
}

impl WindowStorage {
    pub fn new(root: PathBuf, profile: String) -> Self {
        Self { root, profile }
    }

    pub fn load(&self) -> Result<WindowSnapshot, String> {
        let path = self.file_path();
        if !path.exists() {
            return Ok(WindowSnapshot {
                schema_version: SCHEMA_VERSION,
                windows: Vec::new(),
                main_window_id: String::new(),
            });
        }
        let data = fs::read_to_string(&path)
            .map_err(|e| format!("failed to read window storage: {}", e))?;
        let mut snapshot = match serde_json::from_str::<WindowSnapshot>(&data) {
            Ok(s) => s,
            Err(parse_err) => {
                if let Some(backup) = self.load_from_backup() {
                    return self.migrate(backup);
                }
                return Err(format!("failed to parse window storage: {}", parse_err));
            }
        };
        snapshot = self.migrate(snapshot)?;
        Ok(snapshot)
    }

    fn load_from_backup(&self) -> Option<WindowSnapshot> {
        let backup_path = self.file_path().with_extension("json.bak");
        if !backup_path.exists() {
            return None;
        }
        let data = fs::read_to_string(&backup_path).ok()?;
        serde_json::from_str::<WindowSnapshot>(&data).ok()
    }

    fn migrate(&self, mut snapshot: WindowSnapshot) -> Result<WindowSnapshot, String> {
        if snapshot.schema_version < SCHEMA_VERSION {
            snapshot.schema_version = SCHEMA_VERSION;
        }
        Ok(snapshot)
    }

    pub fn save(&self, snapshot: &WindowSnapshot) -> Result<(), String> {
        let path = self.file_path();
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)
                .map_err(|e| format!("failed to create window storage directory: {}", e))?;
        }
        self.rotate_backups(&path);
        let tmp_path = path.with_extension("json.tmp");
        let file = fs::File::create(&tmp_path)
            .map_err(|e| format!("failed to create temp file: {}", e))?;
        let mut writer = BufWriter::new(file);
        serde_json::to_writer_pretty(&mut writer, snapshot)
            .map_err(|e| format!("failed to serialize window snapshot: {}", e))?;
        use std::io::Write;
        writer
            .flush()
            .map_err(|e| format!("failed to flush window storage: {}", e))?;
        fs::rename(&tmp_path, &path)
            .map_err(|e| format!("failed to write window storage: {}", e))?;
        Ok(())
    }

    fn rotate_backups(&self, path: &Path) {
        use crate::diagnostics;

        if !path.exists() {
            return;
        }

        let bak2 = path.with_extension("json.bak.2");
        let bak1 = path.with_extension("json.bak.1");
        let bak = path.with_extension("json.bak");

        if let Err(e) = fs::remove_file(&bak2) {
            if e.kind() != std::io::ErrorKind::NotFound {
                diagnostics::log(format!("window_backup_warning: remove bak2 failed: {}", e));
            }
        }

        if bak1.exists() {
            if let Err(e) = fs::rename(&bak1, &bak2) {
                diagnostics::log(format!("window_backup_warning: rotate bak1->bak2 failed: {}", e));
            }
        }

        if bak.exists() {
            if let Err(e) = fs::rename(&bak, &bak1) {
                diagnostics::log(format!("window_backup_warning: rotate bak->bak1 failed: {}", e));
            }
        }

        if let Err(e) = fs::rename(path, &bak) {
            diagnostics::log(format!("window_backup_warning: create backup failed: {}", e));
        }
    }

    fn file_path(&self) -> PathBuf {
        let profile_dir = self.root.join("profiles").join(&self.profile);
        profile_dir.join("windows.json")
    }
}

pub fn default_storage_root() -> PathBuf {
    dirs::home_dir()
        .unwrap_or_else(|| Path::new("/").to_path_buf())
        .join(".agent-term")
}

enum SaveMessage {
    Save,
    Shutdown,
}

/// Debounced storage wrapper that coalesces rapid saves.
///
/// Example:
/// ```rust,ignore
/// let storage = WindowStorage::new(root, "default".to_string());
/// let debounced = DebouncedWindowStorage::new(storage, 500);
/// debounced.save(&snapshot)?;
/// ```
pub struct DebouncedWindowStorage {
    storage: WindowStorage,
    sender: Sender<SaveMessage>,
    pending: Arc<PLMutex<Option<WindowSnapshot>>>,
    worker: Option<JoinHandle<()>>,
}

impl DebouncedWindowStorage {
    pub fn new(storage: WindowStorage, debounce_ms: u64) -> Self {
        let (sender, receiver) = mpsc::channel();
        let pending: Arc<PLMutex<Option<WindowSnapshot>>> = Arc::new(PLMutex::new(None));
        let pending_clone = pending.clone();
        let storage_clone = storage.clone();
        let debounce = Duration::from_millis(debounce_ms);

        let worker = thread::spawn(move || {
            Self::worker_loop(receiver, storage_clone, pending_clone, debounce);
        });

        Self {
            storage,
            sender,
            pending,
            worker: Some(worker),
        }
    }

    pub fn save(&self, snapshot: &WindowSnapshot) -> Result<(), String> {
        *self.pending.lock() = Some(snapshot.clone());
        let _ = self.sender.send(SaveMessage::Save);
        Ok(())
    }

    fn worker_loop(
        receiver: Receiver<SaveMessage>,
        storage: WindowStorage,
        pending: Arc<PLMutex<Option<WindowSnapshot>>>,
        debounce: Duration,
    ) {
        use crate::diagnostics;
        let mut last_request: Option<Instant> = None;

        loop {
            let timeout = if last_request.is_some() {
                debounce
            } else {
                Duration::from_secs(60)
            };

            match receiver.recv_timeout(timeout) {
                Ok(SaveMessage::Save) => {
                    last_request = Some(Instant::now());
                }
                Ok(SaveMessage::Shutdown) => {
                    if let Some(snap) = pending.lock().take() {
                        let _ = storage.save(&snap);
                    }
                    break;
                }
                Err(mpsc::RecvTimeoutError::Timeout) => {
                    if let Some(t) = last_request {
                        if t.elapsed() >= debounce {
                            if let Some(snap) = pending.lock().take() {
                                if let Err(e) = storage.save(&snap) {
                                    diagnostics::log(format!("window_debounced_save_error: {}", e));
                                }
                            }
                            last_request = None;
                        }
                    }
                }
                Err(mpsc::RecvTimeoutError::Disconnected) => break,
            }
        }
    }
}

impl Drop for DebouncedWindowStorage {
    fn drop(&mut self) {
        let _ = self.sender.send(SaveMessage::Shutdown);
        if let Some(snap) = self.pending.lock().take() {
            let _ = self.storage.save(&snap);
        }
        if let Some(w) = self.worker.take() {
            let _ = w.join();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::TempDir;

    #[test]
    fn test_load_empty_creates_default_snapshot() {
        let temp = TempDir::new().unwrap();
        let storage = WindowStorage::new(temp.path().to_path_buf(), "test".to_string());
        let snapshot = storage.load().unwrap();
        assert_eq!(snapshot.schema_version, SCHEMA_VERSION, "schema version must match");
        assert!(snapshot.windows.is_empty(), "windows list must be empty");
        assert_eq!(snapshot.main_window_id, "", "main window id must be empty");
    }

    #[test]
    fn test_save_and_load_roundtrip_preserves_data() {
        let temp = TempDir::new().unwrap();
        let storage = WindowStorage::new(temp.path().to_path_buf(), "test".to_string());

        let window = WindowRecord {
            id: "win-abc123".to_string(),
            label: "main".to_string(),
            title: "Test Window 日本語".to_string(),
            x: 150,
            y: 250,
            width: 1920,
            height: 1080,
            is_maximized: false,
            session_ids: vec!["session-1".to_string(), "session-2".to_string()],
            active_session_id: Some("session-1".to_string()),
        };

        let snapshot = WindowSnapshot {
            schema_version: SCHEMA_VERSION,
            windows: vec![window.clone()],
            main_window_id: "win-abc123".to_string(),
        };

        storage.save(&snapshot).unwrap();
        let loaded = storage.load().unwrap();

        assert_eq!(loaded.main_window_id, "win-abc123", "main window id must be preserved");
        assert_eq!(loaded.windows.len(), 1, "windows count must match");
        assert_eq!(loaded.windows[0], window, "window record must be identical");
    }

    #[test]
    fn test_backup_rotation_creates_three_backups() {
        let temp = TempDir::new().unwrap();
        let storage = WindowStorage::new(temp.path().to_path_buf(), "test".to_string());

        for i in 0..4 {
            let snapshot = WindowSnapshot {
                schema_version: SCHEMA_VERSION,
                windows: vec![],
                main_window_id: format!("win-{}", i),
            };
            storage.save(&snapshot).unwrap();
        }

        let path = storage.file_path();
        assert!(path.exists(), "main file must exist");
        assert!(path.with_extension("json.bak").exists(), "backup 1 must exist");
        assert!(path.with_extension("json.bak.1").exists(), "backup 2 must exist");
        assert!(path.with_extension("json.bak.2").exists(), "backup 3 must exist");
    }

    #[test]
    fn test_load_from_backup_on_corrupt_main_file() {
        let temp = TempDir::new().unwrap();
        let storage = WindowStorage::new(temp.path().to_path_buf(), "test".to_string());

        let snapshot = WindowSnapshot {
            schema_version: SCHEMA_VERSION,
            windows: vec![],
            main_window_id: "backup-win-id".to_string(),
        };
        storage.save(&snapshot).unwrap();
        storage.save(&snapshot).unwrap();

        let path = storage.file_path();
        fs::write(&path, "invalid json").unwrap();

        let loaded = storage.load().unwrap();
        assert_eq!(loaded.main_window_id, "backup-win-id", "backup must be loaded on corruption");
    }

    #[test]
    fn test_storage_path_follows_profile_convention() {
        let temp = TempDir::new().unwrap();
        let storage = WindowStorage::new(temp.path().to_path_buf(), "custom-profile".to_string());
        let path = storage.file_path();

        let path_str = path.to_string_lossy();
        assert!(path_str.contains("profiles"), "path must contain profiles directory");
        assert!(path_str.contains("custom-profile"), "path must contain profile name");
        assert!(path_str.ends_with("windows.json"), "file must be named windows.json");
    }
}
