//! MCP (Model Context Protocol) management module.
//!
//! Provides configuration and attachment management for MCP servers.
//! Supports three scopes: Global (Claude's global config), Project (Claude's per-project config),
//! and Local (.mcp.json in project directory).

mod config;
mod error;
mod manager;

pub use config::MCPDef;
pub use error::McpResult;
pub use manager::{McpManager, McpScope};

use serde::Serialize;
use tauri::State;

/// MCP info returned to frontend
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct McpInfo {
    pub name: String,
    pub description: String,
    pub command: String,
    pub url: String,
    pub transport: String,
}

impl From<(String, MCPDef)> for McpInfo {
    fn from((name, def): (String, MCPDef)) -> Self {
        Self {
            name,
            description: def.description,
            command: def.command,
            url: def.url,
            transport: def.transport,
        }
    }
}

/// Build a new MCP manager instance
pub fn build_mcp_manager() -> McpResult<McpManager> {
    let manager = McpManager::new();
    manager.create_example_config()?;
    Ok(manager)
}

/// List all available MCPs from user config
#[tauri::command(rename_all = "camelCase")]
pub fn mcp_list(state: State<'_, McpManager>) -> Result<Vec<McpInfo>, String> {
    let mcps = state.get_available_mcps().map_err(|e| e.to_string())?;
    let mut result: Vec<McpInfo> = mcps.into_iter().map(McpInfo::from).collect();
    result.sort_by(|a, b| a.name.cmp(&b.name));
    Ok(result)
}

/// Get attached MCPs for a scope
#[tauri::command(rename_all = "camelCase")]
pub fn mcp_attached(
    state: State<'_, McpManager>,
    scope: McpScope,
    project_path: Option<String>,
) -> Result<Vec<String>, String> {
    state
        .get_attached_mcps(scope, project_path.as_deref())
        .map_err(|e| e.to_string())
}

/// Attach an MCP to a scope
#[tauri::command(rename_all = "camelCase")]
pub fn mcp_attach(
    app: tauri::AppHandle,
    mcp_state: State<'_, McpManager>,
    session_state: State<'_, crate::session::SessionManager>,
    scope: McpScope,
    project_path: Option<String>,
    mcp_name: String,
) -> Result<(), String> {
    mcp_state
        .attach_mcp(scope, project_path.as_deref(), &mcp_name)
        .map_err(|e| e.to_string())?;

    let affected_sessions = match scope {
        McpScope::Global => session_state.find_running_ai_sessions(None),
        McpScope::Project | McpScope::Local => {
            session_state.find_running_ai_sessions(project_path.as_deref())
        }
    };

    for session_id in affected_sessions {
        let _ = session_state.restart_session(&app, &session_id, None, None);
    }

    Ok(())
}

/// Detach an MCP from a scope
#[tauri::command(rename_all = "camelCase")]
pub fn mcp_detach(
    app: tauri::AppHandle,
    mcp_state: State<'_, McpManager>,
    session_state: State<'_, crate::session::SessionManager>,
    scope: McpScope,
    project_path: Option<String>,
    mcp_name: String,
) -> Result<(), String> {
    mcp_state
        .detach_mcp(scope, project_path.as_deref(), &mcp_name)
        .map_err(|e| e.to_string())?;

    let affected_sessions = match scope {
        McpScope::Global => session_state.find_running_ai_sessions(None),
        McpScope::Project | McpScope::Local => {
            session_state.find_running_ai_sessions(project_path.as_deref())
        }
    };

    for session_id in affected_sessions {
        let _ = session_state.restart_session(&app, &session_id, None, None);
    }

    Ok(())
}
