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
        SessionTool::Shell => {
            // Shells already handle their own profile init
            let args = if record.args.is_empty() {
                vec!["-l".to_string(), "-i".to_string()]
            } else {
                record.args.clone()
            };
            Ok(CommandSpec {
                program: record.command.clone(),
                args,
                env: append_proxy_path_env(Vec::new()),
            })
        }
        SessionTool::Claude => {
            let (args, env) = build_claude_args_env(record)?;
            Ok(wrap_in_shell(&record.command, &args, env))
        }
        SessionTool::Gemini => {
            let args = build_gemini_args(record)?;
            Ok(wrap_in_shell(&record.command, &args, Vec::new()))
        }
        SessionTool::Codex | SessionTool::OpenCode | SessionTool::Custom(_) => {
            Ok(wrap_in_shell(&record.command, &record.args, Vec::new()))
        }
    }
}

/// Wrap a command in the system's default shell for profile initialization
fn wrap_in_shell(program: &str, args: &[String], env: Vec<(String, String)>) -> CommandSpec {
    let shell = crate::detect_default_shell();

    // Build the full command string
    let full_cmd = if args.is_empty() {
        program.to_string()
    } else {
        format!("{} {}", program, args.join(" "))
    };

    #[cfg(not(target_os = "windows"))]
    {
        CommandSpec {
            program: shell,
            args: vec!["-l".to_string(), "-i".to_string(), "-c".to_string(), full_cmd],
            env: append_proxy_path_env(env),
        }
    }

    #[cfg(target_os = "windows")]
    {
        let shell_lower = shell.to_lowercase();
        if shell_lower.contains("pwsh") || shell_lower.contains("powershell") {
            CommandSpec {
                program: shell,
                args: vec!["-NoLogo".to_string(), "-Command".to_string(), format!("& {}", full_cmd)],
                env: append_proxy_path_env(env),
            }
        } else {
            // CMD
            CommandSpec {
                program: shell,
                args: vec!["/c".to_string(), full_cmd],
                env: append_proxy_path_env(env),
            }
        }
    }
}

fn build_claude_args_env(record: &SessionRecord) -> Result<(Vec<String>, Vec<(String, String)>), String> {
    let mut args = build_mcp_config_args(record);
    if let Some(session_id) = &record.claude_session_id {
        if !validate_session_id(session_id) {
            return Err(format!("Invalid claude session ID: {}", session_id));
        }
        args.push("--resume".to_string());
        args.push(session_id.clone());
    }
    let mut env = Vec::new();
    if let Ok(config_dir) = get_claude_config_dir() {
        env.push((
            "CLAUDE_CONFIG_DIR".to_string(),
            config_dir.display().to_string(),
        ));
    }
    Ok((args, env))
}

fn build_gemini_args(record: &SessionRecord) -> Result<Vec<String>, String> {
    let mut args = Vec::new();
    if let Some(session_id) = &record.gemini_session_id {
        if !validate_session_id(session_id) {
            return Err(format!("Invalid gemini session ID: {}", session_id));
        }
        args.push("--resume".to_string());
        args.push(session_id.clone());
    }
    Ok(args)
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
