use super::model::{SessionRecord, SessionTool};
use crate::diagnostics;
use crate::mcp::config::{
    get_managed_global_mcp_path,
    get_user_project_mcp_path,
};
use crate::mcp::get_claude_config_dir;
use crate::mcp::proxy::proxy_bin_dir;

/// Validate session ID contains only safe characters
fn validate_session_id(id: &str) -> bool {
    !id.is_empty() && id.chars().all(|c| c.is_ascii_alphanumeric() || c == '-' || c == '_' || c == '.')
}

/// Command specification for launching a session tool.
///
/// Example:
/// ```rust,ignore
/// let spec = CommandSpec {
///     program: "bash".to_string(),
///     args: vec!["-i".to_string()],
///     env: vec![],
/// };
/// ```
#[derive(Debug, Clone)]
pub struct CommandSpec {
    pub program: String,
    pub args: Vec<String>,
    pub env: Vec<(String, String)>,
}

pub fn build_command(record: &SessionRecord) -> Result<CommandSpec, String> {
    match &record.tool {
        SessionTool::Shell => Ok(CommandSpec {
            program: record.command.clone(),
            args: vec!["-l".to_string(), "-i".to_string()],
            env: append_proxy_path_env(Vec::new()),
        }),
        SessionTool::Claude => build_claude_command(record),
        SessionTool::Gemini => build_gemini_command(record),
        SessionTool::Codex | SessionTool::OpenCode | SessionTool::Custom(_) => Ok(CommandSpec {
            program: record.command.clone(),
            args: Vec::new(),
            env: append_proxy_path_env(Vec::new()),
        }),
    }
}

fn build_claude_command(record: &SessionRecord) -> Result<CommandSpec, String> {
    let mut args = build_mcp_config_args(record);
    if let Some(session_id) = &record.claude_session_id {
        if !validate_session_id(session_id) {
            return Err(format!("Invalid claude session ID: {}", session_id));
        }
        args.push("--resume".to_string());
        args.push(session_id.clone());
    }
    let mut env = append_proxy_path_env(Vec::new());
    if let Ok(config_dir) = get_claude_config_dir() {
        env.push((
            "CLAUDE_CONFIG_DIR".to_string(),
            config_dir.display().to_string(),
        ));
    }
    Ok(CommandSpec {
        program: record.command.clone(),
        args,
        env,
    })
}

fn build_gemini_command(record: &SessionRecord) -> Result<CommandSpec, String> {
    let mut args = Vec::new();
    if let Some(session_id) = &record.gemini_session_id {
        if !validate_session_id(session_id) {
            return Err(format!("Invalid gemini session ID: {}", session_id));
        }
        args.push("--resume".to_string());
        args.push(session_id.clone());
    }
    Ok(CommandSpec {
        program: record.command.clone(),
        args,
        env: append_proxy_path_env(Vec::new()),
    })
}

fn build_mcp_config_args(record: &SessionRecord) -> Vec<String> {
    let mut paths = Vec::new();

    if let Ok(global_path) = get_managed_global_mcp_path() {
        if global_path.exists() {
            paths.push(global_path.display().to_string());
        }
    }

    if !record.project_path.is_empty() {
        let user_path = get_user_project_mcp_path(&record.project_path);
        if user_path.exists() {
            paths.push(user_path.display().to_string());
        }
    }

    if paths.is_empty() {
        diagnostics::log("mcp_config_paths none");
        return Vec::new();
    }

    let mut args = Vec::with_capacity(paths.len() + 1);
    args.push("--mcp-config".to_string());
    args.extend(paths);
    diagnostics::log(format!("mcp_config_paths count={} paths={}", args.len() - 1, args[1..].join(";")));
    args
}

fn append_proxy_path_env(mut env: Vec<(String, String)>) -> Vec<(String, String)> {
    let bin_dir = match proxy_bin_dir() {
        Ok(dir) => dir,
        Err(_) => return env,
    };

    let separator = if cfg!(windows) { ';' } else { ':' };
    let bin_dir_str = bin_dir.to_string_lossy().to_string();
    let existing = std::env::var("PATH").unwrap_or_default();
    let mut has_entry = false;
    for entry in existing.split(separator) {
        if entry == bin_dir_str {
            has_entry = true;
            break;
        }
    }

    let updated = if has_entry {
        existing
    } else if existing.is_empty() {
        bin_dir_str
    } else {
        format!("{}{}{}", bin_dir_str, separator, existing)
    };

    for (key, value) in env.iter_mut() {
        if key == "PATH" {
            *value = updated;
            return env;
        }
    }

    env.push(("PATH".to_string(), updated));
    env
}
