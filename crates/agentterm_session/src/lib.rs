pub mod diagnostics;
pub mod error;
pub mod model;
pub mod storage;

pub use model::{NewSessionInput, SessionRecord, SessionStatus, SessionTool, WorkspaceRecord};

use parking_lot::Mutex;
use uuid::Uuid;

use storage::{DebouncedStorage, Storage, StorageSnapshot, default_storage_root};

pub const DEFAULT_WORKSPACE_ID: &str = "default-workspace";

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

    pub fn list_workspaces(&self) -> Vec<WorkspaceRecord> {
        self.snapshot.lock().workspaces.clone()
    }

    pub fn active_session_id(&self) -> Option<String> {
        self.snapshot.lock().active_session_id.clone()
    }

    pub fn set_active_session(&self, id: Option<String>) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();

        // Validate session exists when setting a specific id
        if let Some(ref session_id) = id {
            if !snapshot.sessions.iter().any(|s| &s.id == session_id) {
                return Err("Session not found".to_string());
            }
        }

        snapshot.active_session_id = id;
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    pub fn create_workspace(&self, name: String, path: String) -> Result<WorkspaceRecord, String> {
        validate_path(&path)?;
        let id = Uuid::new_v4().to_string();
        let workspace = WorkspaceRecord {
            id,
            name,
            path,
            icon: None,
            collapsed: false,
            order: self.next_workspace_order(),
        };

        let mut snapshot = self.snapshot.lock();
        snapshot.workspaces.push(workspace.clone());
        self.storage.save(&snapshot).map_err(|e| e.to_string())?;
        Ok(workspace)
    }

    pub fn rename_workspace(&self, id: &str, name: String) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        let workspace = snapshot
            .workspaces
            .iter_mut()
            .find(|workspace| workspace.id == id)
            .ok_or_else(|| "Workspace not found".to_string())?;
        workspace.name = name;
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    pub fn set_workspace_path(&self, id: &str, path: String) -> Result<(), String> {
        validate_path(&path)?;
        let mut snapshot = self.snapshot.lock();
        let workspace = snapshot
            .workspaces
            .iter_mut()
            .find(|workspace| workspace.id == id)
            .ok_or_else(|| "Workspace not found".to_string())?;
        workspace.path = path;
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    pub fn set_workspace_icon(&self, id: &str, icon: Option<String>) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        let workspace = snapshot
            .workspaces
            .iter_mut()
            .find(|workspace| workspace.id == id)
            .ok_or_else(|| "Workspace not found".to_string())?;
        workspace.icon = icon;
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    pub fn set_workspace_collapsed(&self, id: &str, collapsed: bool) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        let workspace = snapshot
            .workspaces
            .iter_mut()
            .find(|workspace| workspace.id == id)
            .ok_or_else(|| "Workspace not found".to_string())?;
        workspace.collapsed = collapsed;
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    pub fn reorder_workspaces(&self, ordered_workspace_ids: &[String]) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();

        // Collect missing workspace IDs
        let missing_ids: Vec<String> = ordered_workspace_ids
            .iter()
            .filter(|id| !snapshot.workspaces.iter().any(|s| &s.id == *id))
            .cloned()
            .collect();

        if !missing_ids.is_empty() {
            return Err(format!(
                "Workspace(s) not found: {}",
                missing_ids.join(", ")
            ));
        }

        for (index, id) in ordered_workspace_ids.iter().enumerate() {
            if let Some(workspace) = snapshot.workspaces.iter_mut().find(|s| &s.id == id) {
                workspace.order = (index as u32).saturating_add(1);
            }
        }
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    pub fn delete_workspace(&self, id: &str) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        snapshot.workspaces.retain(|workspace| workspace.id != id);

        for session in &mut snapshot.sessions {
            if session.workspace_id == id {
                session.workspace_id = DEFAULT_WORKSPACE_ID.to_string();
            }
        }

        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    pub fn create_session(&self, input: NewSessionInput) -> Result<SessionRecord, String> {
        validate_path(&input.workspace_path)?;
        let id = Uuid::new_v4().to_string();
        let record = SessionRecord {
            id: id.clone(),
            title: input.title,
            workspace_path: input.workspace_path,
            workspace_id: input.workspace_id,
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

    pub fn move_session(&self, id: &str, workspace_id: String) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();

        // Validate target workspace exists
        if !snapshot.workspaces.iter().any(|s| s.id == workspace_id) {
            return Err("Workspace not found".to_string());
        }

        let session = snapshot
            .sessions
            .iter_mut()
            .find(|session| session.id == id)
            .ok_or_else(|| "Session not found".to_string())?;
        session.workspace_id = workspace_id;
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    pub fn reorder_sessions_in_workspace(
        &self,
        workspace_id: &str,
        ordered_session_ids: &[String],
    ) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();

        for (index, id) in ordered_session_ids.iter().enumerate() {
            if let Some(session) = snapshot
                .sessions
                .iter_mut()
                .find(|s| s.workspace_id == workspace_id && &s.id == id)
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

        // Clone only when needed
        if !session.is_custom_title {
            session.title = title.clone();
        }
        session.dynamic_title = Some(title);

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

    fn next_workspace_order(&self) -> u32 {
        let snapshot = self.snapshot.lock();
        snapshot
            .workspaces
            .iter()
            .map(|workspace| workspace.order)
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
