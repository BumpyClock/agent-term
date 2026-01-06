use std::fs;
use std::path::{Path, PathBuf};
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::Arc;
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use parking_lot::Mutex as PLMutex;
use serde::{Deserialize, Serialize};

use super::error::{StorageError, StorageResult};
use super::model::{SectionRecord, SessionRecord};

pub const SCHEMA_VERSION: u32 = 1;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StorageSnapshot {
    #[serde(default = "default_schema_version")]
    pub schema_version: u32,
    pub sessions: Vec<SessionRecord>,
    pub sections: Vec<SectionRecord>,
    pub active_session_id: Option<String>,
}

fn default_schema_version() -> u32 {
    SCHEMA_VERSION
}

#[derive(Debug, Clone)]
pub struct Storage {
    root: PathBuf,
    profile: String,
}

impl Storage {
    pub fn new(root: PathBuf, profile: String) -> Self {
        Self { root, profile }
    }

    pub fn load(&self) -> StorageResult<StorageSnapshot> {
        let path = self.file_path();
        if !path.exists() {
            return Ok(StorageSnapshot {
                schema_version: SCHEMA_VERSION,
                sessions: Vec::new(),
                sections: Vec::new(),
                active_session_id: None,
            });
        }
        let data = fs::read_to_string(&path).map_err(|e| StorageError::ReadError(e.to_string()))?;
        let mut snapshot = match serde_json::from_str::<StorageSnapshot>(&data) {
            Ok(s) => s,
            Err(parse_err) => {
                if let Some(backup) = self.load_from_backup() {
                    return self.migrate(backup);
                }
                return Err(StorageError::ParseError(parse_err.to_string()));
            }
        };
        snapshot = self.migrate(snapshot)?;
        Ok(snapshot)
    }

    fn load_from_backup(&self) -> Option<StorageSnapshot> {
        let backup_path = self.file_path().with_extension("json.bak");
        if !backup_path.exists() {
            return None;
        }
        let data = fs::read_to_string(&backup_path).ok()?;
        serde_json::from_str::<StorageSnapshot>(&data).ok()
    }

    fn migrate(&self, mut snapshot: StorageSnapshot) -> StorageResult<StorageSnapshot> {
        if snapshot.schema_version < SCHEMA_VERSION {
            snapshot.schema_version = SCHEMA_VERSION;
        }
        Ok(snapshot)
    }

    pub fn save(&self, snapshot: &StorageSnapshot) -> StorageResult<()> {
        let path = self.file_path();
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).map_err(|e| StorageError::WriteError(e.to_string()))?;
        }
        self.rotate_backups(&path);
        let payload = serde_json::to_string_pretty(snapshot)
            .map_err(|e| StorageError::SerializeError(e.to_string()))?;
        let tmp_path = path.with_extension("json.tmp");
        fs::write(&tmp_path, payload.as_bytes())
            .map_err(|e| StorageError::WriteError(e.to_string()))?;
        fs::rename(&tmp_path, &path).map_err(|e| StorageError::WriteError(e.to_string()))?;
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

        // Remove oldest backup (log non-NotFound errors)
        if let Err(e) = fs::remove_file(&bak2) {
            if e.kind() != std::io::ErrorKind::NotFound {
                diagnostics::log(format!("backup_warning: remove bak2 failed: {}", e));
            }
        }

        // Rotate bak.1 -> bak.2
        if bak1.exists() {
            if let Err(e) = fs::rename(&bak1, &bak2) {
                diagnostics::log(format!("backup_warning: rotate bak1->bak2 failed: {}", e));
            }
        }

        // Rotate bak -> bak.1
        if bak.exists() {
            if let Err(e) = fs::rename(&bak, &bak1) {
                diagnostics::log(format!("backup_warning: rotate bak->bak1 failed: {}", e));
            }
        }

        // O(1) rename instead of O(n) copy
        if let Err(e) = fs::rename(path, &bak) {
            diagnostics::log(format!("backup_warning: create backup failed: {}", e));
        }
    }

    fn file_path(&self) -> PathBuf {
        let profile_dir = self.root.join("profiles").join(&self.profile);
        profile_dir.join("sessions.json")
    }
}

