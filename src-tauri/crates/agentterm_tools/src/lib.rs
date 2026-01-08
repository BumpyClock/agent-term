use agentterm_mcp::{McpManager, McpResult};
use serde::{Deserialize, Serialize};
use std::path::PathBuf;

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

pub async fn tools_list(manager: &McpManager) -> McpResult<Vec<ToolInfo>> {
    let config = manager.load_config().await?;

    let mut tools: Vec<ToolInfo> = Vec::new();
    tools.extend(builtin_tools());

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

    tools.sort_by(|a, b| a.order.cmp(&b.order).then_with(|| a.name.cmp(&b.name)));
    Ok(tools)
}

pub async fn get_pinned_shells(manager: &McpManager) -> McpResult<Vec<String>> {
    let config = manager.load_config().await?;
    Ok(config.shell.pinned_shells)
}

pub async fn toggle_pin_shell(manager: &McpManager, shell_id: String) -> McpResult<Vec<String>> {
    let mut config = manager.load_config().await?;

    if let Some(pos) = config.shell.pinned_shells.iter().position(|id| id == &shell_id) {
        config.shell.pinned_shells.remove(pos);
    } else {
        config.shell.pinned_shells.push(shell_id);
    }

    let pinned = config.shell.pinned_shells.clone();
    manager.write_config(&config).await?;
    Ok(pinned)
}

pub async fn get_resolved_shell(manager: &McpManager) -> McpResult<String> {
    let config = manager.load_config().await?;
    if !config.shell.default_shell.is_empty() {
        return Ok(config.shell.default_shell);
    }
    Ok(detect_default_shell())
}

