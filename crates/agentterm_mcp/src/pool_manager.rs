use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::{Arc, OnceLock};
use std::time::Duration;

use parking_lot::Mutex;

use crate::diagnostics;

use super::config::{MCPDef, UserConfig};
use super::error::{McpError, McpResult};
use super::pool::types::PoolStatusResponse;
use super::pool::{socket_alive, socket_path_for, Pool, PoolConfig};

static GLOBAL_POOL: OnceLock<Mutex<Option<Arc<Pool>>>> = OnceLock::new();

fn global_pool_state() -> &'static Mutex<Option<Arc<Pool>>> {
    GLOBAL_POOL.get_or_init(|| Mutex::new(None))
}

pub fn initialize_global_pool(config: &UserConfig) -> McpResult<Option<Arc<Pool>>> {
    if !config.mcp_pool.enabled {
        return Ok(None);
    }

    let mut state = global_pool_state().lock();
    if let Some(pool) = state.as_ref() {
        return Ok(Some(pool.clone()));
    }

    let pool = Arc::new(Pool::new(PoolConfig {
        enabled: config.mcp_pool.enabled,
        pool_all: config.mcp_pool.pool_all,
        exclude_mcps: config.mcp_pool.exclude_mcps.clone(),
        pool_mcps: config.mcp_pool.pool_mcps.clone(),
    }));

    let discovered = pool.discover_existing_sockets();
    if discovered > 0 {
        diagnostics::log(format!("pool_sockets_discovered count={}", discovered));
    }

    if config.mcp_pool.auto_start {
        let available = config.mcps.clone();
        diagnostics::log("pool_auto_start begin");
        start_pool_mcps(&pool, &available)?;
        diagnostics::log("pool_auto_start done");
    }

    *state = Some(pool.clone());
    Ok(Some(pool))
}

pub fn ensure_global_pool(config: &UserConfig) -> McpResult<Option<Arc<Pool>>> {
    initialize_global_pool(config)
}

pub fn shutdown_global_pool() -> McpResult<()> {
    let mut state = global_pool_state().lock();
    if let Some(pool) = state.as_ref() {
        pool.shutdown();
    }
    *state = None;
    Ok(())
}

pub async fn wait_for_socket_ready(pool: &Pool, name: &str, timeout: Duration) -> bool {
    pool.wait_for_socket(name, timeout).await
}

pub fn get_external_socket_path(name: &str) -> Option<PathBuf> {
    let path = socket_path_for(name);
    if socket_alive(&path) {
        return Some(path);
    }
    None
}

pub fn start_pool_mcp(pool: &Pool, name: &str, def: &MCPDef) -> McpResult<()> {
    if !pool.should_pool(name) {
        return Ok(());
    }
    if pool.is_running(name) {
        return Ok(());
    }
    if !def.url.is_empty() {
        return Ok(());
    }
    if def.command.is_empty() {
        return Err(McpError::InvalidConfig(format!(
            "mcp command missing for {}",
            name
        )));
    }
    pool.start(
        name,
        def.command.clone(),
        def.args.clone(),
        def.env.clone(),
    )
    .map_err(|err| McpError::IoError(err.to_string()))
}

fn start_pool_mcps(pool: &Pool, mcps: &HashMap<String, MCPDef>) -> McpResult<()> {
    for (name, def) in mcps {
        if def.command.is_empty() && def.url.is_empty() {
            continue;
        }
        if !pool.should_pool(name) {
            continue;
        }
        if pool.is_running(name) {
            continue;
        }
        if !def.url.is_empty() {
            continue;
        }
        if let Err(err) = start_pool_mcp(pool, name, def) {
            let msg = err.to_string().replace('.', "");
            diagnostics::log(format!("pool_proxy_start_failed name={} error={}", name, msg));
        }
    }
    Ok(())
}

/// Get the current global pool instance (if enabled)
pub fn get_global_pool() -> Option<Arc<Pool>> {
    global_pool_state().lock().clone()
}

/// Get status of all pooled MCP servers
pub fn get_pool_status() -> PoolStatusResponse {
    match get_global_pool() {
        Some(pool) => pool.get_status(),
        None => PoolStatusResponse {
            enabled: false,
            server_count: 0,
            servers: vec![],
        },
    }
}

/// Restart a specific MCP server in the pool
pub async fn restart_pool_server(name: &str) -> McpResult<bool> {
    match get_global_pool() {
        Some(pool) => pool.restart(name).await.map_err(|e| McpError::IoError(e.to_string())),
        None => Ok(false),
    }
}

/// Stop a specific MCP server in the pool
pub fn stop_pool_server(name: &str) -> McpResult<bool> {
    match get_global_pool() {
        Some(pool) => pool.stop_server(name).map_err(|e| McpError::IoError(e.to_string())),
        None => Ok(false),
    }
}
