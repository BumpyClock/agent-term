use std::fs::{create_dir_all, OpenOptions};
use std::io::Write;
use std::path::PathBuf;
use std::sync::{
    atomic::{AtomicBool, Ordering},
    OnceLock,
};

use time::format_description::well_known::Rfc3339;
use time::OffsetDateTime;

const DIAG_ENV: &str = "AGENT_TERM_DIAG";

static DIAG_ENABLED: AtomicBool = AtomicBool::new(false);
static DIAG_ENABLED_INIT: OnceLock<()> = OnceLock::new();

/// Explicitly set diagnostics enabled state. Call early in main().
/// If not called, falls back to checking AGENT_TERM_DIAG env var.
pub fn set_enabled(enabled: bool) {
    DIAG_ENABLED.store(enabled, Ordering::Relaxed);
    let _ = DIAG_ENABLED_INIT.set(());
}

fn diagnostics_enabled() -> bool {
    if DIAG_ENABLED_INIT.get().is_some() {
        return DIAG_ENABLED.load(Ordering::Relaxed);
    }

    let env_enabled = std::env::var(DIAG_ENV)
        .map(|v| matches!(v.to_lowercase().as_str(), "1" | "true" | "yes" | "on"))
        .unwrap_or(false);
    if env_enabled {
        DIAG_ENABLED.store(true, Ordering::Relaxed);
    }
    let _ = DIAG_ENABLED_INIT.set(());
    env_enabled
}

fn diagnostics_path() -> Option<PathBuf> {
    static PATH: OnceLock<Option<PathBuf>> = OnceLock::new();
    PATH.get_or_init(|| {
        let home = dirs::home_dir()?;
        Some(
            home.join(".agent-term")
                .join("logs")
                .join("diagnostics.log"),
        )
    })
    .clone()
}

pub fn log_dir() -> Option<PathBuf> {
    static DIR: OnceLock<Option<PathBuf>> = OnceLock::new();
    DIR.get_or_init(|| {
        let home = dirs::home_dir()?;
        Some(home.join(".agent-term").join("logs"))
    })
    .clone()
}

pub fn log(message: impl AsRef<str>) {
    if !diagnostics_enabled() {
        return;
    }

    let timestamp = OffsetDateTime::now_utc()
        .format(&Rfc3339)
        .unwrap_or_else(|_| "unknown-time".to_string());
    let line = format!("[{}] {}\n", timestamp, message.as_ref());

    if let Some(path) = diagnostics_path() {
        if let Some(parent) = path.parent() {
            let _ = create_dir_all(parent);
        }
        if let Ok(mut file) = OpenOptions::new().create(true).append(true).open(&path) {
            let _ = file.write_all(line.as_bytes());
        }
    }

    eprintln!("[diag] {}", message.as_ref());
}
