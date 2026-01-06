use std::env;
use std::fs;
use std::path::{Path, PathBuf};

use tauri::{AppHandle, Manager};

use crate::diagnostics;

use super::error::{McpError, McpResult};

const PROXY_COMMAND_ENV: &str = "AGENTTERM_MCP_PROXY_CMD";

pub fn proxy_command() -> String {
    env::var(PROXY_COMMAND_ENV).unwrap_or_else(|_| "agentterm-mcp-proxy".to_string())
}

pub fn proxy_bin_dir() -> McpResult<PathBuf> {
    let home = dirs::home_dir().ok_or_else(|| {
        McpError::ConfigNotFound("Home directory not found".to_string())
    })?;
    Ok(home.join(".local").join("bin"))
}

pub fn proxy_install_path() -> McpResult<PathBuf> {
    let mut path = proxy_bin_dir()?;
    if cfg!(windows) {
        path.push("agentterm-mcp-proxy.exe");
    } else {
        path.push("agentterm-mcp-proxy");
    }
    Ok(path)
}

pub fn ensure_proxy_installed(app: &AppHandle) -> McpResult<()> {
    let install_path = proxy_install_path()?;
    if let Some(parent) = install_path.parent() {
        fs::create_dir_all(parent)
            .map_err(|e| McpError::ConfigWriteError(format!("create_dir_all: {}", e)))?;
    }

    if !install_path.exists() {
        if let Some(source) = resolve_proxy_source(app) {
            if let Err(err) = fs::copy(&source, &install_path) {
                diagnostics::log(format!(
                    "proxy_install_failed source={} dest={} error={}",
                    source.display(),
                    install_path.display(),
                    err
                ));
            } else {
                #[cfg(unix)]
                {
                    use std::os::unix::fs::PermissionsExt;
                    let _ = fs::set_permissions(&install_path, fs::Permissions::from_mode(0o755));
                }
                diagnostics::log(format!(
                    "proxy_installed source={} dest={}",
                    source.display(),
                    install_path.display()
                ));
            }
        } else {
            diagnostics::log("proxy_source_not_found");
        }
    }

    let command = if install_path.exists() {
        "agentterm-mcp-proxy".to_string()
    } else if let Some(fallback) = resolve_proxy_source(app) {
        fallback.display().to_string()
    } else if proxy_in_path() {
        "agentterm-mcp-proxy".to_string()
    } else {
        return Err(McpError::ConfigNotFound(
            "proxy binary not found".to_string(),
        ));
    };

    ensure_path_contains(&proxy_bin_dir()?)?;
    env::set_var(PROXY_COMMAND_ENV, &command);
    diagnostics::log(format!("proxy_command_set command={}", command));
    Ok(())
}

fn resolve_proxy_source(app: &AppHandle) -> Option<PathBuf> {
    let resource_dir = app.path().resource_dir().ok();
    if let Some(dir) = resource_dir.as_ref() {
        if let Some(path) = find_proxy_in_dir(dir) {
            return Some(path);
        }
        if let Some(path) = find_proxy_in_dir(&dir.join("bin")) {
            return Some(path);
        }
    }

    let exe_dir = app.path().executable_dir().ok();
    if let Some(dir) = exe_dir.as_ref() {
        if let Some(path) = find_proxy_in_dir(dir) {
            return Some(path);
        }
        if let Some(path) = find_proxy_in_dir(&dir.join("bin")) {
            return Some(path);
        }
    }

    None
}

fn find_proxy_in_dir(dir: &Path) -> Option<PathBuf> {
    let entries = fs::read_dir(dir).ok()?;
    let exact = proxy_file_name();
    let mut fallback = None;
    for entry in entries.flatten() {
        let path = entry.path();
        if !path.is_file() {
            continue;
        }
        let name = path.file_name()?.to_string_lossy();
        if name == exact {
            return Some(path);
        }
        if name.starts_with("agentterm-mcp-proxy") {
            fallback = Some(path);
        }
    }
    fallback
}

fn proxy_file_name() -> String {
    if cfg!(windows) {
        "agentterm-mcp-proxy.exe".to_string()
    } else {
        "agentterm-mcp-proxy".to_string()
    }
}

fn ensure_path_contains(dir: &Path) -> McpResult<()> {
    let dir_str = dir.to_string_lossy();
    let separator = if cfg!(windows) { ';' } else { ':' };
    let current = env::var("PATH").unwrap_or_default();
    let mut has_entry = false;
    for entry in current.split(separator) {
        if entry == dir_str {
            has_entry = true;
            break;
        }
    }
    if !has_entry {
        let mut updated = String::new();
        updated.push_str(&dir_str);
        if !current.is_empty() {
            updated.push(separator);
            updated.push_str(&current);
        }
        env::set_var("PATH", updated);
        diagnostics::log(format!("proxy_path_added dir={}", dir_str));
    }
    Ok(())
}

fn proxy_in_path() -> bool {
    let separator = if cfg!(windows) { ';' } else { ':' };
    let current = env::var("PATH").unwrap_or_default();
    let file_name = proxy_file_name();
    for entry in current.split(separator) {
        if entry.is_empty() {
            continue;
        }
        let candidate = PathBuf::from(entry).join(&file_name);
        if candidate.exists() {
            return true;
        }
    }
    false
}
