use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::path::PathBuf;

use super::error::{McpError, McpResult};

/// User configuration loaded from ~/.agent-term/config.toml
#[derive(Debug, Clone, Deserialize, Serialize)]
pub struct UserConfig {
    /// Default tool for new sessions
    #[serde(default)]
    pub default_tool: String,

    /// Custom tool definitions
    #[serde(default)]
    pub tools: HashMap<String, ToolDef>,

    /// MCP server definitions
    #[serde(default)]
    pub mcps: HashMap<String, MCPDef>,

    /// Claude-specific settings
    #[serde(default)]
    pub claude: ClaudeSettings,

    /// Log settings
    #[serde(default)]
    pub logs: LogSettings,

    /// Global search settings
    #[serde(default)]
    pub global_search: GlobalSearchSettings,

    /// MCP pool settings
    #[serde(default)]
    pub mcp_pool: MCPPoolSettings,

    /// Update settings
    #[serde(default)]
    pub updates: UpdateSettings,
}

impl Default for UserConfig {
    fn default() -> Self {
        Self {
            default_tool: String::new(),
            tools: HashMap::new(),
            mcps: HashMap::new(),
            claude: ClaudeSettings::default(),
            logs: LogSettings::default(),
            global_search: GlobalSearchSettings::default(),
            mcp_pool: MCPPoolSettings::default(),
            updates: UpdateSettings::default(),
        }
    }
}

/// Custom tool definition
#[derive(Debug, Clone, Deserialize, Serialize)]
pub struct ToolDef {
    /// Shell command to run
    pub command: String,

    /// Icon/symbol to display
    #[serde(default)]
    pub icon: String,

    /// Patterns that indicate the tool is busy
    #[serde(default)]
    pub busy_patterns: Vec<String>,
}

/// MCP server definition
#[derive(Debug, Clone, Deserialize, Serialize)]
pub struct MCPDef {
    /// Command to run (for stdio MCPs)
    #[serde(default)]
    pub command: String,

    /// Command-line arguments
    #[serde(default)]
    pub args: Vec<String>,

    /// Environment variables
    #[serde(default)]
    pub env: HashMap<String, String>,

    /// Description shown in MCP manager
    #[serde(default)]
    pub description: String,

    /// URL for HTTP/SSE MCPs
    #[serde(default)]
    pub url: String,

    /// Transport type: "stdio", "http", or "sse"
    #[serde(default)]
    pub transport: String,
}

impl Default for MCPDef {
    fn default() -> Self {
        Self {
            command: String::new(),
            args: Vec::new(),
            env: HashMap::new(),
            description: String::new(),
            url: String::new(),
            transport: String::new(),
        }
    }
}

/// Claude Code integration settings
#[derive(Debug, Clone, Deserialize, Serialize)]
pub struct ClaudeSettings {
    /// Path to Claude's config directory
    #[serde(default = "default_claude_config_dir")]
    pub config_dir: String,

    /// Enable dangerous mode
    #[serde(default)]
    pub dangerous_mode: bool,
}

impl Default for ClaudeSettings {
    fn default() -> Self {
        Self {
            config_dir: default_claude_config_dir(),
            dangerous_mode: false,
        }
    }
}

fn default_claude_config_dir() -> String {
    // Will be resolved to actual path at runtime
    "~/.claude".to_string()
}

/// Log file management settings
#[derive(Debug, Clone, Deserialize, Serialize)]
pub struct LogSettings {
    /// Maximum size in MB before truncation
    #[serde(default = "default_max_size_mb")]
    pub max_size_mb: i32,

    /// Number of lines to keep when truncating
    #[serde(default = "default_max_lines")]
    pub max_lines: i32,

    /// Remove orphan log files
    #[serde(default = "default_remove_orphans")]
    pub remove_orphans: bool,
}

impl Default for LogSettings {
    fn default() -> Self {
        Self {
            max_size_mb: default_max_size_mb(),
            max_lines: default_max_lines(),
            remove_orphans: default_remove_orphans(),
        }
    }
}

fn default_max_size_mb() -> i32 {
    10
}

fn default_max_lines() -> i32 {
    10000
}

fn default_remove_orphans() -> bool {
    true
}

/// Global search settings
#[derive(Debug, Clone, Deserialize, Serialize)]
pub struct GlobalSearchSettings {
    /// Enable/disable global search
    #[serde(default = "default_global_search_enabled")]
    pub enabled: bool,

    /// Search tier: "auto", "instant", "balanced", "disabled"
    #[serde(default)]
    pub tier: String,

    /// Memory limit in MB
    #[serde(default = "default_memory_limit_mb")]
    pub memory_limit_mb: i32,

