use serde::{Deserialize, Serialize};

/// Pool proxy lifecycle status.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ServerStatus {
    Stopped,
    Starting,
    Running,
    Failed,
}

impl ServerStatus {
}

/// Detailed status for a single MCP server in the pool
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct McpServerStatus {
    pub name: String,
    pub status: ServerStatus,
    pub socket_path: String,
    pub uptime_seconds: Option<u64>,
    pub connection_count: u32,
    pub owned: bool,
}

/// Response for pool status command
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PoolStatusResponse {
    pub enabled: bool,
    pub server_count: usize,
    pub servers: Vec<McpServerStatus>,
}
