use super::config::{
    get_config_path,
    get_managed_global_mcp_path,
    get_managed_project_mcp_path,
    MCPServerConfig,
    McpJsonConfig,
    UserConfig,
};
use super::error::{McpError, McpResult};
use super::pool_manager;
use super::proxy;
use crate::diagnostics;
use parking_lot::Mutex;
use serde_json;
use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;
use tokio::fs;
use tokio::io::AsyncWriteExt;

/// MCP attachment scope
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum McpScope {
    /// Global scope (Agent Term managed MCP config)
    Global,
    /// Project scope (project .mcp.json)
    Project,
    /// Local scope (project .mcp.json)
    Local,
}

/// MCP Manager handles configuration and attachment of MCP servers
#[derive(Clone)]
pub struct McpManager {
    /// Cached user configuration
    config_cache: Arc<Mutex<Option<UserConfig>>>,
}

impl McpManager {
    /// Create a new MCP manager
    pub fn new() -> Self {
        Self {
            config_cache: Arc::new(Mutex::new(None)),
        }
    }

    /// Load user configuration from ~/.agent-term/config.toml
    /// Returns cached config after first load
    pub async fn load_config(&self) -> McpResult<UserConfig> {
        // Check cache first
        {
            let cache = self.config_cache.lock();
            if let Some(ref config) = *cache {
                return Ok(config.clone());
            }
        }

        // Load from file
        let config_path = get_config_path()?;

        // If config doesn't exist, return default
        if !config_path.exists() {
            let default_config = UserConfig::default();
            // Cache the default
            *self.config_cache.lock() = Some(default_config.clone());
            return Ok(default_config);
        }

        // Read and parse config (async)
        let contents = fs::read_to_string(&config_path)
            .await
            .map_err(|e| McpError::ConfigReadError(format!("{}: {}", config_path.display(), e)))?;

        let config: UserConfig = toml::from_str(&contents)
            .map_err(|e| McpError::ConfigParseError(format!("{}: {}", config_path.display(), e)))?;

        // Cache the config
        *self.config_cache.lock() = Some(config.clone());

        Ok(config)
    }

    /// Write user configuration to ~/.agent-term/config.toml
    pub async fn write_config(&self, config: &UserConfig) -> McpResult<()> {
        let config_path = get_config_path()?;

        // Ensure directory exists
        if let Some(parent) = config_path.parent() {
            fs::create_dir_all(parent)
                .await
                .map_err(|e| McpError::ConfigWriteError(format!("create_dir_all: {}", e)))?;
        }

        // Serialize to TOML
        let toml_str = toml::to_string_pretty(config)
            .map_err(|e| McpError::ConfigWriteError(format!("toml serialization: {}", e)))?;

        // Atomic write: write to temp file, then rename
        let temp_path = config_path.with_extension("toml.tmp");
        {
            let mut file = fs::File::create(&temp_path)
                .await
                .map_err(|e| McpError::ConfigWriteError(format!("create tmp: {}", e)))?;
            file.write_all(toml_str.as_bytes())
                .await
                .map_err(|e| McpError::ConfigWriteError(format!("write tmp: {}", e)))?;
        }

        // Atomic rename
        fs::rename(&temp_path, &config_path)
            .await
            .map_err(|e| McpError::ConfigWriteError(format!("rename: {}", e)))?;

        // Update cache
        *self.config_cache.lock() = Some(config.clone());

        Ok(())
    }

    /// Initialize MCP pool on startup if auto_start is enabled
    /// This should be called early in the app lifecycle to start pooled MCPs
    pub async fn initialize_pool(&self) -> McpResult<()> {
        let config = self.load_config().await?;

        if !config.mcp_pool.enabled {
            diagnostics::log("pool_init_skipped reason=disabled");
            return Ok(());
        }

        if !config.mcp_pool.auto_start {
            diagnostics::log("pool_init_skipped reason=auto_start_disabled");
            return Ok(());
        }

        diagnostics::log("pool_init_starting");

        match pool_manager::initialize_global_pool(&config) {
            Ok(Some(_)) => {
                diagnostics::log("pool_init_success");
            }
            Ok(None) => {
                diagnostics::log("pool_init_returned_none");
            }
            Err(e) => {
                diagnostics::log(format!("pool_init_failed error={}", e));
                return Err(e);
            }
        }

        Ok(())
    }

