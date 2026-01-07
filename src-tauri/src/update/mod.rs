// ABOUTME: Handles application auto-updates using tauri-plugin-updater.
// ABOUTME: Provides commands for checking, downloading, and installing updates.

use serde::{Deserialize, Serialize};
use std::sync::Arc;
use parking_lot::Mutex;
use tauri::{AppHandle, Emitter, Manager};
use tauri_plugin_updater::{Update, UpdaterExt};
use crate::diagnostics;
use crate::mcp::McpManager;

/// Update status for frontend
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub enum UpdateStatus {
    Idle,
    Checking,
    Available,
    Downloading,
    Ready,
    Error,
}

/// Information about an available update
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateInfo {
    pub version: String,
    pub body: Option<String>,
    pub date: Option<String>,
}

/// Current state of the updater
pub struct UpdateState {
    pub status: UpdateStatus,
    pub update_info: Option<UpdateInfo>,
    pub download_progress: f64,
    pub error: Option<String>,
    pub pending_update: Option<Update>,
}

impl Default for UpdateState {
    fn default() -> Self {
        Self {
            status: UpdateStatus::Idle,
            update_info: None,
            download_progress: 0.0,
            error: None,
            pending_update: None,
        }
    }
}

/// Manager for update operations
#[derive(Clone)]
pub struct UpdateManager {
    state: Arc<Mutex<UpdateState>>,
}

impl UpdateManager {
    pub fn new() -> Self {
        Self {
            state: Arc::new(Mutex::new(UpdateState::default())),
        }
    }

    pub fn get_status(&self) -> UpdateStatus {
        self.state.lock().status.clone()
    }

    pub fn get_info(&self) -> Option<UpdateInfo> {
        self.state.lock().update_info.clone()
    }

    pub fn get_progress(&self) -> f64 {
        self.state.lock().download_progress
    }

    pub fn get_error(&self) -> Option<String> {
        self.state.lock().error.clone()
    }

    pub fn set_status(&self, status: UpdateStatus) {
        self.state.lock().status = status;
    }

    pub fn set_error(&self, error: Option<String>) {
        let mut state = self.state.lock();
        state.error = error;
        state.status = UpdateStatus::Error;
    }

    pub fn set_update_info(&self, info: Option<UpdateInfo>) {
        self.state.lock().update_info = info;
    }

    pub fn set_progress(&self, progress: f64) {
        self.state.lock().download_progress = progress;
    }

    pub fn set_pending_update(&self, update: Option<Update>) {
        self.state.lock().pending_update = update;
    }

    pub fn take_pending_update(&self) -> Option<Update> {
        self.state.lock().pending_update.take()
    }

    pub fn has_pending_update(&self) -> bool {
        self.state.lock().pending_update.is_some()
    }
}

/// Response for update_get_status command
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateStatusResponse {
    pub status: UpdateStatus,
    pub update_info: Option<UpdateInfo>,
    pub download_progress: f64,
    pub error: Option<String>,
}

/// Check for available updates
#[tauri::command(rename_all = "camelCase")]
pub async fn update_check(
    app: AppHandle,
    update_manager: tauri::State<'_, UpdateManager>,
) -> Result<Option<UpdateInfo>, String> {
    update_manager.set_status(UpdateStatus::Checking);
    update_manager.set_error(None);

    diagnostics::log("update_check started".to_string());

    match app.updater() {
        Ok(updater) => {
            match updater.check().await {
                Ok(Some(update)) => {
                    let info = UpdateInfo {
                        version: update.version.clone(),
                        body: update.body.clone(),
                        date: update.date.map(|d| d.to_string()),
                    };

                    diagnostics::log(format!("update_check found version={}", info.version));

                    update_manager.set_update_info(Some(info.clone()));
                    update_manager.set_pending_update(Some(update));
                    update_manager.set_status(UpdateStatus::Available);

                    // Update last check time
                    if let Some(mcp_manager) = app.try_state::<McpManager>() {
                        if let Ok(mut config) = mcp_manager.load_config().await {
                            config.updates.last_check_time = Some(chrono_now_iso());
                            let _ = mcp_manager.write_config(&config).await;
                        }
                    }

                    Ok(Some(info))
                }
                Ok(None) => {
                    diagnostics::log("update_check no_update_available".to_string());
                    update_manager.set_status(UpdateStatus::Idle);
                    update_manager.set_update_info(None);

                    // Update last check time even when no update
                    if let Some(mcp_manager) = app.try_state::<McpManager>() {
                        if let Ok(mut config) = mcp_manager.load_config().await {
                            config.updates.last_check_time = Some(chrono_now_iso());
                            let _ = mcp_manager.write_config(&config).await;
                        }
                    }

                    Ok(None)
                }
                Err(e) => {
                    let err_msg = format!("Failed to check for updates: {}", e);
                    diagnostics::log(format!("update_check error={}", err_msg));
                    update_manager.set_error(Some(err_msg.clone()));
                    Err(err_msg)
                }
            }
        }
        Err(e) => {
            let err_msg = format!("Updater not available: {}", e);
            diagnostics::log(format!("update_check error={}", err_msg));
            update_manager.set_error(Some(err_msg.clone()));
            Err(err_msg)
        }
    }
}

