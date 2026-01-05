use super::config::{
    expand_tilde, get_claude_config_path, get_config_path, ClaudeJsonConfig, ClaudeProjectConfig,
    MCPServerConfig, McpJsonConfig, UserConfig,
};
use super::error::{McpError, McpResult};
use parking_lot::Mutex;
use serde_json;
use std::collections::{HashMap, HashSet};
use std::fs;
use std::io::Write;
use std::path::PathBuf;
use std::sync::Arc;

/// MCP attachment scope
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum McpScope {
    /// Global scope (Claude's global mcpServers)
    Global,
    /// Project scope (Claude's projects[path].mcpServers)
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
    pub fn load_config(&self) -> McpResult<UserConfig> {
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

        // Read and parse config
        let contents = fs::read_to_string(&config_path)
            .map_err(|e| McpError::ConfigReadError(format!("{}: {}", config_path.display(), e)))?;

        let config: UserConfig = toml::from_str(&contents)
            .map_err(|e| McpError::ConfigParseError(format!("{}: {}", config_path.display(), e)))?;

        // Cache the config
        *self.config_cache.lock() = Some(config.clone());

        Ok(config)
    }

    /// Force reload of user configuration
    pub fn reload_config(&self) -> McpResult<UserConfig> {
        // Clear cache
        *self.config_cache.lock() = None;
        self.load_config()
    }

    /// Write user configuration to ~/.agent-term/config.toml
    fn write_config(&self, config: &UserConfig) -> McpResult<()> {
        let config_path = get_config_path()?;

        // Ensure directory exists
        if let Some(parent) = config_path.parent() {
            fs::create_dir_all(parent)
                .map_err(|e| McpError::ConfigWriteError(format!("create_dir_all: {}", e)))?;
        }

        // Serialize to TOML
        let toml_str = toml::to_string_pretty(config)
            .map_err(|e| McpError::ConfigWriteError(format!("toml serialization: {}", e)))?;

        // Atomic write: write to temp file, then rename
        let temp_path = config_path.with_extension("toml.tmp");
        {
            let mut file = fs::File::create(&temp_path)
                .map_err(|e| McpError::ConfigWriteError(format!("create tmp: {}", e)))?;
            file.write_all(toml_str.as_bytes())
                .map_err(|e| McpError::ConfigWriteError(format!("write tmp: {}", e)))?;
        }

        // Atomic rename
        fs::rename(&temp_path, &config_path)
            .map_err(|e| McpError::ConfigWriteError(format!("rename: {}", e)))?;

        // Update cache
        *self.config_cache.lock() = Some(config.clone());

        Ok(())
    }

    /// Get all available MCP definitions from config
    pub fn get_available_mcps(&self) -> McpResult<HashMap<String, super::config::MCPDef>> {
        let config = self.load_config()?;
        Ok(config.mcps)
    }

    /// Get list of available MCP names (sorted)
    pub fn get_available_mcp_names(&self) -> McpResult<Vec<String>> {
        let mcps = self.get_available_mcps()?;
        let mut names: Vec<String> = mcps.keys().cloned().collect();
        names.sort();
        Ok(names)
    }

    /// Get MCP definition by name
    pub fn get_mcp_def(&self, name: &str) -> McpResult<super::config::MCPDef> {
        let config = self.load_config()?;
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
    pub fn get_attached_mcps(
        &self,
        scope: McpScope,
        project_path: Option<&str>,
    ) -> McpResult<Vec<String>> {
        match scope {
            McpScope::Global => self.get_global_mcps(),
            McpScope::Project => {
                let path = project_path
                    .ok_or_else(|| McpError::InvalidInput("project_path required".to_string()))?;
                self.get_project_mcps(path)
            }
            McpScope::Local => {
                let path = project_path
                    .ok_or_else(|| McpError::InvalidInput("project_path required".to_string()))?;
                self.get_local_mcps(path)
            }
        }
    }

    /// Get globally attached MCPs (from Claude's mcpServers)
    fn get_global_mcps(&self) -> McpResult<Vec<String>> {
        let config_path = get_claude_config_path()?;

        if !config_path.exists() {
            return Ok(Vec::new());
        }

        let contents = fs::read_to_string(&config_path)
            .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?;

        let config: ClaudeJsonConfig = serde_json::from_str(&contents)
            .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?;

        let mut names: Vec<String> = config.mcp_servers.keys().cloned().collect();
        names.sort();
        Ok(names)
    }

    /// Get project-specific MCPs (from Claude's projects[path].mcpServers)
    fn get_project_mcps(&self, project_path: &str) -> McpResult<Vec<String>> {
        let config_path = get_claude_config_path()?;

        if !config_path.exists() {
            return Ok(Vec::new());
        }

        let contents = fs::read_to_string(&config_path)
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
    fn get_local_mcps(&self, project_path: &str) -> McpResult<Vec<String>> {
        let mcp_json_path = PathBuf::from(project_path).join(".mcp.json");

        if !mcp_json_path.exists() {
            return Ok(Vec::new());
        }

        let contents = fs::read_to_string(&mcp_json_path)
            .map_err(|e| McpError::McpJsonWriteError(e.to_string()))?;

        let config: McpJsonConfig = serde_json::from_str(&contents)
            .map_err(|e| McpError::McpJsonWriteError(e.to_string()))?;

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
    pub fn attach_mcp(
        &self,
        scope: McpScope,
        project_path: Option<&str>,
        mcp_name: &str,
    ) -> McpResult<()> {
        // Verify MCP exists
        let mcp_def = self.get_mcp_def(mcp_name)?;

        match scope {
            McpScope::Global => self.attach_mcp_global(mcp_name, &mcp_def),
            McpScope::Project => {
                let path = project_path.ok_or_else(|| {
                    McpError::InvalidInput("project_path required for project scope".to_string())
                })?;
                self.attach_mcp_project(path, mcp_name, &mcp_def)
            }
            McpScope::Local => {
                let path = project_path.ok_or_else(|| {
                    McpError::InvalidInput("project_path required for local scope".to_string())
                })?;
                self.attach_mcp_local(path, mcp_name, &mcp_def)
            }
        }
    }

    /// Attach MCP to global scope
    fn attach_mcp_global(&self, mcp_name: &str, mcp_def: &super::config::MCPDef) -> McpResult<()> {
        let config_path = get_claude_config_path()?;

        // Read existing config or create new
        let mut config = if config_path.exists() {
            let contents = fs::read_to_string(&config_path)
                .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?;
            serde_json::from_str(&contents)
                .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?
        } else {
            ClaudeJsonConfig::default()
        };

        // Convert MCPDef to MCPServerConfig
        let server_config = self.mcp_def_to_server_config(mcp_def);

        // Add to global mcpServers
        config
            .mcp_servers
            .insert(mcp_name.to_string(), serde_json::to_value(server_config)?);

        // Write back
        self.write_claude_config(&config_path, &config)
    }

    /// Attach MCP to project scope
    fn attach_mcp_project(
        &self,
        project_path: &str,
        mcp_name: &str,
        mcp_def: &super::config::MCPDef,
    ) -> McpResult<()> {
        let config_path = get_claude_config_path()?;

        // Read existing config or create new
        let mut config = if config_path.exists() {
            let contents = fs::read_to_string(&config_path)
                .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?;
            serde_json::from_str(&contents)
                .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?
        } else {
            ClaudeJsonConfig::default()
        };

        // Convert MCPDef to MCPServerConfig
        let server_config = self.mcp_def_to_server_config(mcp_def);

        // Get or create project config
        let project = config
            .projects
            .entry(project_path.to_string())
            .or_insert_with(ClaudeProjectConfig::default);

        // Add to project mcpServers
        project
            .mcp_servers
            .insert(mcp_name.to_string(), serde_json::to_value(server_config)?);

        // Write back
        self.write_claude_config(&config_path, &config)
    }

    /// Attach MCP to local scope (.mcp.json in project directory)
    fn attach_mcp_local(
        &self,
        project_path: &str,
        mcp_name: &str,
        mcp_def: &super::config::MCPDef,
    ) -> McpResult<()> {
        let mcp_json_path = PathBuf::from(project_path).join(".mcp.json");

        // Read existing .mcp.json or create new
        let mut config = if mcp_json_path.exists() {
            let contents = fs::read_to_string(&mcp_json_path)
                .map_err(|e| McpError::McpJsonWriteError(e.to_string()))?;
            serde_json::from_str(&contents)
                .map_err(|e| McpError::McpJsonWriteError(e.to_string()))?
        } else {
            McpJsonConfig::default()
        };

        // Convert MCPDef to MCPServerConfig
        let server_config = self.mcp_def_to_server_config(mcp_def);

        // Add to mcpServers
        config
            .mcp_servers
            .insert(mcp_name.to_string(), server_config);

        // Write back atomically
        self.write_mcp_json(&mcp_json_path, &config)
    }

    /// Detach an MCP from a scope
    ///
    /// # Arguments
    /// * `scope` - Attachment scope (Global, Project, or Local)
    /// * `project_path` - Project directory path (required for Project and Local scopes)
    /// * `mcp_name` - Name of MCP to detach
    pub fn detach_mcp(
        &self,
        scope: McpScope,
        project_path: Option<&str>,
        mcp_name: &str,
    ) -> McpResult<()> {
        match scope {
            McpScope::Global => self.detach_mcp_global(mcp_name),
            McpScope::Project => {
                let path = project_path.ok_or_else(|| {
                    McpError::InvalidInput("project_path required for project scope".to_string())
                })?;
                self.detach_mcp_project(path, mcp_name)
            }
            McpScope::Local => {
                let path = project_path.ok_or_else(|| {
                    McpError::InvalidInput("project_path required for local scope".to_string())
                })?;
                self.detach_mcp_local(path, mcp_name)
            }
        }
    }

    /// Detach MCP from global scope
    fn detach_mcp_global(&self, mcp_name: &str) -> McpResult<()> {
        let config_path = get_claude_config_path()?;

        if !config_path.exists() {
            return Ok(()); // Nothing to detach
        }

        let contents = fs::read_to_string(&config_path)
            .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?;

        let mut config: ClaudeJsonConfig = serde_json::from_str(&contents)
            .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?;

        config.mcp_servers.remove(mcp_name);

        self.write_claude_config(&config_path, &config)
    }

    /// Detach MCP from project scope
    fn detach_mcp_project(&self, project_path: &str, mcp_name: &str) -> McpResult<()> {
        let config_path = get_claude_config_path()?;

        if !config_path.exists() {
            return Ok(()); // Nothing to detach
        }

        let contents = fs::read_to_string(&config_path)
            .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?;

        let mut config: ClaudeJsonConfig = serde_json::from_str(&contents)
            .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?;

        if let Some(project) = config.projects.get_mut(project_path) {
            project.mcp_servers.remove(mcp_name);
        }

        self.write_claude_config(&config_path, &config)
    }

    /// Detach MCP from local scope
    fn detach_mcp_local(&self, project_path: &str, mcp_name: &str) -> McpResult<()> {
        let mcp_json_path = PathBuf::from(project_path).join(".mcp.json");

        if !mcp_json_path.exists() {
            return Ok(()); // Nothing to detach
        }

        let contents = fs::read_to_string(&mcp_json_path)
            .map_err(|e| McpError::McpJsonWriteError(e.to_string()))?;

        let mut config: McpJsonConfig = serde_json::from_str(&contents)
            .map_err(|e| McpError::McpJsonWriteError(e.to_string()))?;

        config.mcp_servers.remove(mcp_name);

        self.write_mcp_json(&mcp_json_path, &config)
    }

    /// Convert MCPDef to MCPServerConfig
    fn mcp_def_to_server_config(&self, mcp_def: &super::config::MCPDef) -> MCPServerConfig {
        // Check if this is an HTTP/SSE MCP
        if !mcp_def.url.is_empty() {
            let transport = if mcp_def.transport.is_empty() {
                "http".to_string()
            } else {
                mcp_def.transport.clone()
            };

            return MCPServerConfig {
                server_type: Some(transport),
                url: mcp_def.url.clone(),
                ..Default::default()
            };
        }

        // STDIO MCP
        MCPServerConfig {
            server_type: Some("stdio".to_string()),
            command: mcp_def.command.clone(),
            args: mcp_def.args.clone(),
            env: mcp_def.env.clone(),
            ..Default::default()
        }
    }

    /// Write Claude config atomically
    fn write_claude_config(&self, path: &PathBuf, config: &ClaudeJsonConfig) -> McpResult<()> {
        // Ensure directory exists
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)
                .map_err(|e| McpError::ClaudeConfigWriteError(format!("create_dir_all: {}", e)))?;
        }

        // Serialize
        let json_str = serde_json::to_string_pretty(config)
            .map_err(|e| McpError::ClaudeConfigWriteError(format!("serialization: {}", e)))?;

        // Atomic write
        let temp_path = path.with_extension("json.tmp");
        {
            let mut file = fs::File::create(&temp_path)
                .map_err(|e| McpError::ClaudeConfigWriteError(format!("create tmp: {}", e)))?;
            file.write_all(json_str.as_bytes())
                .map_err(|e| McpError::ClaudeConfigWriteError(format!("write tmp: {}", e)))?;
        }

        fs::rename(&temp_path, path)
            .map_err(|e| McpError::ClaudeConfigWriteError(format!("rename: {}", e)))?;

        Ok(())
    }

    /// Write .mcp.json atomically
    fn write_mcp_json(&self, path: &PathBuf, config: &McpJsonConfig) -> McpResult<()> {
        // Ensure directory exists
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)
                .map_err(|e| McpError::McpJsonWriteError(format!("create_dir_all: {}", e)))?;
        }

        // Serialize
        let json_str = serde_json::to_string_pretty(config)
            .map_err(|e| McpError::McpJsonWriteError(format!("serialization: {}", e)))?;

        // Atomic write
        let temp_path = path.with_extension("json.tmp");
        {
            let mut file = fs::File::create(&temp_path)
                .map_err(|e| McpError::McpJsonWriteError(format!("create tmp: {}", e)))?;
            file.write_all(json_str.as_bytes())
                .map_err(|e| McpError::McpJsonWriteError(format!("write tmp: {}", e)))?;
        }

        fs::rename(&temp_path, path)
            .map_err(|e| McpError::McpJsonWriteError(format!("rename: {}", e)))?;

        Ok(())
    }

    /// Set multiple MCPs at once for a scope (replaces existing)
    ///
    /// # Arguments
    /// * `scope` - Attachment scope (Global, Project, or Local)
    /// * `project_path` - Project directory path (required for Project and Local scopes)
    /// * `mcp_names` - Names of MCPs to attach
    pub fn set_mcps(
        &self,
        scope: McpScope,
        project_path: Option<&str>,
        mcp_names: &[String],
    ) -> McpResult<()> {
        // Verify all MCPs exist
        let config = self.load_config()?;
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
            McpScope::Global => self.set_mcps_global(&mcp_defs),
            McpScope::Project => {
                let path = project_path.ok_or_else(|| {
                    McpError::InvalidInput("project_path required for project scope".to_string())
                })?;
                self.set_mcps_project(path, &mcp_defs)
            }
            McpScope::Local => {
                let path = project_path.ok_or_else(|| {
                    McpError::InvalidInput("project_path required for local scope".to_string())
                })?;
                self.set_mcps_local(path, &mcp_defs)
            }
        }
    }

    /// Set global MCPs
    fn set_mcps_global(&self, mcp_defs: &HashMap<String, super::config::MCPDef>) -> McpResult<()> {
        let config_path = get_claude_config_path()?;

        // Read existing config
        let mut config = if config_path.exists() {
            let contents = fs::read_to_string(&config_path)
                .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?;
            serde_json::from_str(&contents)
                .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?
        } else {
            ClaudeJsonConfig::default()
        };

        // Clear and rebuild mcpServers
        config.mcp_servers.clear();
        for (name, mcp_def) in mcp_defs {
            let server_config = self.mcp_def_to_server_config(mcp_def);
            config.mcp_servers.insert(
                name.clone(),
                serde_json::to_value(server_config)
                    .map_err(|e| McpError::ClaudeConfigWriteError(e.to_string()))?,
            );
        }

        self.write_claude_config(&config_path, &config)
    }

    /// Set project MCPs
    fn set_mcps_project(
        &self,
        project_path: &str,
        mcp_defs: &HashMap<String, super::config::MCPDef>,
    ) -> McpResult<()> {
        let config_path = get_claude_config_path()?;

        // Read existing config
        let mut config = if config_path.exists() {
            let contents = fs::read_to_string(&config_path)
                .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?;
            serde_json::from_str(&contents)
                .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?
        } else {
            ClaudeJsonConfig::default()
        };

        // Get or create project
        let project = config
            .projects
            .entry(project_path.to_string())
            .or_insert_with(ClaudeProjectConfig::default);

        // Clear and rebuild mcpServers
        project.mcp_servers.clear();
        for (name, mcp_def) in mcp_defs {
            let server_config = self.mcp_def_to_server_config(mcp_def);
            project.mcp_servers.insert(
                name.clone(),
                serde_json::to_value(server_config)
                    .map_err(|e| McpError::ClaudeConfigWriteError(e.to_string()))?,
            );
        }

        self.write_claude_config(&config_path, &config)
    }

    /// Set local MCPs
    fn set_mcps_local(
        &self,
        project_path: &str,
        mcp_defs: &HashMap<String, super::config::MCPDef>,
    ) -> McpResult<()> {
        let mcp_json_path = PathBuf::from(project_path).join(".mcp.json");

        // Create new config
        let mut config = McpJsonConfig::default();

        // Build mcpServers
        for (name, mcp_def) in mcp_defs {
            let server_config = self.mcp_def_to_server_config(mcp_def);
            config.mcp_servers.insert(name.clone(), server_config);
        }

        self.write_mcp_json(&mcp_json_path, &config)
    }

    /// Clear all MCPs from a scope
    ///
    /// # Arguments
    /// * `scope` - Attachment scope (Global, Project, or Local)
    /// * `project_path` - Project directory path (required for Project and Local scopes)
    pub fn clear_mcps(&self, scope: McpScope, project_path: Option<&str>) -> McpResult<()> {
        match scope {
            McpScope::Global => self.clear_mcps_global(),
            McpScope::Project => {
                let path = project_path.ok_or_else(|| {
                    McpError::InvalidInput("project_path required for project scope".to_string())
                })?;
                self.clear_mcps_project(path)
            }
            McpScope::Local => {
                let path = project_path.ok_or_else(|| {
                    McpError::InvalidInput("project_path required for local scope".to_string())
                })?;
                self.clear_mcps_local(path)
            }
        }
    }

    /// Clear global MCPs
    fn clear_mcps_global(&self) -> McpResult<()> {
        let config_path = get_claude_config_path()?;

        if !config_path.exists() {
            return Ok(());
        }

        let contents = fs::read_to_string(&config_path)
            .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?;

        let mut config: ClaudeJsonConfig = serde_json::from_str(&contents)
            .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?;

        config.mcp_servers.clear();

        self.write_claude_config(&config_path, &config)
    }

    /// Clear project MCPs
    fn clear_mcps_project(&self, project_path: &str) -> McpResult<()> {
        let config_path = get_claude_config_path()?;

        if !config_path.exists() {
            return Ok(());
        }

        let contents = fs::read_to_string(&config_path)
            .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?;

        let mut config: ClaudeJsonConfig = serde_json::from_str(&contents)
            .map_err(|e| McpError::ClaudeConfigReadError(e.to_string()))?;

        if let Some(project) = config.projects.get_mut(project_path) {
            project.mcp_servers.clear();
        }

        self.write_claude_config(&config_path, &config)
    }

    /// Clear local MCPs (delete .mcp.json)
    fn clear_mcps_local(&self, project_path: &str) -> McpResult<()> {
        let mcp_json_path = PathBuf::from(project_path).join(".mcp.json");

        if mcp_json_path.exists() {
            fs::remove_file(&mcp_json_path)
                .map_err(|e| McpError::IoError(format!("remove .mcp.json: {}", e)))?;
        }

        Ok(())
    }

    /// Create example config.toml if it doesn't exist
    pub fn create_example_config(&self) -> McpResult<()> {
        let config_path = get_config_path()?;

        if config_path.exists() {
            return Ok(()); // Don't overwrite
        }

        // Ensure directory exists
        if let Some(parent) = config_path.parent() {
            fs::create_dir_all(parent)
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
    use tempfile::TempDir;

    fn test_manager() -> (TempDir, McpManager) {
        let temp = TempDir::new().unwrap();
        let manager = McpManager::new();
        (temp, manager)
    }

    #[test]
    fn test_load_config_default() {
        let (_temp, manager) = test_manager();
        let config = manager.load_config().unwrap();
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
