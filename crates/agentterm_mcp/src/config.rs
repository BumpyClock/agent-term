use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::fs;
use std::path::PathBuf;

use super::error::{McpError, McpResult};

/// User configuration loaded from ~/.agent-term/config.toml
#[derive(Debug, Clone, Deserialize, Serialize)]
pub struct UserConfig {
    /// Enable debug/diagnostic logging
    #[serde(default)]
    pub debug: bool,

    /// Default tool for new sessions
    #[serde(default)]
    pub default_tool: String,

    /// Custom tool definitions
    #[serde(default)]
    pub tools: HashMap<String, ToolDef>,

    /// Shell settings
    #[serde(default)]
    pub shell: ShellSettings,

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
            debug: false,
            default_tool: String::new(),
            tools: HashMap::new(),
            shell: ShellSettings::default(),
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

    /// Command-line arguments
    #[serde(default)]
    pub args: Vec<String>,

    /// Icon/symbol to display
    #[serde(default)]
    pub icon: String,

    /// Human-readable description
    #[serde(default)]
    pub description: String,

    /// Patterns that indicate the tool is busy
    #[serde(default)]
    pub busy_patterns: Vec<String>,

    /// Whether this is a shell (uses shell-specific args like -l -i)
    #[serde(default)]
    pub is_shell: bool,

    /// Display order in the picker
    #[serde(default)]
    pub order: i32,

    /// Whether this tool is enabled
    #[serde(default = "default_tool_enabled")]
    pub enabled: bool,
}

fn default_tool_enabled() -> bool {
    true
}

/// Shell-specific settings
#[derive(Debug, Clone, Deserialize, Serialize)]
pub struct ShellSettings {
    /// Override default shell path (empty = auto-detect)
    #[serde(default)]
    pub default_shell: String,

    /// Additional shell arguments
    #[serde(default)]
    pub default_shell_args: Vec<String>,

    /// IDs of shells pinned to the top-level menu
    #[serde(default)]
    pub pinned_shells: Vec<String>,
}

impl Default for ShellSettings {
    fn default() -> Self {
        Self {
            default_shell: String::new(),
            default_shell_args: Vec::new(),
            pinned_shells: Vec::new(),
        }
    }
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
#[serde(rename_all = "camelCase")]
pub struct UpdateSettings {
    /// Auto-update without prompting (auto-download when update available)
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

    /// Last time we checked for updates (ISO 8601 string)
    #[serde(default)]
    pub last_check_time: Option<String>,
}

impl Default for UpdateSettings {
    fn default() -> Self {
        Self {
            auto_update: false,
            check_enabled: default_check_enabled(),
            check_interval_hours: default_check_interval_hours(),
            notify_in_cli: default_notify_in_cli(),
            last_check_time: None,
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
    #[serde(rename = "type", default, skip_serializing_if = "Option::is_none")]
    pub server_type: Option<String>,

    /// Command to run
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub command: String,

    /// Command arguments
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub args: Vec<String>,

    /// Environment variables
    #[serde(default, skip_serializing_if = "HashMap::is_empty")]
    pub env: HashMap<String, String>,

    /// URL for HTTP/SSE transport
    #[serde(default, skip_serializing_if = "String::is_empty")]
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

/// Get the path to the agent-term config directory
/// Checks AGENT_TERM_HOME env var first for testing, then defaults to ~/.agent-term
pub fn get_agent_term_dir() -> McpResult<PathBuf> {
    // Allow tests to override via env var
    if let Ok(custom_dir) = std::env::var("AGENT_TERM_HOME") {
        return Ok(PathBuf::from(custom_dir));
    }

    let home = dirs::home_dir()
        .ok_or_else(|| McpError::ConfigNotFound("Home directory not found".to_string()))?;

    Ok(home.join(".agent-term"))
}

pub fn get_agent_term_mcp_dir() -> McpResult<PathBuf> {
    Ok(get_agent_term_dir()?.join("mcp"))
}

pub fn get_agent_term_mcp_run_dir() -> McpResult<PathBuf> {
    Ok(get_agent_term_dir()?.join("run").join("mcp"))
}

pub fn get_managed_global_mcp_path() -> McpResult<PathBuf> {
    Ok(get_agent_term_mcp_dir()?.join("global.mcp.json"))
}

pub fn get_user_project_mcp_path(project_path: &str) -> PathBuf {
    PathBuf::from(project_path).join(".mcp.json")
}

/// Get the AgentTerm-managed MCP config path for a project
/// Stores in ~/.agent-term/project-configs/{project-identifier}/.mcp.json
/// This keeps AgentTerm's MCP configs separate from the user's project .mcp.json
pub fn get_managed_project_mcp_path(project_path: &str) -> McpResult<PathBuf> {
    let identifier = project_path_to_identifier(project_path);
    let dir = get_agent_term_dir()?
        .join("project-configs")
        .join(&identifier);
    Ok(dir.join(".mcp.json"))
}

/// Generate a unique, readable identifier for a project path
/// Format: {sanitized_name}-{hash_suffix}
/// Example: /Users/john/projects/my-app -> my-app-a1b2c3d4
fn project_path_to_identifier(project_path: &str) -> String {
    use std::collections::hash_map::DefaultHasher;
    use std::hash::{Hash, Hasher};

    let path = std::path::Path::new(project_path);
    let name = path
        .file_name()
        .and_then(|n| n.to_str())
        .unwrap_or("project");

    let mut hasher = DefaultHasher::new();
    project_path.hash(&mut hasher);
    let hash = hasher.finish();
    let hash_suffix = format!("{:x}", hash).chars().take(8).collect::<String>();

    let sanitized = sanitize_project_name(name);
    format!("{}-{}", sanitized, hash_suffix)
}

fn sanitize_project_name(name: &str) -> String {
    let sanitized: String = name
        .chars()
        .map(|c| {
            if c.is_ascii_alphanumeric() || c == '-' || c == '_' {
                c
            } else if c == ' ' {
                '-'
            } else {
                '_'
            }
        })
        .collect();
    if sanitized.is_empty() {
        "project".to_string()
    } else {
        sanitized.to_lowercase()
    }
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

    // Check user config for an explicit Claude config dir
    if let Ok(config_path) = get_config_path() {
        if config_path.exists() {
            if let Ok(contents) = fs::read_to_string(&config_path) {
                if let Ok(config) = toml::from_str::<UserConfig>(&contents) {
                    let configured = config.claude.config_dir.trim();
                    if !configured.is_empty() {
                        return Ok(expand_tilde(configured));
                    }
                }
            }
        }
    }

    // Default to ~/.claude
    let home = dirs::home_dir()
        .ok_or_else(|| McpError::ConfigNotFound("Home directory not found".to_string()))?;

    Ok(home.join(".claude"))
}

/// Expand tilde in path (cross-platform)
pub fn expand_tilde(path: &str) -> PathBuf {
    if path.starts_with("~/") || path.starts_with("~\\") {
        if let Some(home) = dirs::home_dir() {
            let rest = &path[2..]; // Skip "~/" or "~\"
            return home.join(rest);
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
