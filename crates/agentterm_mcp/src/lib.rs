pub mod diagnostics;

pub mod config;
mod error;
pub mod manager;
pub mod pool;
pub mod pool_manager;
mod proxy;

pub use config::{MCPDef, get_claude_config_dir};
pub use error::{McpError, McpResult};
pub use manager::{McpManager, McpScope};

// Pool management exports
pub use pool::types::{McpServerStatus, PoolStatusResponse, ServerStatus};
pub use pool_manager::{get_pool_status, restart_pool_server, stop_pool_server};

pub async fn build_mcp_manager() -> McpResult<McpManager> {
    let manager = McpManager::new();
    manager.create_example_config().await?;
    Ok(manager)
}
