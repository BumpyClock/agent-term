use std::path::PathBuf;

pub fn set_enabled(enabled: bool) {
    agentterm_shared::diagnostics::set_enabled(enabled);
}

pub fn log_dir() -> Option<PathBuf> {
    agentterm_shared::diagnostics::log_dir()
}

pub fn log(message: impl AsRef<str>) {
    agentterm_shared::diagnostics::log(message);
}