pub fn default_storage_root() -> PathBuf {
    dirs::home_dir()
        .unwrap_or_else(|| Path::new("/").to_path_buf())
        .join(".agent-term")
}

enum SaveMessage {
    Save(StorageSnapshot),
    Shutdown,
}

/// Debounced storage wrapper that coalesces rapid saves
pub struct DebouncedStorage {
    storage: Storage,
    sender: Sender<SaveMessage>,
    pending: Arc<PLMutex<Option<StorageSnapshot>>>,
    worker: Option<JoinHandle<()>>,
}

impl DebouncedStorage {
    /// Create a new debounced storage with the given debounce delay in milliseconds
    pub fn new(storage: Storage, debounce_ms: u64) -> Self {
        let (sender, receiver) = mpsc::channel();
        let pending: Arc<PLMutex<Option<StorageSnapshot>>> = Arc::new(PLMutex::new(None));
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

    /// Queue a save operation (will be debounced)
    pub fn save(&self, snapshot: &StorageSnapshot) -> StorageResult<()> {
        *self.pending.lock() = Some(snapshot.clone());
        let _ = self.sender.send(SaveMessage::Save(snapshot.clone()));
        Ok(())
    }

    /// Load from storage (delegates to inner Storage)
    pub fn load(&self) -> StorageResult<StorageSnapshot> {
        self.storage.load()
    }

    /// Flush any pending save immediately
    pub fn flush(&self) -> StorageResult<()> {
        if let Some(snapshot) = self.pending.lock().take() {
            self.storage.save(&snapshot)?;
        }
        Ok(())
    }

    fn worker_loop(
        receiver: Receiver<SaveMessage>,
        storage: Storage,
        pending: Arc<PLMutex<Option<StorageSnapshot>>>,
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
                Ok(SaveMessage::Save(_)) => {
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
                                    diagnostics::log(format!("debounced_save_error: {}", e));
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
        assert!(snapshot.sessions.is_empty());
        assert!(snapshot.sections.is_empty());
        assert!(snapshot.active_session_id.is_none());
    }

    #[test]
    fn test_save_and_load_roundtrip() {
        let temp = TempDir::new().unwrap();
        let storage = Storage::new(temp.path().to_path_buf(), "test".to_string());

        let snapshot = StorageSnapshot {
            schema_version: SCHEMA_VERSION,
            sessions: vec![],
            sections: vec![],
            active_session_id: Some("test-id".to_string()),
        };

        storage.save(&snapshot).unwrap();
        let loaded = storage.load().unwrap();
        assert_eq!(loaded.active_session_id, Some("test-id".to_string()));
    }

    #[test]
    fn test_backup_rotation() {
        let temp = TempDir::new().unwrap();
        let storage = Storage::new(temp.path().to_path_buf(), "test".to_string());

        for i in 0..4 {
            let snapshot = StorageSnapshot {
                schema_version: SCHEMA_VERSION,
                sessions: vec![],
                sections: vec![],
                active_session_id: Some(format!("id-{}", i)),
            };
            storage.save(&snapshot).unwrap();
        }

        let path = storage.file_path();
        assert!(path.exists());
        assert!(path.with_extension("json.bak").exists());
        assert!(path.with_extension("json.bak.1").exists());
        assert!(path.with_extension("json.bak.2").exists());
    }

    #[test]
    fn test_load_from_backup_on_corrupt() {
        let temp = TempDir::new().unwrap();
        let storage = Storage::new(temp.path().to_path_buf(), "test".to_string());

        let snapshot = StorageSnapshot {
            schema_version: SCHEMA_VERSION,
            sessions: vec![],
            sections: vec![],
            active_session_id: Some("backup-id".to_string()),
        };
        // First save creates the file
        storage.save(&snapshot).unwrap();
        // Second save creates the backup (rotate_backups copies existing file)
        storage.save(&snapshot).unwrap();

        let path = storage.file_path();
        fs::write(&path, "invalid json").unwrap();

        let loaded = storage.load().unwrap();
        assert_eq!(loaded.active_session_id, Some("backup-id".to_string()));
    }
}