    /// Get all available MCP definitions from config
    pub async fn get_available_mcps(&self) -> McpResult<HashMap<String, super::config::MCPDef>> {
        let config = self.load_config().await?;
        Ok(config.mcps)
    }

    /// Get MCP definition by name
    pub async fn get_mcp_def(&self, name: &str) -> McpResult<super::config::MCPDef> {
        let config = self.load_config().await?;
        config
            .mcps
            .get(name)
            .cloned()
            .ok_or_else(|| McpError::MCPNotFound(name.to_string()))
    }

    /// Get the MCP config file path for launching tools (if it exists)
    /// Returns the path to the AgentTerm-managed project config if it exists
    pub fn get_project_mcp_config_path(&self, project_path: &str) -> Option<PathBuf> {
        if project_path.is_empty() {
            return None;
        }
        let path = get_managed_project_mcp_path(project_path).ok()?;
        if path.exists() {
            Some(path)
        } else {
            None
        }
    }

    /// Get attached MCPs for a given scope
    ///
    /// # Arguments
    /// * `scope` - Attachment scope (Global, Project, or Local)
    /// * `project_path` - Project directory path (required for Project and Local scopes)
    pub async fn get_attached_mcps(
        &self,
        scope: McpScope,
        project_path: Option<&str>,
    ) -> McpResult<Vec<String>> {
        match scope {
            McpScope::Global => self.get_global_mcps().await,
            McpScope::Project => {
                let path = project_path
                    .ok_or_else(|| McpError::InvalidInput("project_path required".to_string()))?;
                self.get_local_mcps(path).await
            }
            McpScope::Local => {
                let path = project_path
                    .ok_or_else(|| McpError::InvalidInput("project_path required".to_string()))?;
                self.get_local_mcps(path).await
            }
        }
    }

    /// Get globally attached MCPs (Agent Term managed MCP config)
    async fn get_global_mcps(&self) -> McpResult<Vec<String>> {
        let config_path = get_managed_global_mcp_path()?;

        if !config_path.exists() {
            return Ok(Vec::new());
        }

        let contents = fs::read_to_string(&config_path)
            .await
            .map_err(|e| {
                diagnostics::log(format!(
                    "mcp_global_read_failed path={} error={}",
                    config_path.display(),
                    e
                ));
                McpError::IoError(format!("{}: {}", config_path.display(), e))
            })?;

        let config: McpJsonConfig = serde_json::from_str(&contents).map_err(|e| {
            diagnostics::log(format!(
                "mcp_global_parse_failed path={} error={}",
                config_path.display(),
                e
            ));
            McpError::ConfigParseError(format!("{}: {}", config_path.display(), e))
        })?;

        let mut names: Vec<String> = config.mcp_servers.keys().cloned().collect();
        names.sort();
        Ok(names)
    }

    /// Get local MCPs (AgentTerm-managed project config)
    async fn get_local_mcps(&self, project_path: &str) -> McpResult<Vec<String>> {
        let managed_path = get_managed_project_mcp_path(project_path)?;
        if managed_path.exists() {
            let contents = fs::read_to_string(&managed_path).await.map_err(|e| {
                diagnostics::log(format!(
                    "mcp_managed_read_failed path={} error={}",
                    managed_path.display(),
                    e
                ));
                McpError::IoError(format!("{}: {}", managed_path.display(), e))
            })?;

            let config: McpJsonConfig = serde_json::from_str(&contents).map_err(|e| {
                diagnostics::log(format!(
                    "mcp_managed_parse_failed path={} error={}",
                    managed_path.display(),
                    e
                ));
                McpError::ConfigParseError(format!("{}: {}", managed_path.display(), e))
            })?;

            let mut names: Vec<String> = config.mcp_servers.keys().cloned().collect();
            names.sort();
            return Ok(names);
        }

        Ok(Vec::new())
    }

