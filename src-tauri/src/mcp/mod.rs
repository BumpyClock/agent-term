//! MCP (Model Context Protocol) management module.
//!
//! Provides configuration and attachment management for MCP servers.
//! Supports three scopes: Global (Agent Term managed config), Project (project .mcp.json),
//! and Local (project .mcp.json).

pub(crate) mod config;
mod error;
mod manager;
pub mod pool;
pub(crate) mod proxy;
pub(crate) mod pool_manager;

pub use config::MCPDef;
pub use config::get_claude_config_dir;
pub use error::McpResult;
pub use manager::{McpManager, McpScope};

use pool::types::PoolStatusResponse;
use serde::Serialize;
use tauri::State;
use std::collections::HashMap;

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

#[derive(Debug, Clone, Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct McpPoolSettingsDto {
    pub enabled: bool,
    pub auto_start: bool,
    pub port_start: i32,
    pub port_end: i32,
    pub start_on_demand: bool,
    pub shutdown_on_exit: bool,
    pub pool_mcps: Vec<String>,
    pub fallback_to_stdio: bool,
    pub show_pool_status: bool,
    pub pool_all: bool,
    pub exclude_mcps: Vec<String>,
}

impl From<config::MCPPoolSettings> for McpPoolSettingsDto {
    fn from(settings: config::MCPPoolSettings) -> Self {
        Self {
            enabled: settings.enabled,
            auto_start: settings.auto_start,
            port_start: settings.port_start,
            port_end: settings.port_end,
            start_on_demand: settings.start_on_demand,
            shutdown_on_exit: settings.shutdown_on_exit,
            pool_mcps: settings.pool_mcps,
            fallback_to_stdio: settings.fallback_to_stdio,
            show_pool_status: settings.show_pool_status,
            pool_all: settings.pool_all,
            exclude_mcps: settings.exclude_mcps,
        }
    }
}

impl From<McpPoolSettingsDto> for config::MCPPoolSettings {
    fn from(settings: McpPoolSettingsDto) -> Self {
        Self {
            enabled: settings.enabled,
            auto_start: settings.auto_start,
            port_start: settings.port_start,
            port_end: settings.port_end,
            start_on_demand: settings.start_on_demand,
            shutdown_on_exit: settings.shutdown_on_exit,
            pool_mcps: settings.pool_mcps,
            fallback_to_stdio: settings.fallback_to_stdio,
            show_pool_status: settings.show_pool_status,
            pool_all: settings.pool_all,
            exclude_mcps: settings.exclude_mcps,
        }
    }
}

#[derive(Debug, Clone, Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct McpSettings {
    pub mcps: HashMap<String, MCPDef>,
    pub mcp_pool: McpPoolSettingsDto,
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
pub async fn build_mcp_manager() -> McpResult<McpManager> {
    let manager = McpManager::new();
    manager.create_example_config().await?;
    Ok(manager)
}

/// List all available MCPs from user config
#[tauri::command(rename_all = "camelCase")]
pub async fn mcp_list(state: State<'_, McpManager>) -> Result<Vec<McpInfo>, String> {
    let mcps = state.get_available_mcps().await.map_err(|e| e.to_string())?;
    let mut result: Vec<McpInfo> = mcps.into_iter().map(McpInfo::from).collect();
    result.sort_by(|a, b| a.name.cmp(&b.name));
    Ok(result)
}

#[tauri::command(rename_all = "camelCase")]
pub async fn mcp_get_settings(state: State<'_, McpManager>) -> Result<McpSettings, String> {
    let config = state.load_config().await.map_err(|e| e.to_string())?;
    Ok(McpSettings {
        mcps: config.mcps,
        mcp_pool: McpPoolSettingsDto::from(config.mcp_pool),
    })
}

#[tauri::command(rename_all = "camelCase")]
pub async fn mcp_set_settings(
    state: State<'_, McpManager>,
    settings: McpSettings,
) -> Result<(), String> {
    let mut config = state.load_config().await.map_err(|e| e.to_string())?;
    config.mcps = settings.mcps;
    config.mcp_pool = config::MCPPoolSettings::from(settings.mcp_pool);
    state.write_config(&config).await.map_err(|e| e.to_string())?;

    let _ = pool_manager::shutdown_global_pool();
    if config.mcp_pool.enabled {
        pool_manager::initialize_global_pool(&config).map_err(|e| e.to_string())?;
    }

    Ok(())
}

