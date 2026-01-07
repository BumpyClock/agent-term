//! Shell detection and enumeration
//!
//! Detects available shells on the system including native shells (PowerShell, cmd, Git Bash)
//! and WSL distributions on Windows. Provides shell metadata for the UI picker.

use crate::mcp::McpManager;
use serde::{Deserialize, Serialize};
use std::path::PathBuf;
use tauri::State;

/// Type of shell - native OS shell or WSL distribution
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum ShellType {
    Native,
    Wsl,
}

/// Information about an available shell
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ShellInfo {
    /// Unique identifier for this shell
    pub id: String,
    /// Display name
    pub name: String,
    /// Command to execute (path or wsl command)
    pub command: String,
    /// Default arguments for this shell
    pub args: Vec<String>,
    /// Icon path
    pub icon: String,
    /// Shell type (native or WSL)
    pub shell_type: ShellType,
    /// Whether this is the system default shell
    pub is_default: bool,
}

/// Detect all available shells on the system
#[tauri::command(rename_all = "camelCase")]
pub async fn available_shells() -> Result<Vec<ShellInfo>, String> {
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

    Ok(shells)
}

/// Get pinned shell IDs from config
#[tauri::command(rename_all = "camelCase")]
pub async fn get_pinned_shells(state: State<'_, McpManager>) -> Result<Vec<String>, String> {
    let config = state.load_config().await.map_err(|e| e.to_string())?;
    Ok(config.shell.pinned_shells)
}

/// Toggle a shell's pinned status
#[tauri::command(rename_all = "camelCase")]
pub async fn toggle_pin_shell(
    state: State<'_, McpManager>,
    shell_id: String,
) -> Result<Vec<String>, String> {
    let mut config = state.load_config().await.map_err(|e| e.to_string())?;

    if let Some(pos) = config.shell.pinned_shells.iter().position(|id| id == &shell_id) {
        config.shell.pinned_shells.remove(pos);
    } else {
        config.shell.pinned_shells.push(shell_id);
    }

    let pinned = config.shell.pinned_shells.clone();
    state
        .write_config(&config)
        .await
        .map_err(|e| e.to_string())?;

    Ok(pinned)
}

#[cfg(target_os = "windows")]
fn detect_windows_shells() -> Vec<ShellInfo> {
    let mut shells = Vec::new();
    let default_shell = crate::detect_default_shell();

    if let Some(shell) = detect_powershell_7() {
        let is_default = shell.command == default_shell;
        shells.push(ShellInfo {
            is_default,
            ..shell
        });
    }

    if let Some(shell) = detect_windows_powershell() {
        let is_default = shell.command == default_shell;
        shells.push(ShellInfo {
            is_default,
            ..shell
        });
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

    // WSL outputs UTF-16LE on Windows, not UTF-8
    let stdout = decode_utf16le(&output.stdout);

    for line in stdout.lines() {
        let name = line.trim().trim_matches('\0').trim();
        if name.is_empty() {
            continue;
        }

        let id = format!("wsl-{}", name.to_lowercase().replace(' ', "-"));
        let icon = get_distro_icon(name);

        distros.push(ShellInfo {
            id,
            name: name.to_string(),
            command: "wsl".to_string(),
            args: vec!["-d".to_string(), name.to_string()],
            icon,
            shell_type: ShellType::Wsl,
            is_default: false,
        });
    }

    distros
}

/// Decode UTF-16LE bytes (Windows WSL output) to a String
#[cfg(target_os = "windows")]
fn decode_utf16le(bytes: &[u8]) -> String {
    // Convert pairs of bytes to u16 values (little-endian)
    let u16_values: Vec<u16> = bytes
        .chunks(2)
        .filter_map(|chunk| {
            if chunk.len() == 2 {
                Some(u16::from_le_bytes([chunk[0], chunk[1]]))
            } else {
                None
            }
        })
        .collect();

    // Decode UTF-16, replacing invalid characters
    String::from_utf16_lossy(&u16_values)
}

#[cfg(target_os = "windows")]
fn get_distro_icon(distro_name: &str) -> String {
    let lower = distro_name.to_lowercase();

    if lower.contains("ubuntu") {
        "/tool-icons/ubuntu.svg".to_string()
    } else if lower.contains("debian") {
        "/tool-icons/debian.svg".to_string()
    } else if lower.contains("arch") {
        "/tool-icons/archlinux.svg".to_string()
    } else if lower.contains("fedora") {
        "/tool-icons/fedora.svg".to_string()
    } else if lower.contains("opensuse") || lower.contains("suse") {
        "/tool-icons/opensuse.svg".to_string()
    } else if lower.contains("alpine") {
        "/tool-icons/alpine.svg".to_string()
    } else if lower.contains("kali") {
        "/tool-icons/kali.svg".to_string()
    } else {
        "/tool-icons/linux.svg".to_string()
    }
}

#[cfg(not(target_os = "windows"))]
fn detect_unix_shells() -> Vec<ShellInfo> {
    use users::os::unix::UserExt;

    let mut shells = Vec::new();
    let default_shell = crate::detect_default_shell();

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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn shell_type_serializes_correctly() {
        let native = ShellType::Native;
        let wsl = ShellType::Wsl;

        let native_json = serde_json::to_string(&native).unwrap();
        let wsl_json = serde_json::to_string(&wsl).unwrap();

        assert_eq!(native_json, "\"native\"");
        assert_eq!(wsl_json, "\"wsl\"");
    }

    #[test]
    fn shell_info_serializes_with_camel_case() {
        let shell = ShellInfo {
            id: "test".to_string(),
            name: "Test Shell".to_string(),
            command: "/bin/test".to_string(),
            args: vec![],
            icon: "/icon.svg".to_string(),
            shell_type: ShellType::Native,
            is_default: true,
        };

        let json = serde_json::to_string(&shell).unwrap();
        assert!(json.contains("\"shellType\""));
        assert!(json.contains("\"isDefault\""));
    }

    #[cfg(target_os = "windows")]
    #[test]
    fn get_distro_icon_returns_correct_icons() {
        assert_eq!(get_distro_icon("Ubuntu-22.04"), "/tool-icons/ubuntu.svg");
        assert_eq!(get_distro_icon("Debian"), "/tool-icons/debian.svg");
        assert_eq!(get_distro_icon("Arch"), "/tool-icons/archlinux.svg");
        assert_eq!(get_distro_icon("CustomDistro"), "/tool-icons/linux.svg");
    }
}