    /// Recent days to limit search
    #[serde(default = "default_recent_days")]
    pub recent_days: i32,

    /// Index rate limit
    #[serde(default = "default_index_rate_limit")]
    pub index_rate_limit: i32,
}

impl Default for GlobalSearchSettings {
    fn default() -> Self {
        Self {
            enabled: default_global_search_enabled(),
            tier: String::from("auto"),
            memory_limit_mb: default_memory_limit_mb(),
            recent_days: default_recent_days(),
            index_rate_limit: default_index_rate_limit(),
        }
    }
}

fn default_global_search_enabled() -> bool {
    true
}

fn default_memory_limit_mb() -> i32 {
    100
}

fn default_recent_days() -> i32 {
    90
}

fn default_index_rate_limit() -> i32 {
    20
}

/// MCP pool settings
#[derive(Debug, Clone, Deserialize, Serialize)]
pub struct MCPPoolSettings {
    /// Enable HTTP pool mode
    #[serde(default)]
    pub enabled: bool,

    /// Auto-start pool on launch
    #[serde(default = "default_pool_auto_start")]
    pub auto_start: bool,

    /// Port range start
    #[serde(default = "default_port_start")]
    pub port_start: i32,

    /// Port range end
    #[serde(default = "default_port_end")]
    pub port_end: i32,

    /// Start on demand
    #[serde(default)]
    pub start_on_demand: bool,

    /// Shutdown on exit
    #[serde(default = "default_shutdown_on_exit")]
    pub shutdown_on_exit: bool,

    /// MCPs to run in pool mode
    #[serde(default)]
    pub pool_mcps: Vec<String>,

    /// Fallback to stdio
    #[serde(default = "default_fallback_stdio")]
    pub fallback_to_stdio: bool,

    /// Show pool status
    #[serde(default = "default_show_pool_status")]
    pub show_pool_status: bool,

    /// Pool all MCPs
    #[serde(default)]
    pub pool_all: bool,

    /// Exclude MCPs from pool
    #[serde(default)]
    pub exclude_mcps: Vec<String>,
}

impl Default for MCPPoolSettings {
    fn default() -> Self {
        Self {
            enabled: false,
            auto_start: default_pool_auto_start(),
            port_start: default_port_start(),
            port_end: default_port_end(),
            start_on_demand: false,
            shutdown_on_exit: default_shutdown_on_exit(),
            pool_mcps: Vec::new(),
            fallback_to_stdio: default_fallback_stdio(),
            show_pool_status: default_show_pool_status(),
            pool_all: false,
            exclude_mcps: Vec::new(),
        }
    }
}

fn default_pool_auto_start() -> bool {
    true
}

fn default_port_start() -> i32 {
    8001
}

fn default_port_end() -> i32 {
    8050
}

fn default_shutdown_on_exit() -> bool {
    true
}

fn default_fallback_stdio() -> bool {
    true
}

fn default_show_pool_status() -> bool {
    true
}

/// Update settings
#[derive(Debug, Clone, Deserialize, Serialize)]
pub struct UpdateSettings {
    /// Auto-update without prompting
    #[serde(default)]
    pub auto_update: bool,

    /// Enable update checks
    #[serde(default = "default_check_enabled")]
    pub check_enabled: bool,

    /// Check interval in hours
    #[serde(default = "default_check_interval_hours")]
    pub check_interval_hours: i32,

    /// Notify in CLI
    #[serde(default = "default_notify_in_cli")]
    pub notify_in_cli: bool,
}

impl Default for UpdateSettings {
    fn default() -> Self {
        Self {
            auto_update: false,
            check_enabled: default_check_enabled(),
            check_interval_hours: default_check_interval_hours(),
            notify_in_cli: default_notify_in_cli(),
        }
    }
}

fn default_check_enabled() -> bool {
    true
}

fn default_check_interval_hours() -> i32 {
    24
}

fn default_notify_in_cli() -> bool {
    true
}

/// MCP server configuration for Claude's .mcp.json format
#[derive(Debug, Clone, Deserialize, Serialize)]
pub struct MCPServerConfig {
    /// Transport type
    #[serde(rename = "type", skip_serializing_if = "Option::is_none")]
    pub server_type: Option<String>,

    /// Command to run
    #[serde(skip_serializing_if = "String::is_empty")]
    pub command: String,

    /// Command arguments
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub args: Vec<String>,

    /// Environment variables
    #[serde(skip_serializing_if = "HashMap::is_empty")]
    pub env: HashMap<String, String>,

    /// URL for HTTP/SSE transport
    #[serde(skip_serializing_if = "String::is_empty")]
    pub url: String,
}

