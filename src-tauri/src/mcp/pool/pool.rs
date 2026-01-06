use std::collections::HashMap;
use std::path::PathBuf;
use std::os::unix::net::UnixStream;
use std::sync::Arc;
use std::time::{Duration, Instant};

use parking_lot::RwLock;

use crate::diagnostics;
use super::socket_proxy::SocketProxy;
use super::types::ServerStatus;

#[derive(Debug, Clone)]
pub struct PoolConfig {
    pub enabled: bool,
    pub pool_all: bool,
    pub exclude_mcps: Vec<String>,
    pub pool_mcps: Vec<String>,
    pub fallback_stdio: bool,
    pub start_on_demand: bool,
}

#[derive(Debug, Clone)]
pub struct ProxyInfo {
    pub name: String,
    pub socket_path: String,
    pub status: String,
    pub clients: usize,
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

    pub fn config(&self) -> &PoolConfig {
        &self.config
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
        let _ = proxy.start();
        self.proxies.write().insert(name.to_string(), proxy);
    }

    pub fn discover_existing_sockets(&self) -> usize {
        let mut discovered = 0;
        if let Ok(entries) = std::fs::read_dir("/tmp") {
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

    pub fn list_servers(&self) -> Vec<ProxyInfo> {
        let proxies = self.proxies.read();
        proxies
            .values()
            .map(|proxy| ProxyInfo {
                name: proxy.name(),
                socket_path: proxy.socket_path().display().to_string(),
                status: proxy.status().as_str().to_string(),
                clients: proxy.client_count(),
            })
            .collect()
    }

    pub fn shutdown(&self) {
        let mut proxies = self.proxies.write();
        for proxy in proxies.values() {
            let _ = proxy.stop();
        }
        proxies.clear();
    }

    pub fn wait_for_socket(&self, name: &str, timeout: Duration) -> bool {
        let deadline = Instant::now() + timeout;
        while Instant::now() < deadline {
            if self.is_running(name) {
                return true;
            }
            std::thread::sleep(Duration::from_millis(100));
        }
        false
    }
}

pub fn socket_path_for(name: &str) -> PathBuf {
    PathBuf::from(format!("/tmp/agentterm-mcp-{}.sock", name))
}

pub fn socket_name_from_path(path: &PathBuf) -> Option<String> {
    let file_name = path.file_name()?.to_string_lossy();
    if !file_name.starts_with("agentterm-mcp-") || !file_name.ends_with(".sock") {
        return None;
    }
    let trimmed = file_name.trim_start_matches("agentterm-mcp-");
    Some(trimmed.trim_end_matches(".sock").to_string())
}

pub fn socket_alive(path: &PathBuf) -> bool {
    if !path.exists() {
        return false;
    }
    UnixStream::connect(path).is_ok()
}
