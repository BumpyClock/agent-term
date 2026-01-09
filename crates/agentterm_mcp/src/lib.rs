pub mod diagnostics;

pub mod config;
mod error;
pub mod manager;
pub mod pool;
mod pool_manager;
mod proxy;

pub use config::{MCPDef, get_claude_config_dir};
pub use error::{McpError, McpResult};
pub use manager::{McpManager, McpScope};

pub async fn build_mcp_manager() -> McpResult<McpManager> {
    let manager = McpManager::new();
    manager.create_example_config().await?;
    Ok(manager)
}

