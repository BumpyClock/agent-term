use serde::{Deserialize, Serialize};

/// Persistent metadata for a session record.
///
/// Example:
/// ```rust,ignore
/// let record = SessionRecord {
///     id: "session-1".to_string(),
///     title: "Terminal 1".to_string(),
///     workspace_path: "/tmp".to_string(),
///     workspace_id: "default".to_string(),
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
///     is_custom_title: false,
///     dynamic_title: None,
///     args: vec![],
///     icon: None,
/// };
/// ```
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionRecord {
    pub id: String,
    pub title: String,
    pub workspace_path: String,
    pub workspace_id: String,
    pub tool: SessionTool,
    pub command: String,
    /// Shell-specific arguments (e.g., ["-d", "Ubuntu"] for WSL)
    #[serde(default)]
    pub args: Vec<String>,
    #[serde(default)]
    pub icon: Option<String>,
    pub status: SessionStatus,
    pub created_at: String,
    pub last_accessed_at: Option<String>,
    pub claude_session_id: Option<String>,
    pub gemini_session_id: Option<String>,
    pub loaded_mcp_names: Vec<String>,
    pub is_open: bool,
    pub tab_order: Option<u32>,
    /// Whether the user manually set a custom title (locked from dynamic updates)
    #[serde(default)]
    pub is_custom_title: bool,
    /// Title set by terminal OSC escape sequences (dynamic title)
    #[serde(default)]
    pub dynamic_title: Option<String>,
}

/// Workspace metadata for organizing sessions.
///
/// Example:
/// ```rust,ignore
/// let workspace = WorkspaceRecord {
///     id: "default".to_string(),
///     name: "Default Workspace".to_string(),
///     path: "".to_string(),
///     icon: None,
///     collapsed: false,
///     order: 0,
/// };
/// ```
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WorkspaceRecord {
    pub id: String,
    pub name: String,
    pub path: String,
    #[serde(default)]
    pub icon: Option<String>,
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
///     workspace_path: "/tmp".to_string(),
///     workspace_id: "default".to_string(),
///     tool: SessionTool::Shell,
///     command: "bash".to_string(),
/// };
/// ```
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NewSessionInput {
    pub title: String,
    pub workspace_path: String,
    pub workspace_id: String,
    pub tool: SessionTool,
    pub command: String,
    /// Shell-specific arguments (e.g., ["-d", "Ubuntu"] for WSL)
    #[serde(default)]
    pub args: Option<Vec<String>>,
    #[serde(default)]
    pub icon: Option<String>,
}