fn builtin_tools() -> Vec<ToolInfo> {
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

pub fn detect_default_shell() -> String {
    #[cfg(not(target_os = "windows"))]
    {
        use users::os::unix::UserExt;

        if let Ok(shell) = std::env::var("SHELL") {
            if !shell.trim().is_empty() {
                return shell;
            }
        }

        if let Some(user) = users::get_user_by_uid(users::get_current_uid()) {
            if let Some(shell_path) = user.shell().to_str() {
                if !shell_path.is_empty() {
                    return shell_path.to_string();
                }
            }
        }

        "/bin/bash".to_string()
    }

    #[cfg(target_os = "windows")]
    {
        if let Ok(output) = std::process::Command::new("where")
            .arg("pwsh.exe")
            .output()
        {
            if output.status.success() {
                if let Some(path) = String::from_utf8_lossy(&output.stdout).lines().next() {
                    if !path.trim().is_empty() {
                        return path.trim().to_string();
                    }
                }
            }
        }

        if let Ok(output) = std::process::Command::new("where")
            .arg("powershell.exe")
            .output()
        {
            if output.status.success() {
                if let Some(path) = String::from_utf8_lossy(&output.stdout).lines().next() {
                    if !path.trim().is_empty() {
                        return path.trim().to_string();
                    }
                }
            }
        }

        std::env::var("COMSPEC").unwrap_or_else(|_| "cmd.exe".to_string())
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum ShellType {
    Native,
    Wsl,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ShellInfo {
    pub id: String,
    pub name: String,
    pub command: String,
    pub args: Vec<String>,
    pub icon: String,
    pub shell_type: ShellType,
    pub is_default: bool,
}

pub fn available_shells() -> Vec<ShellInfo> {
    let mut shells = Vec::new();

    #[cfg(target_os = "windows")]
    {
        shells.extend(detect_windows_shells());
        shells.extend(detect_wsl_distros());
    }

    #[cfg(not(target_os = "windows"))]
    {
        shells.extend(detect_unix_shells());
    }

    shells
}

#[cfg(not(target_os = "windows"))]
fn detect_unix_shells() -> Vec<ShellInfo> {
    use users::os::unix::UserExt;

    let mut shells = Vec::new();
    let default_shell = detect_default_shell();

    let shell_configs = [
        ("bash", "Bash", "/bin/bash", "/tool-icons/bash.svg"),
        ("zsh", "Zsh", "/bin/zsh", "/tool-icons/zsh.svg"),
        ("fish", "Fish", "/usr/bin/fish", "/tool-icons/fish.svg"),
        ("sh", "Shell", "/bin/sh", "/tool-icons/terminal.svg"),
    ];

    for (id, name, path, icon) in shell_configs {
        if PathBuf::from(path).exists() {
            shells.push(ShellInfo {
                id: id.to_string(),
                name: name.to_string(),
                command: path.to_string(),
                args: vec!["-l".to_string(), "-i".to_string()],
                icon: icon.to_string(),
                shell_type: ShellType::Native,
                is_default: default_shell == path,
            });
        }
    }

    if let Some(user) = users::get_user_by_uid(users::get_current_uid()) {
        if let Some(shell_path) = user.shell().to_str() {
            let shell_found = shells.iter().any(|s| s.command == shell_path);
            if !shell_found && PathBuf::from(shell_path).exists() {
                let name = PathBuf::from(shell_path)
                    .file_name()
                    .and_then(|n| n.to_str())
                    .unwrap_or("Shell")
                    .to_string();

                shells.push(ShellInfo {
                    id: name.to_lowercase(),
                    name: name.clone(),
                    command: shell_path.to_string(),
                    args: vec!["-l".to_string(), "-i".to_string()],
                    icon: "/tool-icons/terminal.svg".to_string(),
                    shell_type: ShellType::Native,
                    is_default: true,
                });
            }
        }
    }

    shells
}

#[cfg(target_os = "windows")]
fn detect_windows_shells() -> Vec<ShellInfo> {
    use std::path::PathBuf;

    let mut shells = Vec::new();
    let default_shell = detect_default_shell();

    if let Some(shell) = detect_powershell_7() {
        let is_default = shell.command == default_shell;
        shells.push(ShellInfo { is_default, ..shell });
    }

    if let Some(shell) = detect_windows_powershell() {
        let is_default = shell.command == default_shell;
        shells.push(ShellInfo { is_default, ..shell });
    }

    shells.push(ShellInfo {
        id: "cmd".to_string(),
        name: "Command Prompt".to_string(),
        command: std::env::var("COMSPEC").unwrap_or_else(|_| "cmd.exe".to_string()),
        args: vec![],
        icon: "/tool-icons/cmd.svg".to_string(),
        shell_type: ShellType::Native,
        is_default: default_shell.to_lowercase().contains("cmd"),
    });

    if let Some(shell) = detect_git_bash() {
        shells.push(shell);
    }

    shells
}

#[cfg(target_os = "windows")]
fn detect_powershell_7() -> Option<ShellInfo> {
    let output = std::process::Command::new("where")
        .arg("pwsh.exe")
        .output()
        .ok()?;

    if !output.status.success() {
        return None;
    }

    let path = String::from_utf8_lossy(&output.stdout)
        .lines()
        .next()?
        .trim()
        .to_string();

    if path.is_empty() {
        return None;
    }

    Some(ShellInfo {
        id: "pwsh".to_string(),
        name: "PowerShell 7".to_string(),
        command: path,
        args: vec!["-NoLogo".to_string()],
        icon: "/tool-icons/powershell.svg".to_string(),
        shell_type: ShellType::Native,
        is_default: false,
    })
}

#[cfg(target_os = "windows")]
fn detect_windows_powershell() -> Option<ShellInfo> {
    let output = std::process::Command::new("where")
        .arg("powershell.exe")
        .output()
        .ok()?;

    if !output.status.success() {
        return None;
    }

    let path = String::from_utf8_lossy(&output.stdout)
        .lines()
        .next()?
        .trim()
        .to_string();

    if path.is_empty() {
        return None;
    }

    Some(ShellInfo {
        id: "powershell".to_string(),
        name: "Windows PowerShell".to_string(),
        command: path,
        args: vec!["-NoLogo".to_string()],
        icon: "/tool-icons/powershell-classic.svg".to_string(),
        shell_type: ShellType::Native,
        is_default: false,
    })
}

#[cfg(target_os = "windows")]
fn detect_git_bash() -> Option<ShellInfo> {
    use std::path::PathBuf;

    let common_paths = [
        r"C:\Program Files\Git\bin\bash.exe",
        r"C:\Program Files (x86)\Git\bin\bash.exe",
    ];

    for path in &common_paths {
        if PathBuf::from(path).exists() {
            return Some(ShellInfo {
                id: "git-bash".to_string(),
                name: "Git Bash".to_string(),
                command: path.to_string(),
                args: vec!["--login".to_string(), "-i".to_string()],
                icon: "/tool-icons/git-bash.svg".to_string(),
                shell_type: ShellType::Native,
                is_default: false,
            });
        }
    }

    let output = std::process::Command::new("where")
        .arg("bash.exe")
        .output()
        .ok()?;

    if !output.status.success() {
        return None;
    }

    for line in String::from_utf8_lossy(&output.stdout).lines() {
        let path = line.trim();
        if path.to_lowercase().contains("git") && PathBuf::from(path).exists() {
            return Some(ShellInfo {
                id: "git-bash".to_string(),
                name: "Git Bash".to_string(),
                command: path.to_string(),
                args: vec!["--login".to_string(), "-i".to_string()],
                icon: "/tool-icons/git-bash.svg".to_string(),
                shell_type: ShellType::Native,
                is_default: false,
            });
        }
    }

    None
}

#[cfg(target_os = "windows")]
fn detect_wsl_distros() -> Vec<ShellInfo> {
    let mut distros = Vec::new();

    let output = match std::process::Command::new("wsl")
        .args(["--list", "--quiet"])
        .output()
    {
        Ok(out) => out,
        Err(_) => return distros,
    };

    if !output.status.success() {
        return distros;
    }

    let stdout = decode_utf16le(&output.stdout);

    for line in stdout.lines() {
        let name = line.trim().trim_matches('\0').trim();
        if name.is_empty() {
            continue;
        }

        distros.push(ShellInfo {
            id: format!("wsl-{}", name),
            name: name.to_string(),
            command: "wsl".to_string(),
            args: vec!["-d".to_string(), name.to_string()],
            icon: get_distro_icon(name).to_string(),
            shell_type: ShellType::Wsl,
            is_default: false,
        });
    }

    distros
}

#[cfg(target_os = "windows")]
fn decode_utf16le(bytes: &[u8]) -> String {
    use std::iter;

    let u16_iter = bytes
        .chunks_exact(2)
        .map(|chunk| u16::from_le_bytes([chunk[0], chunk[1]]));

    String::from_utf16_lossy(&u16_iter.collect::<Vec<u16>>())
        .trim_matches('\u{feff}')
        .to_string()
        .chars()
        .filter(|c| *c != '\0')
        .collect()
}

#[cfg(target_os = "windows")]
fn get_distro_icon(distro: &str) -> &'static str {
    let distro_lower = distro.to_lowercase();

    if distro_lower.contains("ubuntu") {
        "/tool-icons/ubuntu.svg"
    } else if distro_lower.contains("debian") {
        "/tool-icons/debian.svg"
    } else if distro_lower.contains("arch") {
        "/tool-icons/archlinux.svg"
    } else {
        "/tool-icons/linux.svg"
    }
}

