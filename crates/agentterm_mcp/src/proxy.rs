pub fn proxy_command() -> String {
    let exe_name = if cfg!(windows) {
        "agentterm-mcp-proxy.exe"
    } else {
        "agentterm-mcp-proxy"
    };

    if let Ok(current) = std::env::current_exe() {
        let candidate = current.with_file_name(exe_name);
        if candidate.exists() {
            return candidate.to_string_lossy().to_string();
        }
    }

    exe_name.to_string()
}
