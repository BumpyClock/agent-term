use std::io;
use std::path::PathBuf;

/// Get the agent-term directory
pub fn get_agent_term_dir() -> io::Result<PathBuf> {
    let home = dirs::home_dir().ok_or_else(|| {
        io::Error::new(io::ErrorKind::NotFound, "Home directory not found")
    })?;
    Ok(home.join(".agent-term"))
}

/// Get the MCP run directory for sockets
pub fn get_agent_term_mcp_run_dir() -> io::Result<PathBuf> {
    Ok(get_agent_term_dir()?.join("run").join("mcp"))
}

/// Compute the socket path for a given MCP name
pub fn socket_path_for(name: &str) -> PathBuf {
    let safe_name = sanitize_socket_name(name);
    if cfg!(windows) {
        return PathBuf::from(format!("\\\\.\\pipe\\agentterm-mcp-{}", safe_name));
    }
    let base = get_agent_term_mcp_run_dir().unwrap_or_else(|_| PathBuf::from("/tmp"));
    base.join(format!("agentterm-mcp-{}.sock", safe_name))
}

fn sanitize_socket_name(name: &str) -> String {
    let mut out = String::with_capacity(name.len());
    for ch in name.chars() {
        if ch.is_ascii_alphanumeric() || ch == '-' || ch == '_' {
            out.push(ch);
        } else {
            out.push('_');
        }
    }
    if out.is_empty() {
        "mcp".to_string()
    } else {
        out
    }
}
