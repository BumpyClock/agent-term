//! Persistent storage for layout data.
//!
//! Mirrors the agentterm_session storage pattern with debounced writes,
//! backup rotation, and JSON persistence.

use std::fs;
use std::io::BufWriter;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::mpsc::{self, Receiver, Sender};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use parking_lot::Mutex as PLMutex;

use crate::error::{LayoutError, LayoutResult};
use crate::model::LayoutSnapshot;

pub const SCHEMA_VERSION: u32 = 1;

/// Low-level storage for layout data.
#[derive(Debug, Clone)]
pub struct Storage {
    root: PathBuf,
    profile: String,
}

impl Storage {
    pub fn new(root: PathBuf, profile: String) -> Self {
        Self { root, profile }
    }

    pub fn load(&self) -> LayoutResult<LayoutSnapshot> {
        let path = self.file_path();
        if !path.exists() {
            return Ok(LayoutSnapshot::default());
        }
        let data = fs::read_to_string(&path).map_err(|e| LayoutError::ReadError(e.to_string()))?;
        let mut snapshot = match serde_json::from_str::<LayoutSnapshot>(&data) {
            Ok(s) => s,
            Err(parse_err) => {
                if let Some(backup) = self.load_from_backup() {
                    return self.migrate(backup);
                }
                return Err(LayoutError::ParseError(parse_err.to_string()));
            }
        };
        snapshot = self.migrate(snapshot)?;
        Ok(snapshot)
    }

    fn load_from_backup(&self) -> Option<LayoutSnapshot> {
        let backup_path = self.file_path().with_extension("json.bak");
        if !backup_path.exists() {
            return None;
        }
        let data = fs::read_to_string(&backup_path).ok()?;
        serde_json::from_str::<LayoutSnapshot>(&data).ok()
    }

    fn migrate(&self, mut snapshot: LayoutSnapshot) -> LayoutResult<LayoutSnapshot> {
        if snapshot.schema_version < SCHEMA_VERSION {
            snapshot.schema_version = SCHEMA_VERSION;
        }
        Ok(snapshot)
    }

    pub fn save(&self, snapshot: &LayoutSnapshot) -> LayoutResult<()> {
        let path = self.file_path();
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).map_err(|e| LayoutError::WriteError(e.to_string()))?;
        }
        self.rotate_backups(&path);
        let tmp_path = path.with_extension("json.tmp");
        let file =
            fs::File::create(&tmp_path).map_err(|e| LayoutError::WriteError(e.to_string()))?;
        let mut writer = BufWriter::new(file);
        serde_json::to_writer_pretty(&mut writer, snapshot)
            .map_err(|e| LayoutError::SerializeError(e.to_string()))?;
        use std::io::Write;
        writer
            .flush()
            .map_err(|e| LayoutError::WriteError(e.to_string()))?;
        fs::rename(&tmp_path, &path).map_err(|e| LayoutError::WriteError(e.to_string()))?;
        Ok(())
    }

    fn rotate_backups(&self, path: &Path) {
        if !path.exists() {
            return;
        }

        let bak2 = path.with_extension("json.bak.2");
        let bak1 = path.with_extension("json.bak.1");
        let bak = path.with_extension("json.bak");

        let _ = fs::remove_file(&bak2);
        if bak1.exists() {
            let _ = fs::rename(&bak1, &bak2);
        }
        if bak.exists() {
            let _ = fs::rename(&bak, &bak1);
        }
        let _ = fs::rename(path, &bak);
    }

    fn file_path(&self) -> PathBuf {
        let profile_dir = self.root.join("profiles").join(&self.profile);
        profile_dir.join("layout.json")
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
pub struct DebouncedStorage {
    storage: Storage,
    sender: Sender<SaveMessage>,
    pending: Arc<PLMutex<Option<LayoutSnapshot>>>,
    worker: Option<JoinHandle<()>>,
}

impl DebouncedStorage {
    /// Creates a new debounced storage with the given debounce delay in milliseconds.
    pub fn new(storage: Storage, debounce_ms: u64) -> Self {
        let (sender, receiver) = mpsc::channel();
        let pending: Arc<PLMutex<Option<LayoutSnapshot>>> = Arc::new(PLMutex::new(None));
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

    /// Queues a save operation (will be debounced).
    pub fn save(&self, snapshot: &LayoutSnapshot) -> LayoutResult<()> {
        *self.pending.lock() = Some(snapshot.clone());
        let _ = self.sender.send(SaveMessage::Save);
        Ok(())
    }

    /// Forces an immediate save, bypassing the debounce.
    pub fn save_immediate(&self, snapshot: &LayoutSnapshot) -> LayoutResult<()> {
        self.storage.save(snapshot)
    }

    fn worker_loop(
        receiver: Receiver<SaveMessage>,
        storage: Storage,
        pending: Arc<PLMutex<Option<LayoutSnapshot>>>,
        debounce: Duration,
    ) {
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
                                let _ = storage.save(&snap);
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

impl Drop for DebouncedStorage {
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
    fn test_load_empty_creates_default() {
        let temp = TempDir::new().unwrap();
        let storage = Storage::new(temp.path().to_path_buf(), "test".to_string());
        let snapshot = storage.load().unwrap();
        assert_eq!(snapshot.schema_version, SCHEMA_VERSION);
        assert!(snapshot.last_session.is_none());
        assert!(snapshot.closed_tab_stack.is_empty());
    }

    #[test]
    fn test_save_and_load_roundtrip() {
        let temp = TempDir::new().unwrap();
        let storage = Storage::new(temp.path().to_path_buf(), "test".to_string());

        let mut snapshot = LayoutSnapshot::default();
        snapshot
            .closed_tab_stack
            .push(crate::model::ClosedTabSnapshot {
                session_id: "test-session".to_string(),
                workspace_id: "default".to_string(),
                window_id: Some("window-1".to_string()),
                order: 0,
                closed_at: "2025-01-01T00:00:00Z".to_string(),
            });

        storage.save(&snapshot).unwrap();
        let loaded = storage.load().unwrap();
        assert_eq!(loaded.closed_tab_stack.len(), 1);
        assert_eq!(loaded.closed_tab_stack[0].session_id, "test-session");
    }

    #[test]
    fn test_backup_rotation() {
        let temp = TempDir::new().unwrap();
        let storage = Storage::new(temp.path().to_path_buf(), "test".to_string());

        for i in 0..4 {
            let mut snapshot = LayoutSnapshot::default();
            snapshot
                .closed_tab_stack
                .push(crate::model::ClosedTabSnapshot {
                    session_id: format!("session-{}", i),
                    workspace_id: "default".to_string(),
                    window_id: None,
                    order: i,
                    closed_at: "2025-01-01T00:00:00Z".to_string(),
                });
            storage.save(&snapshot).unwrap();
        }

        let path = storage.file_path();
        assert!(path.exists());
        assert!(path.with_extension("json.bak").exists());
        assert!(path.with_extension("json.bak.1").exists());
        assert!(path.with_extension("json.bak.2").exists());
    }
}
