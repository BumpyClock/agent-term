use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use parking_lot::RwLock;

use super::socket_proxy::SocketProxy;
use super::types::{McpServerStatus, PoolStatusResponse, ServerStatus};
use crate::config::get_agent_term_mcp_run_dir;
use crate::diagnostics;
use agentterm_shared::socket_path::socket_path_for;

#[derive(Debug, Clone)]
pub struct PoolConfig {
    pub enabled: bool,
    pub pool_all: bool,
    pub exclude_mcps: Vec<String>,
    pub pool_mcps: Vec<String>,
}

pub struct Pool {
    proxies: RwLock<HashMap<String, Arc<SocketProxy>>>,
    config: PoolConfig,
}

impl Pool {
    pub fn new(config: PoolConfig) -> Self {
        Self {
            proxies: RwLock::new(HashMap::new()),
            config,
        }
    }

    pub fn should_pool(&self, name: &str) -> bool {
        if !self.config.enabled {
            return false;
        }
        if self.config.pool_all {
            return !self
                .config
                .exclude_mcps
                .iter()
                .any(|exclude| exclude == name);
        }
        self.config.pool_mcps.iter().any(|entry| entry == name)
    }

    pub fn is_running(&self, name: &str) -> bool {
        let proxies = self.proxies.read();
        if let Some(proxy) = proxies.get(name) {
            if proxy.status() == ServerStatus::Running {
                return socket_alive(&proxy.socket_path());
            }
        }
        false
    }

    pub fn socket_path(&self, name: &str) -> Option<PathBuf> {
        let proxies = self.proxies.read();
        proxies.get(name).map(|proxy| proxy.socket_path())
    }

    pub fn start(
        &self,
        name: &str,
        command: String,
        args: Vec<String>,
        env: HashMap<String, String>,
    ) -> std::io::Result<()> {
        {
            let proxies = self.proxies.read();
            if proxies.contains_key(name) {
                return Ok(());
            }
        }

        let socket_path = socket_path_for(name);
        let proxy = Arc::new(SocketProxy::new(
            name.to_string(),
            socket_path,
            command,
            args,
            env,
            true,
        ));
        proxy.start()?;
        self.proxies.write().insert(name.to_string(), proxy);
        Ok(())
    }

    pub fn register_external_socket(&self, name: &str, socket_path: PathBuf) {
        let proxy = Arc::new(SocketProxy::new(
            name.to_string(),
            socket_path,
            String::new(),
            Vec::new(),
            HashMap::new(),
            false,
        ));
        if let Err(e) = proxy.start() {
            diagnostics::log(format!(
                "pool_external_socket_start_failed name={} error={}",
                name, e
            ));
        }
        self.proxies.write().insert(name.to_string(), proxy);
    }

    pub fn discover_existing_sockets(&self) -> usize {
        let mut discovered = 0;
        if cfg!(windows) {
            return 0;
        }
        let base = get_agent_term_mcp_run_dir().unwrap_or_else(|_| PathBuf::from("/tmp"));
        if let Ok(entries) = std::fs::read_dir(base) {
            for entry in entries.flatten() {
                let path = entry.path();
                if let Some(name) = socket_name_from_path(&path) {
                    if self.proxies.read().contains_key(&name) {
                        continue;
                    }
                    if socket_alive(&path) {
                        self.register_external_socket(&name, path.clone());
                        discovered += 1;
                        diagnostics::log(format!(
                            "pool_socket_discovered name={} socket={}",
                            name,
                            path.display()
                        ));
                    }
                }
            }
        }
        discovered
    }

    pub fn shutdown(&self) {
        let mut proxies = self.proxies.write();
        for proxy in proxies.values() {
            let _ = proxy.stop();
        }
        proxies.clear();
    }

    pub async fn wait_for_socket(&self, name: &str, timeout: Duration) -> bool {
        // Check if already running
        if self.is_running(name) {
            return true;
        }

        // Get the ready notifier for this proxy
        let notify = {
            let proxies = self.proxies.read();
            match proxies.get(name) {
                Some(proxy) => proxy.ready_notifier(),
                None => return false,
            }
        };

        // Wait with timeout for the socket to become ready
        match tokio::time::timeout(timeout, notify.notified()).await {
            Ok(_) => self.is_running(name),
            Err(_) => false,
        }
    }

    /// Get status of all servers in the pool
    pub fn get_status(&self) -> PoolStatusResponse {
        let proxies = self.proxies.read();
        let servers: Vec<McpServerStatus> = proxies
            .iter()
            .map(|(name, proxy)| McpServerStatus {
                name: name.clone(),
                status: proxy.status(),
                socket_path: proxy.socket_path().display().to_string(),
                uptime_seconds: proxy.uptime_seconds(),
                connection_count: proxy.connection_count(),
                owned: proxy.is_owned(),
            })
            .collect();

        PoolStatusResponse {
            enabled: self.config.enabled,
            server_count: servers.len(),
            servers,
        }
    }

    /// Restart a specific MCP server by name
    pub async fn restart(&self, name: &str) -> std::io::Result<bool> {
        let (proxy, exit_rx) = {
            let proxies = self.proxies.read();
            match proxies.get(name).cloned() {
                Some(p) => (p.clone(), p.take_exit_receiver()),
                None => return Ok(false),
            }
        };

        if !proxy.is_owned() {
            return Ok(false);
        }

        proxy.stop()?;

        // Wait for actual process exit instead of arbitrary sleep
        if let Some(rx) = exit_rx {
            let _ = rx.await;
        }

        proxy.start()?;
        Ok(true)
    }

    /// Stop a specific MCP server by name
    pub fn stop_server(&self, name: &str) -> std::io::Result<bool> {
        let proxy = {
            let proxies = self.proxies.read();
            proxies.get(name).cloned()
        };

        if let Some(proxy) = proxy {
            proxy.stop()?;
            Ok(true)
        } else {
            Ok(false)
        }
    }
}

pub fn socket_name_from_path(path: &Path) -> Option<String> {
    let file_name = path.file_name()?.to_string_lossy();
    if !file_name.starts_with("agentterm-mcp-") || !file_name.ends_with(".sock") {
        return None;
    }
    let trimmed = file_name.trim_start_matches("agentterm-mcp-");
    Some(trimmed.trim_end_matches(".sock").to_string())
}

#[cfg(unix)]
pub fn socket_alive(path: &PathBuf) -> bool {
    std::os::unix::net::UnixStream::connect(path).is_ok()
}

#[cfg(windows)]
pub fn socket_alive(path: &PathBuf) -> bool {
    let path_str = path.to_string_lossy().to_string();
    tokio::net::windows::named_pipe::ClientOptions::new()
        .open(path_str)
        .is_ok()
}