/// Get attached MCPs for a scope
#[tauri::command(rename_all = "camelCase")]
pub async fn mcp_attached(
    state: State<'_, McpManager>,
    scope: McpScope,
    project_path: Option<String>,
) -> Result<Vec<String>, String> {
    state
        .get_attached_mcps(scope, project_path.as_deref())
        .await
        .map_err(|e| e.to_string())
}

/// Attach an MCP to a scope
#[tauri::command(rename_all = "camelCase")]
pub async fn mcp_attach(
    app: tauri::AppHandle,
    mcp_state: State<'_, McpManager>,
    session_state: State<'_, crate::session::SessionManager>,
    scope: McpScope,
    project_path: Option<String>,
    mcp_name: String,
) -> Result<(), String> {
    mcp_state
        .attach_mcp(scope, project_path.as_deref(), &mcp_name)
        .await
        .map_err(|e| e.to_string())?;

    let affected_sessions = match scope {
        McpScope::Global => session_state.find_running_ai_sessions(None),
        McpScope::Project | McpScope::Local => {
            session_state.find_running_ai_sessions(project_path.as_deref())
        }
    };

    for session_id in affected_sessions {
        let _ = session_state.restart_session_with_mcp(&app, &session_id, None, None, &mcp_state);
    }

    Ok(())
}

/// Detach an MCP from a scope
#[tauri::command(rename_all = "camelCase")]
pub async fn mcp_detach(
    app: tauri::AppHandle,
    mcp_state: State<'_, McpManager>,
    session_state: State<'_, crate::session::SessionManager>,
    scope: McpScope,
    project_path: Option<String>,
    mcp_name: String,
) -> Result<(), String> {
    mcp_state
        .detach_mcp(scope, project_path.as_deref(), &mcp_name)
        .await
        .map_err(|e| e.to_string())?;

    let affected_sessions = match scope {
        McpScope::Global => session_state.find_running_ai_sessions(None),
        McpScope::Project | McpScope::Local => {
            session_state.find_running_ai_sessions(project_path.as_deref())
        }
    };

    for session_id in affected_sessions {
        let _ = session_state.restart_session_with_mcp(&app, &session_id, None, None, &mcp_state);
    }

    Ok(())
}

/// Get the current status of all pooled MCP servers
#[tauri::command(rename_all = "camelCase")]
pub async fn mcp_pool_status(state: State<'_, McpManager>) -> Result<PoolStatusResponse, String> {
    let config = state.load_config().await.map_err(|e| e.to_string())?;
    pool_manager::ensure_global_pool(&config).map_err(|e| e.to_string())?;
    Ok(pool_manager::get_pool_status())
}

/// Restart a specific MCP server in the pool
#[tauri::command(rename_all = "camelCase")]
pub async fn mcp_restart_server(name: String) -> Result<bool, String> {
    pool_manager::restart_pool_server(&name).await.map_err(|e| e.to_string())
}

/// Stop a specific MCP server in the pool
#[tauri::command(rename_all = "camelCase")]
pub async fn mcp_stop_server(name: String) -> Result<bool, String> {
    pool_manager::stop_pool_server(&name).map_err(|e| e.to_string())
}

/// Start a specific MCP server in the pool (if configured)
#[tauri::command(rename_all = "camelCase")]
pub async fn mcp_start_server(
    state: State<'_, McpManager>,
    name: String,
) -> Result<bool, String> {
    let config = state.load_config().await.map_err(|e| e.to_string())?;
    let pool = match pool_manager::get_global_pool() {
        Some(p) => p,
        None => return Ok(false),
    };

    if let Some(def) = config.mcps.get(&name) {
        pool_manager::start_pool_mcp(&pool, &name, def).map_err(|e| e.to_string())?;
        Ok(true)
    } else {
        Ok(false)
    }
}
