#[cfg(unix)]
mod pool;
#[cfg(unix)]
mod socket_proxy;
#[cfg(unix)]
mod types;

#[cfg(unix)]
pub use pool::{socket_alive, socket_path_for, Pool, PoolConfig};

#[cfg(not(unix))]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ServerStatus {
    Stopped,
    Starting,
    Running,
    Failed,
}

#[cfg(not(unix))]
#[derive(Debug, Clone)]
pub struct PoolConfig {
    pub enabled: bool,
    pub pool_all: bool,
    pub exclude_mcps: Vec<String>,
    pub pool_mcps: Vec<String>,
}

#[cfg(not(unix))]
pub struct Pool;

#[cfg(not(unix))]
impl Pool {
    pub fn new(config: PoolConfig) -> Self {
        let _ = config;
        Self
    }

    pub fn should_pool(&self, _name: &str) -> bool {
        false
    }

    pub fn is_running(&self, _name: &str) -> bool {
        false
    }

    pub fn socket_path(&self, _name: &str) -> Option<std::path::PathBuf> {
        None
    }

    pub fn start(
        &self,
        _name: &str,
        _command: String,
        _args: Vec<String>,
        _env: std::collections::HashMap<String, String>,
    ) -> std::io::Result<()> {
        Ok(())
    }

    pub fn discover_existing_sockets(&self) -> usize {
        0
    }

    pub fn shutdown(&self) {}

    pub fn wait_for_socket(&self, _name: &str, _timeout: std::time::Duration) -> bool {
        false
    }
}

#[cfg(not(unix))]
pub fn socket_path_for(name: &str) -> std::path::PathBuf {
    std::path::PathBuf::from(format!("\\\\.\\pipe\\agentterm-mcp-{}", name))
}

#[cfg(not(unix))]
pub fn socket_alive(_path: &std::path::PathBuf) -> bool {
    false
}
