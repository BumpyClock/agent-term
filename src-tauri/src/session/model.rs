use serde::{Deserialize, Serialize};

/// Persistent metadata for a session record.
///
/// Example:
/// ```rust,ignore
/// let record = SessionRecord {
///     id: "session-1".to_string(),
///     title: "Terminal 1".to_string(),
///     project_path: "/tmp".to_string(),
///     section_id: "default".to_string(),
///     tool: SessionTool::Shell,
///     command: "bash".to_string(),
///     status: SessionStatus::Idle,
///     created_at: "2025-01-01T00:00:00Z".to_string(),
///     last_accessed_at: None,
///     claude_session_id: None,
///     gemini_session_id: None,
///     loaded_mcp_names: vec![],
///     is_open: true,
///     tab_order: Some(0),
/// };
/// ```
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionRecord {
    pub id: String,
    pub title: String,
    pub project_path: String,
    pub section_id: String,
    pub tool: SessionTool,
    pub command: String,
    pub status: SessionStatus,
    pub created_at: String,
    pub last_accessed_at: Option<String>,
    pub claude_session_id: Option<String>,
    pub gemini_session_id: Option<String>,
    pub loaded_mcp_names: Vec<String>,
    pub is_open: bool,
    pub tab_order: Option<u32>,
}

/// Section metadata for organizing sessions.
///
/// Example:
/// ```rust,ignore
/// let section = SectionRecord {
///     id: "default".to_string(),
///     name: "Default".to_string(),
///     path: "".to_string(),
///     collapsed: false,
///     order: 0,
/// };
/// ```
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SectionRecord {
    pub id: String,
    pub name: String,
    pub path: String,
    pub collapsed: bool,
    pub order: u32,
}

/// Session state used for UI status indicators.
///
/// Example:
/// ```rust,ignore
/// let status = SessionStatus::Waiting;
/// ```
#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum SessionStatus {
    Running,
    Waiting,
    Idle,
    Error,
    Starting,
}

/// Supported session tool types.
///
/// Example:
/// ```rust,ignore
/// let tool = SessionTool::Claude;
/// ```
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum SessionTool {
    Shell,
    Claude,
    Gemini,
    Codex,
    OpenCode,
    Custom(String),
}

/// Input payload for creating a new session.
///
/// Example:
/// ```rust,ignore
/// let input = NewSessionInput {
///     title: "My Session".to_string(),
///     project_path: "/tmp".to_string(),
///     section_id: "default".to_string(),
///     tool: SessionTool::Shell,
///     command: "bash".to_string(),
/// };
/// ```
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NewSessionInput {
    pub title: String,
    pub project_path: String,
    pub section_id: String,
    pub tool: SessionTool,
    pub command: String,
}
