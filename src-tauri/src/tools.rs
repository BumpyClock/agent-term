//! Tools and shells configuration management

use crate::mcp::config::{ShellSettings, ToolDef};
use crate::mcp::McpManager;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use tauri::State;

/// Tool info returned to frontend
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ToolInfo {
    pub id: String,
    pub name: String,
    pub command: String,
    pub args: Vec<String>,
    pub icon: String,
    pub description: String,
    pub is_shell: bool,
    pub order: i32,
    pub enabled: bool,
    pub is_builtin: bool,
}

/// Settings payload for tools/shells (DTO for frontend)
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ToolsSettingsDto {
    pub tools: HashMap<String, ToolDefDto>,
    pub shell: ShellSettingsDto,
}

/// ToolDef DTO for frontend
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ToolDefDto {
    pub command: String,
    #[serde(default)]
    pub args: Vec<String>,
    #[serde(default)]
    pub icon: String,
    #[serde(default)]
    pub description: String,
    #[serde(default)]
    pub busy_patterns: Vec<String>,
    #[serde(default)]
    pub is_shell: bool,
    #[serde(default)]
    pub order: i32,
    #[serde(default = "default_enabled")]
    pub enabled: bool,
}

fn default_enabled() -> bool {
    true
}

/// ShellSettings DTO for frontend
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ShellSettingsDto {
    #[serde(default)]
    pub default_shell: String,
    #[serde(default)]
    pub default_shell_args: Vec<String>,
}

impl Default for ShellSettingsDto {
    fn default() -> Self {
        Self {
            default_shell: String::new(),
            default_shell_args: Vec::new(),
        }
    }
}

/// Get all configured tools (custom + built-in)
#[tauri::command(rename_all = "camelCase")]
pub async fn tools_list(state: State<'_, McpManager>) -> Result<Vec<ToolInfo>, String> {
    let config = state.load_config().await.map_err(|e| e.to_string())?;

    let mut tools: Vec<ToolInfo> = Vec::new();

    // Add built-in tools
    tools.extend(get_builtin_tools());

    // Add custom tools from config
    for (name, def) in config.tools {
        tools.push(ToolInfo {
            id: name.clone(),
            name,
            command: def.command,
            args: def.args,
            icon: def.icon,
            description: def.description,
            is_shell: def.is_shell,
            order: def.order,
            enabled: def.enabled,
            is_builtin: false,
        });
    }

    // Sort by order, then by name
    tools.sort_by(|a, b| a.order.cmp(&b.order).then_with(|| a.name.cmp(&b.name)));

    Ok(tools)
}

/// Get tools settings
#[tauri::command(rename_all = "camelCase")]
pub async fn tools_get_settings(state: State<'_, McpManager>) -> Result<ToolsSettingsDto, String> {
    let config = state.load_config().await.map_err(|e| e.to_string())?;

    let tools: HashMap<String, ToolDefDto> = config
        .tools
        .into_iter()
        .map(|(name, def)| {
            (
                name,
                ToolDefDto {
                    command: def.command,
                    args: def.args,
                    icon: def.icon,
                    description: def.description,
                    busy_patterns: def.busy_patterns,
                    is_shell: def.is_shell,
                    order: def.order,
                    enabled: def.enabled,
                },
            )
        })
        .collect();

    Ok(ToolsSettingsDto {
        tools,
        shell: ShellSettingsDto {
            default_shell: config.shell.default_shell,
            default_shell_args: config.shell.default_shell_args,
        },
    })
}

/// Save tools settings
#[tauri::command(rename_all = "camelCase")]
pub async fn tools_set_settings(
    state: State<'_, McpManager>,
    settings: ToolsSettingsDto,
) -> Result<(), String> {
    let mut config = state.load_config().await.map_err(|e| e.to_string())?;

    // Convert DTOs back to internal structs
    config.tools = settings
        .tools
        .into_iter()
        .map(|(name, def)| {
            (
                name,
                ToolDef {
                    command: def.command,
                    args: def.args,
                    icon: def.icon,
                    description: def.description,
                    busy_patterns: def.busy_patterns,
                    is_shell: def.is_shell,
                    order: def.order,
                    enabled: def.enabled,
                },
            )
        })
        .collect();

    config.shell = ShellSettings {
        default_shell: settings.shell.default_shell,
        default_shell_args: settings.shell.default_shell_args,
        pinned_shells: config.shell.pinned_shells,
    };

    state
        .write_config(&config)
        .await
        .map_err(|e| e.to_string())
}

/// Get the resolved default shell (considers user override)
#[tauri::command(rename_all = "camelCase")]
pub async fn get_resolved_shell(state: State<'_, McpManager>) -> Result<String, String> {
    let config = state.load_config().await.map_err(|e| e.to_string())?;

    // If user has configured a custom shell, use that
    if !config.shell.default_shell.is_empty() {
        return Ok(config.shell.default_shell);
    }

    // Otherwise use auto-detection
    Ok(crate::detect_default_shell())
}

fn get_builtin_tools() -> Vec<ToolInfo> {
    vec![
        ToolInfo {
            id: "claude".to_string(),
            name: "Claude Code".to_string(),
            command: "claude".to_string(),
            args: vec![],
            icon: "/tool-icons/claude-logo.svg".to_string(),
            description: "Anthropic Claude AI assistant".to_string(),
            is_shell: false,
            order: -100,
            enabled: true,
            is_builtin: true,
        },
        ToolInfo {
            id: "gemini".to_string(),
            name: "Gemini".to_string(),
            command: "gemini".to_string(),
            args: vec![],
            icon: "/tool-icons/google-logo.svg".to_string(),
            description: "Google Gemini AI assistant".to_string(),
            is_shell: false,
            order: -90,
            enabled: true,
            is_builtin: true,
        },
        ToolInfo {
            id: "codex".to_string(),
            name: "Codex".to_string(),
            command: "codex".to_string(),
            args: vec![],
            icon: "/tool-icons/OpenAI.png".to_string(),
            description: "OpenAI Codex".to_string(),
            is_shell: false,
            order: -80,
            enabled: true,
            is_builtin: true,
        },
        ToolInfo {
            id: "openCode".to_string(),
            name: "OpenCode".to_string(),
            command: "opencode".to_string(),
            args: vec![],
            icon: "/tool-icons/Visual_Studio_Code_1.35_icon.svg".to_string(),
            description: "OpenCode assistant".to_string(),
            is_shell: false,
            order: -70,
            enabled: true,
            is_builtin: true,
        },
    ]
}
