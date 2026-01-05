use serde::Serialize;
use std::fmt;

/// Error types for MCP operations
#[derive(Debug, Clone, Serialize)]
#[serde(tag = "type", content = "message")]
pub enum McpError {
    /// Config file not found
    ConfigNotFound(String),
    /// Failed to read config file
    ConfigReadError(String),
    /// Failed to write config file
    ConfigWriteError(String),
    /// Failed to parse config file
    ConfigParseError(String),
    /// MCP definition not found
    MCPNotFound(String),
    /// Failed to write .mcp.json file
    McpJsonWriteError(String),
    /// Failed to read Claude config
    ClaudeConfigReadError(String),
    /// Failed to write Claude config
    ClaudeConfigWriteError(String),
    /// Invalid MCP configuration
    InvalidConfig(String),
    /// IO error
    IoError(String),
    /// Invalid input parameter
    InvalidInput(String),
}

impl fmt::Display for McpError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            McpError::ConfigNotFound(path) => write!(f, "config not found: {}", path),
            McpError::ConfigReadError(msg) => write!(f, "failed to read config: {}", msg),
            McpError::ConfigWriteError(msg) => write!(f, "failed to write config: {}", msg),
            McpError::ConfigParseError(msg) => write!(f, "failed to parse config: {}", msg),
            McpError::MCPNotFound(name) => write!(f, "MCP not found: {}", name),
            McpError::McpJsonWriteError(msg) => write!(f, "failed to write .mcp.json: {}", msg),
            McpError::ClaudeConfigReadError(msg) => {
                write!(f, "failed to read Claude config: {}", msg)
            }
            McpError::ClaudeConfigWriteError(msg) => {
                write!(f, "failed to write Claude config: {}", msg)
            }
            McpError::InvalidConfig(msg) => write!(f, "invalid MCP configuration: {}", msg),
            McpError::IoError(msg) => write!(f, "IO error: {}", msg),
            McpError::InvalidInput(msg) => write!(f, "invalid input: {}", msg),
        }
    }
}

impl std::error::Error for McpError {}

impl From<std::io::Error> for McpError {
    fn from(e: std::io::Error) -> Self {
        McpError::IoError(e.to_string())
    }
}

impl From<toml::de::Error> for McpError {
    fn from(e: toml::de::Error) -> Self {
        McpError::ConfigParseError(e.to_string())
    }
}

impl From<toml::ser::Error> for McpError {
    fn from(e: toml::ser::Error) -> Self {
        McpError::ConfigWriteError(e.to_string())
    }
}

impl From<serde_json::Error> for McpError {
    fn from(e: serde_json::Error) -> Self {
        McpError::ConfigParseError(e.to_string())
    }
}

/// Result type for MCP operations
pub type McpResult<T> = Result<T, McpError>;

impl From<McpError> for String {
    fn from(e: McpError) -> Self {
        e.to_string()
    }
}
