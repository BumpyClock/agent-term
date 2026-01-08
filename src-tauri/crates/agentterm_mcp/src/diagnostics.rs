use std::path::PathBuf;

pub fn log_dir() -> Option<PathBuf> {
    agentterm_shared::diagnostics::log_dir()
}

pub fn log(message: impl AsRef<str>) {
    agentterm_shared::diagnostics::log(message);
}