    /// Attach an MCP to a scope
    ///
    /// # Arguments
    /// * `scope` - Attachment scope (Global, Project, or Local)
    /// * `project_path` - Project directory path (required for Project and Local scopes)
    /// * `mcp_name` - Name of MCP to attach
    pub async fn attach_mcp(
        &self,
        scope: McpScope,
        project_path: Option<&str>,
        mcp_name: &str,
    ) -> McpResult<()> {
        // Verify MCP exists
        let mcp_def = self.get_mcp_def(mcp_name).await?;

        match scope {
            McpScope::Global => self.attach_mcp_global(mcp_name, &mcp_def).await,
            McpScope::Project => {
                let path = project_path.ok_or_else(|| {
                    McpError::InvalidInput("project_path required for project scope".to_string())
                })?;
                self.attach_mcp_local(path, mcp_name, &mcp_def).await
            }
            McpScope::Local => {
                let path = project_path.ok_or_else(|| {
                    McpError::InvalidInput("project_path required for local scope".to_string())
                })?;
                self.attach_mcp_local(path, mcp_name, &mcp_def).await
            }
        }
    }

    /// Attach MCP to global scope
    async fn attach_mcp_global(&self, mcp_name: &str, mcp_def: &super::config::MCPDef) -> McpResult<()> {
        let config_path = get_managed_global_mcp_path()?;
        let mut config = if config_path.exists() {
            let contents = fs::read_to_string(&config_path)
                .await
                .map_err(|e| McpError::IoError(e.to_string()))?;
            serde_json::from_str(&contents)
                .map_err(|e| McpError::ConfigParseError(e.to_string()))?
        } else {
            McpJsonConfig::default()
        };

        let server_config = self.mcp_def_to_server_config(mcp_name, mcp_def).await?;
        config
            .mcp_servers
            .insert(mcp_name.to_string(), server_config);

        self.write_mcp_json(&config_path, &config).await
    }

    /// Attach MCP to local scope (AgentTerm-managed project config)
    async fn attach_mcp_local(
        &self,
        project_path: &str,
        mcp_name: &str,
        mcp_def: &super::config::MCPDef,
    ) -> McpResult<()> {
        let mcp_json_path = get_managed_project_mcp_path(project_path)?;

        // Read existing managed config or create new
        let mut config = if mcp_json_path.exists() {
            let contents = fs::read_to_string(&mcp_json_path)
                .await
                .map_err(|e| McpError::IoError(e.to_string()))?;
            serde_json::from_str(&contents)
                .map_err(|e| McpError::ConfigParseError(e.to_string()))?
        } else {
            McpJsonConfig::default()
        };

        // Convert MCPDef to MCPServerConfig
        let server_config = self.mcp_def_to_server_config(mcp_name, mcp_def).await?;

        // Add to mcpServers
        config
            .mcp_servers
            .insert(mcp_name.to_string(), server_config);

        // Write back atomically
        self.write_mcp_json(&mcp_json_path, &config).await
    }

    /// Detach an MCP from a scope
    ///
    /// # Arguments
    /// * `scope` - Attachment scope (Global, Project, or Local)
    /// * `project_path` - Project directory path (required for Project and Local scopes)
    /// * `mcp_name` - Name of MCP to detach
    pub async fn detach_mcp(
        &self,
        scope: McpScope,
        project_path: Option<&str>,
        mcp_name: &str,
    ) -> McpResult<()> {
        match scope {
            McpScope::Global => self.detach_mcp_global(mcp_name).await,
            McpScope::Project => {
                let path = project_path.ok_or_else(|| {
                    McpError::InvalidInput("project_path required for project scope".to_string())
                })?;
                self.detach_mcp_local(path, mcp_name).await
            }
            McpScope::Local => {
                let path = project_path.ok_or_else(|| {
                    McpError::InvalidInput("project_path required for local scope".to_string())
                })?;
                self.detach_mcp_local(path, mcp_name).await
            }
        }
    }

