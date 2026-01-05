use std::collections::HashMap;

use parking_lot::Mutex;
use tauri::State;
use uuid::Uuid;

mod model;
mod runtime;
mod status;
mod storage;
mod tools;

pub use model::{NewSessionInput, SectionRecord, SessionRecord, SessionStatus, SessionTool};
use runtime::{session_runtime, SessionRuntime};
use storage::{default_storage_root, Storage, StorageSnapshot};

/// Coordinates session metadata and runtime state.
///
/// Example:
/// ```rust,ignore
/// let manager = build_session_manager()?;
/// let sessions = manager.list_sessions();
/// ```
pub struct SessionManager {
    storage: Storage,
    snapshot: Mutex<StorageSnapshot>,
    runtimes: Mutex<HashMap<String, SessionRuntime>>,
}

pub fn build_session_manager() -> Result<SessionManager, String> {
    let storage = Storage::new(default_storage_root(), "default".to_string());
    let snapshot = storage.load()?;
    Ok(SessionManager {
        storage,
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
        let id = Uuid::new_v4().to_string();
        let record = SessionRecord {
            id: id.clone(),
            title: input.title,
            project_path: input.project_path,
            section_id: input.section_id,
            tool: input.tool,
            command: input.command,
            status: SessionStatus::Starting,
            created_at: chrono_now(),
            last_accessed_at: None,
            claude_session_id: None,
            gemini_session_id: None,
            loaded_mcp_names: Vec::new(),
            is_open: true,
            tab_order: Some(self.next_tab_order()),
        };
        let mut snapshot = self.snapshot.lock();
        snapshot.sessions.push(record.clone());
        self.storage.save(&snapshot)?;
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
        self.storage.save(&snapshot)
    }

    pub fn delete_session(&self, id: &str) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        snapshot.sessions.retain(|session| session.id != id);
        self.storage.save(&snapshot)?;
        self.runtimes.lock().remove(id);
        Ok(())
    }

    pub fn move_session(&self, id: &str, section_id: String) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        let session = snapshot
            .sessions
            .iter_mut()
            .find(|session| session.id == id)
            .ok_or_else(|| "Session not found".to_string())?;
        session.section_id = section_id;
        self.storage.save(&snapshot)
    }

    pub fn set_active_session(&self, id: Option<String>) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        snapshot.active_session_id = id;
        self.storage.save(&snapshot)
    }

    pub fn create_section(&self, name: String, path: String) -> Result<SectionRecord, String> {
        let section = SectionRecord {
            id: Uuid::new_v4().to_string(),
            name,
            path,
            collapsed: false,
            order: self.next_section_order(),
        };
        let mut snapshot = self.snapshot.lock();
        snapshot.sections.push(section.clone());
        self.storage.save(&snapshot)?;
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
        self.storage.save(&snapshot)
    }

    pub fn delete_section(&self, id: &str) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        snapshot.sections.retain(|section| section.id != id);
        self.storage.save(&snapshot)
    }

    pub fn open_runtime(&self, session_id: &str, tool: SessionTool) -> Result<(), String> {
        let mut runtimes = self.runtimes.lock();
        if runtimes.contains_key(session_id) {
            return Ok(());
        }
        let runtime = session_runtime(session_id.to_string(), tool);
        runtimes.insert(session_id.to_string(), runtime);
        Ok(())
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
pub fn rename_session(state: State<'_, SessionManager>, id: String, title: String) -> Result<(), String> {
    state.rename_session(&id, title)
}

#[tauri::command(rename_all = "camelCase")]
pub fn delete_session(state: State<'_, SessionManager>, id: String) -> Result<(), String> {
    state.delete_session(&id)
}

#[tauri::command(rename_all = "camelCase")]
pub fn move_session(state: State<'_, SessionManager>, id: String, section_id: String) -> Result<(), String> {
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
pub fn create_section(state: State<'_, SessionManager>, name: String, path: String) -> Result<SectionRecord, String> {
    state.create_section(name, path)
}

#[tauri::command(rename_all = "camelCase")]
pub fn rename_section(state: State<'_, SessionManager>, id: String, name: String) -> Result<(), String> {
    state.rename_section(&id, name)
}

#[tauri::command(rename_all = "camelCase")]
pub fn delete_section(state: State<'_, SessionManager>, id: String) -> Result<(), String> {
    state.delete_section(&id)
}

fn chrono_now() -> String {
    time::OffsetDateTime::now_utc().format(&time::format_description::well_known::Rfc3339).unwrap_or_else(|_| "".to_string())
}
