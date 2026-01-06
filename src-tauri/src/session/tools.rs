use super::model::{SessionRecord, SessionTool};
use crate::mcp::get_claude_config_dir;

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
            env: Vec::new(),
        }),
        SessionTool::Claude => build_claude_command(record),
        SessionTool::Gemini => build_gemini_command(record),
        SessionTool::Codex | SessionTool::OpenCode | SessionTool::Custom(_) => Ok(CommandSpec {
            program: record.command.clone(),
            args: Vec::new(),
            env: Vec::new(),
        }),
    }
}

fn build_claude_command(record: &SessionRecord) -> Result<CommandSpec, String> {
    let mut args = vec!["--resume".to_string()];
    if let Some(session_id) = &record.claude_session_id {
        args.push(session_id.clone());
    } else {
        args.clear();
    }
    let mut env = Vec::new();
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
        args.push("--resume".to_string());
        args.push(session_id.clone());
    }
    Ok(CommandSpec {
        program: record.command.clone(),
        args,
        env: Vec::new(),
    })
}
