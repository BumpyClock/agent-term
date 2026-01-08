//! Tool icon loading from disk.
//!
//! Tool icons are loaded at runtime from the config directory,
//! allowing them to be updated without recompiling.

use std::path::PathBuf;

/// Metadata for a tool icon.
#[derive(Debug, Clone)]
pub struct ToolIconInfo {
    /// Unique identifier
    pub id: &'static str,
    /// Display label
    pub label: &'static str,
    /// Filename in the tool-icons directory
    pub filename: &'static str,
    /// Whether to render as monochrome (applies theme color)
    pub monochrome: bool,
}

/// All available tool icons.
pub const TOOL_ICONS: &[ToolIconInfo] = &[
    ToolIconInfo { id: "claude", label: "Claude", filename: "claude.svg", monochrome: false },
    ToolIconInfo { id: "claude-logo", label: "Claude Logo", filename: "claude-logo.svg", monochrome: false },
    ToolIconInfo { id: "anthropic", label: "Anthropic", filename: "anthropic-logo.svg", monochrome: true },
    ToolIconInfo { id: "openai", label: "OpenAI", filename: "openai.svg", monochrome: true },
    ToolIconInfo { id: "git", label: "Git", filename: "git.svg", monochrome: true },
    ToolIconInfo { id: "git-bash", label: "Git Bash", filename: "git-bash.svg", monochrome: false },
    ToolIconInfo { id: "mcp", label: "MCP", filename: "mcp.svg", monochrome: true },
    ToolIconInfo { id: "cursor", label: "Cursor", filename: "cursor.svg", monochrome: false },
    ToolIconInfo { id: "vscode", label: "VS Code", filename: "Visual_Studio_Code_1.35_icon.svg", monochrome: false },
    ToolIconInfo { id: "python", label: "Python", filename: "Python-logo-notext.svg", monochrome: false },
    ToolIconInfo { id: "react", label: "React", filename: "React-icon.svg", monochrome: false },
    ToolIconInfo { id: "gemini", label: "Gemini", filename: "googlegemini.svg", monochrome: false },
    ToolIconInfo { id: "google", label: "Google", filename: "google-logo.svg", monochrome: false },
    ToolIconInfo { id: "ollama", label: "Ollama", filename: "Ollama.png", monochrome: false },
    ToolIconInfo { id: "openrouter", label: "OpenRouter", filename: "OpenRouter.png", monochrome: false },
    ToolIconInfo { id: "grok", label: "Grok", filename: "Grok.png", monochrome: true },
    ToolIconInfo { id: "windsurf", label: "Windsurf", filename: "windsurf-white-symbol.svg", monochrome: true },
    ToolIconInfo { id: "ubuntu", label: "Ubuntu", filename: "ubuntu.svg", monochrome: false },
    ToolIconInfo { id: "arch", label: "Arch Linux", filename: "archlinux.svg", monochrome: true },
    ToolIconInfo { id: "debian", label: "Debian", filename: "debian.svg", monochrome: false },
    ToolIconInfo { id: "fedora", label: "Fedora", filename: "fedora.svg", monochrome: false },
    ToolIconInfo { id: "bash", label: "Bash", filename: "bash.svg", monochrome: true },
    ToolIconInfo { id: "powershell", label: "PowerShell", filename: "powershell.svg", monochrome: false },
    ToolIconInfo { id: "linux", label: "Linux", filename: "linux.svg", monochrome: true },
    ToolIconInfo { id: "bun", label: "Bun", filename: "Bun.svg", monochrome: false },
];

/// Get the tool icons directory path.
/// Checks app bundle resources first, then falls back to config directory.
pub fn tool_icons_dir() -> PathBuf {
    // Check app bundle resources first (for packaged macOS app)
    #[cfg(target_os = "macos")]
    if let Ok(exe) = std::env::current_exe() {
        if let Some(parent) = exe.parent() {
            if let Some(grandparent) = parent.parent() {
                let resources = grandparent.join("Resources/tool-icons");
                if resources.exists() {
                    return resources;
                }
            }
        }
    }

    // Fall back to config directory
    dirs::config_dir()
        .unwrap_or_else(|| PathBuf::from("."))
        .join("agentterm/tool-icons")
}

/// Get the full path to a tool icon file.
pub fn tool_icon_path(filename: &str) -> PathBuf {
    tool_icons_dir().join(filename)
}

/// Find tool icon info by ID.
pub fn find_tool_icon(id: &str) -> Option<&'static ToolIconInfo> {
    TOOL_ICONS.iter().find(|t| t.id == id)
}

/// Check if a tool icon file exists.
pub fn tool_icon_exists(filename: &str) -> bool {
    tool_icon_path(filename).exists()
}