    /// Detach MCP from global scope
    async fn detach_mcp_global(&self, mcp_name: &str) -> McpResult<()> {
        let config_path = get_managed_global_mcp_path()?;

        if !config_path.exists() {
            return Ok(()); // Nothing to detach
        }
        let contents = fs::read_to_string(&config_path)
            .await
            .map_err(|e| McpError::IoError(e.to_string()))?;
        let mut config: McpJsonConfig = serde_json::from_str(&contents)
            .map_err(|e| McpError::ConfigParseError(e.to_string()))?;
        config.mcp_servers.remove(mcp_name);
        self.write_mcp_json(&config_path, &config).await
    }

    /// Detach MCP from local scope (AgentTerm-managed project config)
    async fn detach_mcp_local(&self, project_path: &str, mcp_name: &str) -> McpResult<()> {
        let mcp_json_path = get_managed_project_mcp_path(project_path)?;

        if !mcp_json_path.exists() {
            return Ok(()); // Nothing to detach
        }

        let contents = fs::read_to_string(&mcp_json_path)
            .await
            .map_err(|e| McpError::IoError(e.to_string()))?;

        let mut config: McpJsonConfig = serde_json::from_str(&contents)
            .map_err(|e| McpError::ConfigParseError(e.to_string()))?;

        config.mcp_servers.remove(mcp_name);

        self.write_mcp_json(&mcp_json_path, &config).await
    }

    /// Convert MCPDef to MCPServerConfig
    async fn mcp_def_to_server_config(
        &self,
        mcp_name: &str,
        mcp_def: &super::config::MCPDef,
    ) -> McpResult<MCPServerConfig> {
        if !mcp_def.url.is_empty() {
            let transport = if mcp_def.transport.is_empty() {
                "http".to_string()
            } else {
                mcp_def.transport.clone()
            };
            return Ok(MCPServerConfig {
                server_type: Some(transport),
                url: mcp_def.url.clone(),
                ..Default::default()
            });
        }

        let config = self.load_config().await?;
        if config.mcp_pool.enabled {
            let pool = pool_manager::ensure_global_pool(&config)?;
            if let Some(pool) = pool.as_ref() {
                if pool.should_pool(mcp_name) {
                    if config.mcp_pool.start_on_demand {
                        pool_manager::start_pool_mcp(pool, mcp_name, mcp_def)?;
                    }

                    if !pool.is_running(mcp_name) {
                        let _ = pool_manager::wait_for_socket_ready(
                            pool,
                            mcp_name,
                            Duration::from_secs(3),
                        )
                        .await;
                    }

                    if pool.is_running(mcp_name) {
                        if let Some(socket_path) = pool.socket_path(mcp_name) {
                            diagnostics::log(format!(
                                "mcp_pool_socket_used name={} socket={}",
                                mcp_name,
                                socket_path.display()
                            ));
                            return Ok(MCPServerConfig {
                                server_type: Some("stdio".to_string()),
                                command: proxy::proxy_command(),
                                args: vec!["--name".to_string(), mcp_name.to_string()],
                                ..Default::default()
                            });
                        }
                    }

                    if !config.mcp_pool.fallback_to_stdio {
                        return Err(McpError::InvalidConfig(format!(
                            "mcp socket not ready and fallback disabled for {}",
                            mcp_name
                        )));
                    }
                }
            } else if let Some(socket_path) = pool_manager::get_external_socket_path(mcp_name) {
                diagnostics::log(format!(
                    "mcp_pool_socket_used name={} socket={}",
                    mcp_name,
                    socket_path.display()
                ));
                return Ok(MCPServerConfig {
                    server_type: Some("stdio".to_string()),
                    command: proxy::proxy_command(),
                    args: vec!["--name".to_string(), mcp_name.to_string()],
                    ..Default::default()
                });
            } else if !config.mcp_pool.fallback_to_stdio {
                return Err(McpError::InvalidConfig(format!(
                    "mcp socket not available and fallback disabled for {}",
                    mcp_name
                )));
            }
        }

        Ok(MCPServerConfig {
            server_type: Some("stdio".to_string()),
            command: mcp_def.command.clone(),
            args: mcp_def.args.clone(),
            env: mcp_def.env.clone(),
            ..Default::default()
        })
    }

