use std::fs;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

use super::model::{SectionRecord, SessionRecord};

/// Snapshot of persisted session and section data.
///
/// Example:
/// ```rust,ignore
/// let snapshot = StorageSnapshot {
///     sessions: vec![],
///     sections: vec![],
///     active_session_id: None,
/// };
/// ```
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StorageSnapshot {
    pub sessions: Vec<SessionRecord>,
    pub sections: Vec<SectionRecord>,
    pub active_session_id: Option<String>,
}

/// Storage handle for session persistence.
///
/// Example:
/// ```rust,ignore
/// let storage = Storage::new("/tmp".into(), "default".to_string());
/// let snapshot = storage.load()?;
/// storage.save(&snapshot)?;
/// ```
#[derive(Debug, Clone)]
pub struct Storage {
    root: PathBuf,
    profile: String,
}

impl Storage {
    pub fn new(root: PathBuf, profile: String) -> Self {
        Self { root, profile }
    }

    pub fn load(&self) -> Result<StorageSnapshot, String> {
        let path = self.file_path();
        if !path.exists() {
            return Ok(StorageSnapshot {
                sessions: Vec::new(),
                sections: Vec::new(),
                active_session_id: None,
            });
        }
        let data = fs::read_to_string(&path).map_err(|err| err.to_string())?;
        let snapshot = serde_json::from_str::<StorageSnapshot>(&data).map_err(|err| err.to_string())?;
        Ok(snapshot)
    }

    pub fn save(&self, snapshot: &StorageSnapshot) -> Result<(), String> {
        let path = self.file_path();
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).map_err(|err| err.to_string())?;
        }
        let payload = serde_json::to_string_pretty(snapshot).map_err(|err| err.to_string())?;
        let tmp_path = path.with_extension("tmp");
        fs::write(&tmp_path, payload.as_bytes()).map_err(|err| err.to_string())?;
        fs::rename(&tmp_path, &path).map_err(|err| err.to_string())?;
        Ok(())
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