/// Download the pending update
#[tauri::command(rename_all = "camelCase")]
pub async fn update_download(
    app: AppHandle,
    update_manager: tauri::State<'_, UpdateManager>,
) -> Result<(), String> {
    let update = update_manager.take_pending_update()
        .ok_or_else(|| "No pending update to download".to_string())?;

    update_manager.set_status(UpdateStatus::Downloading);
    update_manager.set_progress(0.0);

    diagnostics::log(format!("update_download started version={}", update.version));

    let manager = update_manager.inner().clone();
    let app_handle = app.clone();

    match update.download(
        move |chunk_length, content_length| {
            if let Some(total) = content_length {
                let progress = (chunk_length as f64 / total as f64) * 100.0;
                manager.set_progress(progress);
                // Emit progress event to frontend
                let _ = app_handle.emit("update-download-progress", progress);
            }
        },
        || {
            // Called when download finishes
        },
    ).await {
        Ok(bytes) => {
            diagnostics::log(format!("update_download completed bytes={}", bytes.len()));
            update_manager.set_status(UpdateStatus::Ready);
            update_manager.set_progress(100.0);

            // The update is ready to install
            Ok(())
        }
        Err(e) => {
            let err_msg = format!("Failed to download update: {}", e);
            diagnostics::log(format!("update_download error={}", err_msg));
            update_manager.set_error(Some(err_msg.clone()));
            Err(err_msg)
        }
    }
}

/// Install the downloaded update and restart
#[tauri::command(rename_all = "camelCase")]
pub async fn update_install(
    app: AppHandle,
    update_manager: tauri::State<'_, UpdateManager>,
) -> Result<(), String> {
    diagnostics::log("update_install starting".to_string());

    // The update should be ready after download
    if update_manager.get_status() != UpdateStatus::Ready {
        return Err("No update ready to install".to_string());
    }

    // tauri-plugin-updater handles the installation internally
    // We need to trigger app restart for the update to apply
    diagnostics::log("update_install triggering restart".to_string());

    // Request app restart - the update will be applied on restart
    // Note: app.restart() does not return
    app.restart();
}

/// Get current update status
#[tauri::command(rename_all = "camelCase")]
pub fn update_get_status(
    update_manager: tauri::State<'_, UpdateManager>,
) -> UpdateStatusResponse {
    let state = update_manager.state.lock();
    UpdateStatusResponse {
        status: state.status.clone(),
        update_info: state.update_info.clone(),
        download_progress: state.download_progress,
        error: state.error.clone(),
    }
}

/// Get update settings from config
#[tauri::command(rename_all = "camelCase")]
pub async fn update_get_settings(
    mcp_manager: tauri::State<'_, McpManager>,
) -> Result<crate::mcp::config::UpdateSettings, String> {
    let config = mcp_manager.load_config().await.map_err(|e| e.to_string())?;
    Ok(config.updates)
}

/// Save update settings to config
#[tauri::command(rename_all = "camelCase")]
pub async fn update_set_settings(
    settings: crate::mcp::config::UpdateSettings,
    mcp_manager: tauri::State<'_, McpManager>,
) -> Result<(), String> {
    let mut config = mcp_manager.load_config().await.map_err(|e| e.to_string())?;
    config.updates = settings;
    mcp_manager.write_config(&config).await.map_err(|e| e.to_string())?;
    Ok(())
}

/// Helper to get current ISO 8601 timestamp
fn chrono_now_iso() -> String {
    use time::OffsetDateTime;
    use time::format_description::well_known::Iso8601;
    OffsetDateTime::now_utc()
        .format(&Iso8601::DEFAULT)
        .unwrap_or_else(|_| String::new())
}

/// Build the update manager
pub fn build_update_manager() -> UpdateManager {
    UpdateManager::new()
}