    /// Write MCP config atomically
    async fn write_mcp_json(&self, path: &PathBuf, config: &McpJsonConfig) -> McpResult<()> {
        // Ensure directory exists
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)
                .await
                .map_err(|e| McpError::McpJsonWriteError(format!("create_dir_all: {}", e)))?;
        }

        // Serialize
        let json_str = serde_json::to_string_pretty(config)
            .map_err(|e| McpError::McpJsonWriteError(format!("serialization: {}", e)))?;

        // Atomic write
        let temp_path = path.with_extension("json.tmp");
        {
            let mut file = fs::File::create(&temp_path)
                .await
                .map_err(|e| McpError::McpJsonWriteError(format!("create tmp: {}", e)))?;
            file.write_all(json_str.as_bytes())
                .await
                .map_err(|e| McpError::McpJsonWriteError(format!("write tmp: {}", e)))?;
        }

        fs::rename(&temp_path, path)
            .await
            .map_err(|e| McpError::McpJsonWriteError(format!("rename: {}", e)))?;

        Ok(())
    }

    /// Set multiple MCPs at once for a scope (replaces existing)
    ///
    /// # Arguments
    /// * `scope` - Attachment scope (Global, Project, or Local)
    /// * `project_path` - Project directory path (required for Project and Local scopes)
    /// * `mcp_names` - Names of MCPs to attach
    pub async fn set_mcps(
        &self,
        scope: McpScope,
        project_path: Option<&str>,
        mcp_names: &[String],
    ) -> McpResult<()> {
        // Verify all MCPs exist
        let config = self.load_config().await?;
        let mcp_defs: HashMap<String, super::config::MCPDef> = mcp_names
            .iter()
            .filter_map(|name| config.mcps.get(name).map(|def| (name.clone(), def.clone())))
            .collect();

        if mcp_defs.len() != mcp_names.len() {
            let missing: Vec<_> = mcp_names
                .iter()
                .filter(|name| !config.mcps.contains_key(*name))
                .collect();
            return Err(McpError::MCPNotFound(format!("{:?}", missing)));
        }

        match scope {
            McpScope::Global => self.set_mcps_global(&mcp_defs).await,
            McpScope::Project => {
                let path = project_path.ok_or_else(|| {
                    McpError::InvalidInput("project_path required for project scope".to_string())
                })?;
                self.set_mcps_local(path, &mcp_defs).await
            }
            McpScope::Local => {
                let path = project_path.ok_or_else(|| {
                    McpError::InvalidInput("project_path required for local scope".to_string())
                })?;
                self.set_mcps_local(path, &mcp_defs).await
            }
        }
    }

    /// Set global MCPs
    async fn set_mcps_global(&self, mcp_defs: &HashMap<String, super::config::MCPDef>) -> McpResult<()> {
        let config_path = get_managed_global_mcp_path()?;
        let mut config = McpJsonConfig::default();
        for (name, mcp_def) in mcp_defs {
            let server_config = self.mcp_def_to_server_config(name, mcp_def).await?;
            config.mcp_servers.insert(name.clone(), server_config);
        }
        self.write_mcp_json(&config_path, &config).await
    }

    /// Set local MCPs (AgentTerm-managed project config)
    async fn set_mcps_local(
        &self,
        project_path: &str,
        mcp_defs: &HashMap<String, super::config::MCPDef>,
    ) -> McpResult<()> {
        let mcp_json_path = get_managed_project_mcp_path(project_path)?;

        // Create new config
        let mut config = McpJsonConfig::default();

        // Build mcpServers
        for (name, mcp_def) in mcp_defs {
            let server_config = self.mcp_def_to_server_config(name, mcp_def).await?;
            config.mcp_servers.insert(name.clone(), server_config);
        }

        self.write_mcp_json(&mcp_json_path, &config).await
    }

    /// Create example config.toml if it doesn't exist
    pub async fn create_example_config(&self) -> McpResult<()> {
        let config_path = get_config_path()?;

        if config_path.exists() {
            return Ok(()); // Don't overwrite
        }

        // Ensure directory exists
        if let Some(parent) = config_path.parent() {
            fs::create_dir_all(parent)
                .await
                .map_err(|e| McpError::ConfigWriteError(format!("create_dir_all: {}", e)))?;
        }

        let example_config = r#"# Agent Terminal User Configuration
# This file is loaded on startup. Edit to customize MCPs.

# Default AI tool for new sessions
# When creating a new session, this tool will be pre-selected
# Valid values: "claude", "gemini", "shell", or any custom tool name
# Leave commented out or empty to default to shell (no pre-selection)
# default_tool = "claude"

# Claude Code integration
# Set this if you use a custom Claude profile (e.g., dual account setup)
# Default: ~/.claude (or CLAUDE_CONFIG_DIR env var takes priority)
# [claude]
# config_dir = "~/.claude-work"

# ============================================================================
# MCP Server Definitions
# ============================================================================
# Define available MCP servers here. These can be attached/detached per-project
# using the MCP Manager.
#
# Supports two transport types:
#
# STDIO MCPs (local command-line tools):
#   command     - The executable to run (e.g., "npx", "docker", "node")
#   args        - Command-line arguments (array)
#   env         - Environment variables (optional)
#   description - Help text shown in the MCP Manager (optional)
#
# HTTP/SSE MCPs (remote servers):
#   url         - The endpoint URL (http:// or https://)
#   transport   - "http" or "sse" (defaults to "http" if url is set)
#   description - Help text shown in the MCP Manager (optional)

# ---------- STDIO Examples ----------

# Example: Exa Search MCP
# [mcps.exa]
# command = "npx"
# args = ["-y", "@anthropics/exa-mcp"]
# description = "Web search via Exa AI"

# Example: Filesystem MCP with restricted paths
# [mcps.filesystem]
# command = "npx"
# args = ["-y", "@modelcontextprotocol/server-filesystem", "/Users/you/projects"]
# description = "Read/write local files"

# Example: Sequential Thinking MCP
# [mcps.thinking]
# command = "npx"
# args = ["-y", "@modelcontextprotocol/server-sequential-thinking"]
# description = "Step-by-step reasoning for complex problems"

# ---------- HTTP/SSE Examples ----------

# Example: HTTP MCP server (local or remote)
# [mcps.my-http-server]
# url = "http://localhost:8000/mcp"
# transport = "http"
# description = "My custom HTTP MCP server"
"#;

        fs::write(&config_path, example_config)
            .await
            .map_err(|e| McpError::ConfigWriteError(format!("write: {}", e)))?;

        Ok(())
    }
}