impl Default for MCPServerConfig {
    fn default() -> Self {
        Self {
            server_type: None,
            command: String::new(),
            args: Vec::new(),
            env: HashMap::new(),
            url: String::new(),
        }
    }
}

/// .mcp.json file structure
#[derive(Debug, Clone, Deserialize, Serialize)]
pub struct McpJsonConfig {
    /// MCP servers configuration
    #[serde(rename = "mcpServers", default)]
    pub mcp_servers: HashMap<String, MCPServerConfig>,
}

impl Default for McpJsonConfig {
    fn default() -> Self {
        Self {
            mcp_servers: HashMap::new(),
        }
    }
}

/// Claude's .claude.json structure
#[derive(Debug, Clone, Deserialize, Serialize)]
pub struct ClaudeJsonConfig {
    /// MCP servers (global scope)
    #[serde(rename = "mcpServers", default)]
    pub mcp_servers: HashMap<String, serde_json::Value>,

    /// Project-specific configurations
    #[serde(default)]
    pub projects: HashMap<String, ClaudeProjectConfig>,
}

impl Default for ClaudeJsonConfig {
    fn default() -> Self {
        Self {
            mcp_servers: HashMap::new(),
            projects: HashMap::new(),
        }
    }
}

/// Claude project-specific configuration
#[derive(Debug, Clone, Deserialize, Serialize)]
pub struct ClaudeProjectConfig {
    /// MCP servers for this project
    #[serde(rename = "mcpServers", default)]
    pub mcp_servers: HashMap<String, serde_json::Value>,
}

impl Default for ClaudeProjectConfig {
    fn default() -> Self {
        Self {
            mcp_servers: HashMap::new(),
        }
    }
}

/// Get the path to the agent-term config directory
pub fn get_agent_term_dir() -> McpResult<PathBuf> {
    let home = dirs::home_dir().ok_or_else(|| McpError::ConfigNotFound(
        "Home directory not found".to_string(),
    ))?;

    Ok(home.join(".agent-term"))
}

/// Get the path to the agent-term config.toml file
pub fn get_config_path() -> McpResult<PathBuf> {
    let dir = get_agent_term_dir()?;
    Ok(dir.join("config.toml"))
}

/// Get the Claude config directory
/// Checks CLAUDE_CONFIG_DIR env var first, then defaults to ~/.claude
pub fn get_claude_config_dir() -> McpResult<PathBuf> {
    // Check environment variable first
    if let Ok(custom_dir) = std::env::var("CLAUDE_CONFIG_DIR") {
        return Ok(PathBuf::from(custom_dir));
    }

    // Default to ~/.claude
    let home = dirs::home_dir().ok_or_else(|| McpError::ConfigNotFound(
        "Home directory not found".to_string(),
    ))?;

    Ok(home.join(".claude"))
}

/// Get the path to Claude's .claude.json file
pub fn get_claude_config_path() -> McpResult<PathBuf> {
    let dir = get_claude_config_dir()?;
    Ok(dir.join(".claude.json"))
}

/// Expand tilde in path
pub fn expand_tilde(path: &str) -> PathBuf {
    if path.starts_with("~/") {
        if let Ok(home) = std::env::var("HOME") {
            return PathBuf::from(path.replacen("~", &home, 1));
        }
    }
    PathBuf::from(path)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_default_user_config() {
        let config = UserConfig::default();
        assert_eq!(config.default_tool, "");
        assert!(config.tools.is_empty());
        assert!(config.mcps.is_empty());
        assert!(config.claude.config_dir.contains(".claude"));
    }

    #[test]
    fn test_mcp_def_default() {
        let mcp = MCPDef::default();
        assert_eq!(mcp.command, "");
        assert!(mcp.args.is_empty());
        assert!(mcp.env.is_empty());
    }

    #[test]
    fn test_mcp_server_config_default() {
        let config = MCPServerConfig::default();
        assert!(config.server_type.is_none());
        assert_eq!(config.command, "");
        assert!(config.args.is_empty());
    }

    #[test]
    fn test_log_settings_defaults() {
        let settings = LogSettings::default();
        assert_eq!(settings.max_size_mb, 10);
        assert_eq!(settings.max_lines, 10000);
        assert!(settings.remove_orphans);
    }

    #[test]
    fn test_mcp_pool_settings_defaults() {
        let settings = MCPPoolSettings::default();
        assert_eq!(settings.port_start, 8001);
        assert_eq!(settings.port_end, 8050);
        assert!(settings.auto_start);
        assert!(settings.fallback_to_stdio);
    }
}
