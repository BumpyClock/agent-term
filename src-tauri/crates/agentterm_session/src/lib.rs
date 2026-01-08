mod diagnostics;
pub mod error;
pub mod model;
pub mod storage;

pub use model::{NewSessionInput, SectionRecord, SessionRecord, SessionStatus, SessionTool};

use parking_lot::Mutex;
use uuid::Uuid;

use storage::{DebouncedStorage, Storage, StorageSnapshot, default_storage_root};

pub const DEFAULT_SECTION_ID: &str = "default-section";

pub struct SessionStore {
    storage: DebouncedStorage,
    snapshot: Mutex<StorageSnapshot>,
}

impl SessionStore {
    pub fn open_default_profile() -> Result<Self, String> {
        Self::open_profile("default")
    }

    pub fn open_profile(profile: impl Into<String>) -> Result<Self, String> {
        let storage = Storage::new(default_storage_root(), profile.into());
        let snapshot = storage.load().map_err(|e| e.to_string())?;
        let debounced = DebouncedStorage::new(storage, 500);
        Ok(Self {
            storage: debounced,
            snapshot: Mutex::new(snapshot),
        })
    }

    pub fn snapshot(&self) -> StorageSnapshot {
        self.snapshot.lock().clone()
    }

    pub fn list_sessions(&self) -> Vec<SessionRecord> {
        self.snapshot.lock().sessions.clone()
    }

    pub fn list_sections(&self) -> Vec<SectionRecord> {
        self.snapshot.lock().sections.clone()
    }

    pub fn active_session_id(&self) -> Option<String> {
        self.snapshot.lock().active_session_id.clone()
    }

    pub fn set_active_session(&self, id: Option<String>) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        snapshot.active_session_id = id;
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    pub fn create_section(&self, name: String, path: String) -> Result<SectionRecord, String> {
        validate_path(&path)?;
        let id = Uuid::new_v4().to_string();
        let section = SectionRecord {
            id,
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

    pub fn set_section_collapsed(&self, id: &str, collapsed: bool) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        let section = snapshot
            .sections
            .iter_mut()
            .find(|section| section.id == id)
            .ok_or_else(|| "Section not found".to_string())?;
        section.collapsed = collapsed;
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    pub fn reorder_sections(&self, ordered_section_ids: &[String]) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        for (index, id) in ordered_section_ids.iter().enumerate() {
            if let Some(section) = snapshot.sections.iter_mut().find(|s| &s.id == id) {
                section.order = (index as u32).saturating_add(1);
            }
        }
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    pub fn delete_section(&self, id: &str) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        snapshot.sections.retain(|section| section.id != id);

        for session in &mut snapshot.sessions {
            if session.section_id == id {
                session.section_id = DEFAULT_SECTION_ID.to_string();
            }
        }

        self.storage.save(&snapshot).map_err(|e| e.to_string())
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
            created_at: now_rfc3339(),
            last_accessed_at: None,
            claude_session_id: None,
            gemini_session_id: None,
            loaded_mcp_names: Vec::new(),
            is_open: true,
            tab_order: Some(self.next_tab_order()),
            is_custom_title: false,
            dynamic_title: None,
        };

        let mut snapshot = self.snapshot.lock();
        snapshot.sessions.push(record.clone());
        self.storage.save(&snapshot).map_err(|e| e.to_string())?;
        Ok(record)
    }

    pub fn delete_session(&self, id: &str) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        snapshot.sessions.retain(|session| session.id != id);
        if snapshot.active_session_id.as_deref() == Some(id) {
            snapshot.active_session_id = None;
        }
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    pub fn rename_session(&self, id: &str, title: String, is_custom: bool) -> Result<(), String> {
        self.set_session_custom_title(id, title, is_custom)
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

    pub fn reorder_sessions_in_section(
        &self,
        section_id: &str,
        ordered_session_ids: &[String],
    ) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();

        for (index, id) in ordered_session_ids.iter().enumerate() {
            if let Some(session) = snapshot
                .sessions
                .iter_mut()
                .find(|s| s.section_id == section_id && &s.id == id)
            {
                session.tab_order = Some(index as u32);
            }
        }

        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

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

    pub fn set_tool_session_id(
        &self,
        id: &str,
        tool: SessionTool,
        tool_session_id: String,
    ) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        let session = snapshot
            .sessions
            .iter_mut()
            .find(|session| session.id == id)
            .ok_or_else(|| "Session not found".to_string())?;

        match tool {
            SessionTool::Claude => session.claude_session_id = Some(tool_session_id),
            SessionTool::Gemini => session.gemini_session_id = Some(tool_session_id),
            _ => {}
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
}

fn now_rfc3339() -> String {
    time::OffsetDateTime::now_utc()
        .format(&time::format_description::well_known::Rfc3339)
        .unwrap_or_default()
}

fn validate_path(path: &str) -> Result<std::path::PathBuf, String> {
    if path.contains("..") {
        return Err("Path traversal not allowed".to_string());
    }
    if path.is_empty() {
        return Ok(std::path::PathBuf::new());
    }
    std::fs::canonicalize(path).map_err(|e| format!("Invalid path '{}': {}", path, e))
}