impl Default for McpManager {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use super::super::config::expand_tilde;
    use tempfile::TempDir;

    fn test_manager() -> (TempDir, McpManager) {
        let temp = TempDir::new().unwrap();
        // Set env var so config is read from temp dir instead of real ~/.agent-term
        // Rust 2024: modifying process env is `unsafe` due to potential UB with concurrent readers.
        unsafe {
            std::env::set_var("AGENT_TERM_HOME", temp.path());
        }
        let manager = McpManager::new();
        (temp, manager)
    }

    #[tokio::test]
    async fn test_load_config_default() {
        let (_temp, manager) = test_manager();
        let config = manager.load_config().await.unwrap();
        assert!(config.mcps.is_empty());
        assert!(config.tools.is_empty());
    }

    #[test]
    fn test_mcp_scope_display() {
        assert_eq!(format!("{:?}", McpScope::Global), "Global");
        assert_eq!(format!("{:?}", McpScope::Project), "Project");
        assert_eq!(format!("{:?}", McpScope::Local), "Local");
    }

    #[test]
    fn test_expand_tilde() {
        // dirs::home_dir() handles cross-platform home detection
        let expanded = expand_tilde("~/test");
        if let Some(home) = dirs::home_dir() {
            assert_eq!(expanded, home.join("test"));
        }
    }
}
