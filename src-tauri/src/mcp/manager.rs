use super::config::{
    get_claude_config_path, get_config_path, ClaudeJsonConfig, MCPServerConfig,
    McpJsonConfig, UserConfig,
};
use super::error::{McpError, McpResult};
use super::pool_manager;
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
    /// Global scope (Claude's global mcpServers)
    Global,
    /// Project scope (alias for local .mcp.json in project directory)
    Project,
    /// Local scope (.mcp.json in project directory)
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

    /// Force reload of user configuration
    pub async fn reload_config(&self) -> McpResult<UserConfig> {
        // Clear cache
        *self.config_cache.lock() = None;
        self.load_config().await
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

    /// Get all available MCP definitions from config
    pub async fn get_available_mcps(&self) -> McpResult<HashMap<String, super::config::MCPDef>> {
        let config = self.load_config().await?;
        Ok(config.mcps)
    }

    /// Get list of available MCP names (sorted)
    pub async fn get_available_mcp_names(&self) -> McpResult<Vec<String>> {
        let mcps = self.get_available_mcps().await?;
        let mut names: Vec<String> = mcps.keys().cloned().collect();
        names.sort();
        Ok(names)
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

    /// Get globally attached MCPs (from Claude's mcpServers)
    async fn get_global_mcps(&self) -> McpResult<Vec<String>> {
        let config_path = get_claude_config_path()?;

        if !config_path.exists() {
            return Ok(Vec::new());
        }

        let contents = fs::read_to_string(&config_path)
            .await
            .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?;

        let config: ClaudeJsonConfig = serde_json::from_str(&contents)
            .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?;

        let mut names: Vec<String> = config.mcp_servers.keys().cloned().collect();
        names.sort();
        Ok(names)
    }

    /// Get project-specific MCPs (from Claude's projects[path].mcpServers)
    async fn get_project_mcps(&self, project_path: &str) -> McpResult<Vec<String>> {
        let config_path = get_claude_config_path()?;

        if !config_path.exists() {
            return Ok(Vec::new());
        }

        let contents = fs::read_to_string(&config_path)
            .await
            .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?;

        let config: ClaudeJsonConfig = serde_json::from_str(&contents)
            .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?;

        let project = config.projects.get(project_path);
        match project {
            Some(proj) => {
                let mut names: Vec<String> = proj.mcp_servers.keys().cloned().collect();
                names.sort();
                Ok(names)
            }
            None => Ok(Vec::new()),
        }
    }

    /// Get local MCPs (from .mcp.json in project directory)
    async fn get_local_mcps(&self, project_path: &str) -> McpResult<Vec<String>> {
        let mcp_json_path = PathBuf::from(project_path).join(".mcp.json");

        if !mcp_json_path.exists() {
            return Ok(Vec::new());
        }

        let contents = fs::read_to_string(&mcp_json_path).await.map_err(|e| {
            diagnostics::log(format!(
                "mcp_local_read_failed path={} error={}",
                mcp_json_path.display(),
                e
            ));
            McpError::IoError(format!("{}: {}", mcp_json_path.display(), e))
        })?;

        let config: McpJsonConfig = serde_json::from_str(&contents).map_err(|e| {
            diagnostics::log(format!(
                "mcp_local_parse_failed path={} error={}",
                mcp_json_path.display(),
                e
            ));
            McpError::ConfigParseError(format!("{}: {}", mcp_json_path.display(), e))
        })?;

        let mut names: Vec<String> = config.mcp_servers.keys().cloned().collect();
        names.sort();
        Ok(names)
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
        let config_path = get_claude_config_path()?;
        let mut config = self.read_claude_config_value(&config_path).await?;

        let server_config = self.mcp_def_to_server_config(mcp_name, mcp_def).await?;
        let mcp_servers = self.ensure_object_field(&mut config, "mcpServers")?;
        mcp_servers.insert(mcp_name.to_string(), serde_json::to_value(server_config)?);

        self.write_claude_config(&config_path, &config).await
    }

    /// Attach MCP to project scope
    async fn attach_mcp_project(
        &self,
        project_path: &str,
        mcp_name: &str,
        mcp_def: &super::config::MCPDef,
    ) -> McpResult<()> {
        let config_path = get_claude_config_path()?;
        let mut config = self.read_claude_config_value(&config_path).await?;

        let server_config = self.mcp_def_to_server_config(mcp_name, mcp_def).await?;
        let projects = self.ensure_object_field(&mut config, "projects")?;
        let project_entry = projects
            .entry(project_path.to_string())
            .or_insert_with(|| serde_json::Value::Object(serde_json::Map::new()));
        if !project_entry.is_object() {
            *project_entry = serde_json::Value::Object(serde_json::Map::new());
        }
        let project_obj = project_entry
            .as_object_mut()
            .ok_or_else(|| McpError::InvalidConfig("project config is not an object".to_string()))?;
        let mcp_servers = project_obj
            .entry("mcpServers".to_string())
            .or_insert_with(|| serde_json::Value::Object(serde_json::Map::new()));
        if !mcp_servers.is_object() {
            *mcp_servers = serde_json::Value::Object(serde_json::Map::new());
        }
        let mcp_servers_obj = mcp_servers
            .as_object_mut()
            .ok_or_else(|| McpError::InvalidConfig("project mcpServers is not an object".to_string()))?;
        mcp_servers_obj.insert(mcp_name.to_string(), serde_json::to_value(server_config)?);

        self.write_claude_config(&config_path, &config).await
    }

    /// Attach MCP to local scope (.mcp.json in project directory)
    async fn attach_mcp_local(
        &self,
        project_path: &str,
        mcp_name: &str,
        mcp_def: &super::config::MCPDef,
    ) -> McpResult<()> {
        let mcp_json_path = PathBuf::from(project_path).join(".mcp.json");

        // Read existing .mcp.json or create new
        let mut config = if mcp_json_path.exists() {
            let contents = fs::read_to_string(&mcp_json_path)
                .await
                .map_err(|e| McpError::McpJsonWriteError(e.to_string()))?;
            serde_json::from_str(&contents)
                .map_err(|e| McpError::McpJsonWriteError(e.to_string()))?
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
        let config_path = get_claude_config_path()?;

        if !config_path.exists() {
            return Ok(()); // Nothing to detach
        }
        let mut config = self.read_claude_config_value(&config_path).await?;
        let mcp_servers = self.ensure_object_field(&mut config, "mcpServers")?;
        mcp_servers.remove(mcp_name);
        self.write_claude_config(&config_path, &config).await
    }

    /// Detach MCP from project scope
    async fn detach_mcp_project(&self, project_path: &str, mcp_name: &str) -> McpResult<()> {
        let config_path = get_claude_config_path()?;

        if !config_path.exists() {
            return Ok(()); // Nothing to detach
        }
        let mut config = self.read_claude_config_value(&config_path).await?;
        let projects = self.ensure_object_field(&mut config, "projects")?;
        if let Some(project_entry) = projects.get_mut(project_path) {
            if !project_entry.is_object() {
                *project_entry = serde_json::Value::Object(serde_json::Map::new());
            }
            if let Some(project_obj) = project_entry.as_object_mut() {
                if let Some(mcp_servers) = project_obj.get_mut("mcpServers") {
                    if let Some(mcp_servers_obj) = mcp_servers.as_object_mut() {
                        mcp_servers_obj.remove(mcp_name);
                    }
                }
            }
        }
        self.write_claude_config(&config_path, &config).await
    }

    /// Detach MCP from local scope
    async fn detach_mcp_local(&self, project_path: &str, mcp_name: &str) -> McpResult<()> {
        let mcp_json_path = PathBuf::from(project_path).join(".mcp.json");

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
        if config.mcp_pool.enabled && cfg!(unix) {
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
                        );
                    }

                    if pool.is_running(mcp_name) {
                        if let Some(socket_path) = pool.socket_path(mcp_name) {
                            diagnostics::log(format!(
                                "mcp_pool_socket_used name={} socket={}",
                                mcp_name,
                                socket_path.display()
                            ));
                            return Ok(MCPServerConfig {
                                command: "nc".to_string(),
                                args: vec!["-U".to_string(), socket_path.display().to_string()],
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
                    command: "nc".to_string(),
                    args: vec!["-U".to_string(), socket_path.display().to_string()],
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

    /// Read Claude config as raw JSON (preserves unknown fields)
    async fn read_claude_config_value(&self, path: &PathBuf) -> McpResult<serde_json::Value> {
        if !path.exists() {
            return Ok(serde_json::Value::Object(serde_json::Map::new()));
        }

        let contents = fs::read_to_string(path)
            .await
            .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?;
        let value: serde_json::Value = serde_json::from_str(&contents)
            .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?;
        if !value.is_object() {
            return Err(McpError::InvalidConfig(
                "claude config root is not an object".to_string(),
            ));
        }
        Ok(value)
    }

    fn ensure_object_field<'a>(
        &self,
        value: &'a mut serde_json::Value,
        key: &str,
    ) -> McpResult<&'a mut serde_json::Map<String, serde_json::Value>> {
        if !value.is_object() {
            return Err(McpError::InvalidConfig(
                "claude config root is not an object".to_string(),
            ));
        }
        let root = value.as_object_mut().expect("checked is_object");
        let entry = root
            .entry(key.to_string())
            .or_insert_with(|| serde_json::Value::Object(serde_json::Map::new()));
        if !entry.is_object() {
            *entry = serde_json::Value::Object(serde_json::Map::new());
        }
        Ok(entry
            .as_object_mut()
            .ok_or_else(|| McpError::InvalidConfig(format!("{} is not an object", key)))?)
    }

    /// Write Claude config atomically
    async fn write_claude_config(&self, path: &PathBuf, config: &serde_json::Value) -> McpResult<()> {
        // Ensure directory exists
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)
                .await
                .map_err(|e| McpError::ClaudeConfigWriteError(format!("create_dir_all: {}", e)))?;
        }

        // Serialize
        let json_str = serde_json::to_string_pretty(config)
            .map_err(|e| McpError::ClaudeConfigWriteError(format!("serialization: {}", e)))?;

        // Atomic write
        let temp_path = path.with_extension("json.tmp");
        {
            let mut file = fs::File::create(&temp_path)
                .await
                .map_err(|e| McpError::ClaudeConfigWriteError(format!("create tmp: {}", e)))?;
            file.write_all(json_str.as_bytes())
                .await
                .map_err(|e| McpError::ClaudeConfigWriteError(format!("write tmp: {}", e)))?;
        }

        fs::rename(&temp_path, path)
            .await
            .map_err(|e| McpError::ClaudeConfigWriteError(format!("rename: {}", e)))?;

        Ok(())
    }

    /// Write .mcp.json atomically
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
        let config_path = get_claude_config_path()?;
        let mut config = self.read_claude_config_value(&config_path).await?;

        let mut mcp_servers = serde_json::Map::new();
        for (name, mcp_def) in mcp_defs {
            let server_config = self.mcp_def_to_server_config(name, mcp_def).await?;
            mcp_servers.insert(
                name.clone(),
                serde_json::to_value(server_config)
                    .map_err(|e| McpError::ClaudeConfigWriteError(e.to_string()))?,
            );
        }

        if !config.is_object() {
            return Err(McpError::InvalidConfig(
                "claude config root is not an object".to_string(),
            ));
        }
        config
            .as_object_mut()
            .expect("checked is_object")
            .insert("mcpServers".to_string(), serde_json::Value::Object(mcp_servers));

        self.write_claude_config(&config_path, &config).await
    }

    /// Set project MCPs
    async fn set_mcps_project(
        &self,
        project_path: &str,
        mcp_defs: &HashMap<String, super::config::MCPDef>,
    ) -> McpResult<()> {
        let config_path = get_claude_config_path()?;
        let mut config = self.read_claude_config_value(&config_path).await?;

        let projects = self.ensure_object_field(&mut config, "projects")?;
        let project_entry = projects
            .entry(project_path.to_string())
            .or_insert_with(|| serde_json::Value::Object(serde_json::Map::new()));
        if !project_entry.is_object() {
            *project_entry = serde_json::Value::Object(serde_json::Map::new());
        }
        let project_obj = project_entry
            .as_object_mut()
            .ok_or_else(|| McpError::InvalidConfig("project config is not an object".to_string()))?;

        let mut mcp_servers = serde_json::Map::new();
        for (name, mcp_def) in mcp_defs {
            let server_config = self.mcp_def_to_server_config(name, mcp_def).await?;
            mcp_servers.insert(
                name.clone(),
                serde_json::to_value(server_config)
                    .map_err(|e| McpError::ClaudeConfigWriteError(e.to_string()))?,
            );
        }
        project_obj.insert(
            "mcpServers".to_string(),
            serde_json::Value::Object(mcp_servers),
        );

        self.write_claude_config(&config_path, &config).await
    }

    /// Set local MCPs
    async fn set_mcps_local(
        &self,
        project_path: &str,
        mcp_defs: &HashMap<String, super::config::MCPDef>,
    ) -> McpResult<()> {
        let mcp_json_path = PathBuf::from(project_path).join(".mcp.json");

        // Create new config
        let mut config = McpJsonConfig::default();

        // Build mcpServers
        for (name, mcp_def) in mcp_defs {
            let server_config = self.mcp_def_to_server_config(name, mcp_def).await?;
            config.mcp_servers.insert(name.clone(), server_config);
        }

        self.write_mcp_json(&mcp_json_path, &config).await
    }

    /// Clear all MCPs from a scope
    ///
    /// # Arguments
    /// * `scope` - Attachment scope (Global, Project, or Local)
    /// * `project_path` - Project directory path (required for Project and Local scopes)
    pub async fn clear_mcps(&self, scope: McpScope, project_path: Option<&str>) -> McpResult<()> {
        match scope {
            McpScope::Global => self.clear_mcps_global().await,
            McpScope::Project => {
                let path = project_path.ok_or_else(|| {
                    McpError::InvalidInput("project_path required for project scope".to_string())
                })?;
                self.clear_mcps_local(path).await
            }
            McpScope::Local => {
                let path = project_path.ok_or_else(|| {
                    McpError::InvalidInput("project_path required for local scope".to_string())
                })?;
                self.clear_mcps_local(path).await
            }
        }
    }

    /// Clear global MCPs
    async fn clear_mcps_global(&self) -> McpResult<()> {
        let config_path = get_claude_config_path()?;
        if !config_path.exists() {
            return Ok(());
        }

        let mut config = self.read_claude_config_value(&config_path).await?;
        let root = config
            .as_object_mut()
            .ok_or_else(|| McpError::InvalidConfig("claude config root is not an object".to_string()))?;
        root.insert(
            "mcpServers".to_string(),
            serde_json::Value::Object(serde_json::Map::new()),
        );
        self.write_claude_config(&config_path, &config).await
    }

    /// Clear project MCPs
    async fn clear_mcps_project(&self, project_path: &str) -> McpResult<()> {
        let config_path = get_claude_config_path()?;
        if !config_path.exists() {
            return Ok(());
        }

        let mut config = self.read_claude_config_value(&config_path).await?;
        let projects = self.ensure_object_field(&mut config, "projects")?;
        if let Some(project_entry) = projects.get_mut(project_path) {
            if !project_entry.is_object() {
                *project_entry = serde_json::Value::Object(serde_json::Map::new());
            }
            if let Some(project_obj) = project_entry.as_object_mut() {
                project_obj.insert(
                    "mcpServers".to_string(),
                    serde_json::Value::Object(serde_json::Map::new()),
                );
            }
        }

        self.write_claude_config(&config_path, &config).await
    }

    /// Clear local MCPs (delete .mcp.json)
    async fn clear_mcps_local(&self, project_path: &str) -> McpResult<()> {
        let mcp_json_path = PathBuf::from(project_path).join(".mcp.json");

        if mcp_json_path.exists() {
            fs::remove_file(&mcp_json_path)
                .await
                .map_err(|e| McpError::IoError(format!("remove .mcp.json: {}", e)))?;
        }

        Ok(())
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
        let home = std::env::var("HOME").unwrap();
        let expanded = expand_tilde("~/test");
        assert!(expanded.starts_with(&home));
        assert!(expanded.ends_with("test"));
    }
}
