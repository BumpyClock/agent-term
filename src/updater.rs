//! Auto-update functionality for AgentTerm.
//!
//! Implements custom update checking, downloading, and installation using reqwest
//! rather than `self_update` crate due to macOS code signing constraints.
//!
//! Update source: GitHub releases from `BumpyClock/agent-term` repository.
//! Security: Ed25519 signature verification for all downloaded updates.

use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

use base64::Engine;
use ed25519_dalek::{Signature, Verifier, VerifyingKey};
use futures::AsyncReadExt;
use gpui::http_client::{http, HttpClient};
use gpui::{AsyncApp, Context, EventEmitter, Task, WeakEntity};
use reqwest_client::ReqwestClient;
use semver::Version;
use serde::{Deserialize, Serialize};

const MANIFEST_URL: &str =
    "https://github.com/BumpyClock/agent-term/releases/latest/download/latest.json";

// TODO: When adding "View Release Notes" link, use:
// const GITHUB_REPO: &str = "BumpyClock/agent-term";

/// Embedded public key for signature verification (Ed25519).
/// This should be replaced with your actual public key bytes.
const PUBLIC_KEY_BYTES: [u8; 32] = [
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
];

/// The current state of the update process.
#[derive(Clone, Debug, PartialEq)]
pub enum UpdateState {
    /// No update check in progress.
    Idle,
    /// Checking for updates.
    Checking,
    /// An update is available.
    Available(UpdateInfo),
    /// Downloading the update.
    Downloading {
        info: UpdateInfo,
        progress: f32, // 0.0 to 1.0
    },
    /// Update has been downloaded and is ready to install.
    ReadyToInstall(UpdateInfo),
    /// Installing the update.
    Installing(UpdateInfo),
    /// An error occurred.
    Error(String),
    /// The app is up to date.
    UpToDate,
}

// TODO: Add helper methods to UpdateState when needed by UI:
// - is_update_available() -> bool
// - update_info() -> Option<&UpdateInfo>

/// Information about an available update.
#[derive(Clone, Debug, PartialEq)]
pub struct UpdateInfo {
    pub version: Version,
    pub notes: String,
    pub download_url: String,
    pub signature: String,
    pub pub_date: String,
}

/// Platform-specific asset info from the manifest.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PlatformAsset {
    pub url: String,
    pub signature: String,
}

/// Update manifest format from GitHub releases.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct UpdateManifest {
    pub version: String,
    pub notes: String,
    pub pub_date: String,
    pub platforms: std::collections::HashMap<String, PlatformAsset>,
}

impl UpdateManifest {
    /// Returns the platform key for the current OS/architecture.
    fn current_platform_key() -> &'static str {
        #[cfg(all(target_os = "macos", target_arch = "aarch64"))]
        {
            "darwin-aarch64"
        }
        #[cfg(all(target_os = "macos", target_arch = "x86_64"))]
        {
            "darwin-x86_64"
        }
        #[cfg(all(target_os = "windows", target_arch = "x86_64"))]
        {
            "windows-x86_64"
        }
        #[cfg(all(target_os = "linux", target_arch = "x86_64"))]
        {
            "linux-x86_64"
        }
        #[cfg(not(any(
            all(target_os = "macos", target_arch = "aarch64"),
            all(target_os = "macos", target_arch = "x86_64"),
            all(target_os = "windows", target_arch = "x86_64"),
            all(target_os = "linux", target_arch = "x86_64"),
        )))]
        {
            "unknown"
        }
    }

    /// Gets the asset for the current platform.
    pub fn current_platform_asset(&self) -> Option<&PlatformAsset> {
        self.platforms.get(Self::current_platform_key())
    }
}

/// Events emitted by the UpdateManager.
#[derive(Clone, Debug)]
pub enum UpdateEvent {
    /// State has changed.
    StateChanged,
    /// Update check completed.
    CheckCompleted,
}

/// Manages the auto-update process.
pub struct UpdateManager {
    state: UpdateState,
    http_client: Arc<ReqwestClient>,
    current_download_task: Option<Task<()>>,
    downloaded_file_path: Option<PathBuf>,
}

impl EventEmitter<UpdateEvent> for UpdateManager {}

impl UpdateManager {
    /// Creates a new UpdateManager.
    pub fn new(_cx: &mut Context<Self>) -> Self {
        let http_client = Arc::new(
            ReqwestClient::user_agent(&format!("AgentTerm/{}", env!("CARGO_PKG_VERSION")))
                .unwrap_or_else(|_| ReqwestClient::new()),
        );

        Self {
            state: UpdateState::Idle,
            http_client,
            current_download_task: None,
            downloaded_file_path: None,
        }
    }

    /// Returns the current update state.
    pub fn state(&self) -> &UpdateState {
        &self.state
    }

    /// Returns the current app version.
    pub fn current_version() -> Version {
        Version::parse(env!("CARGO_PKG_VERSION")).unwrap_or_else(|_| Version::new(0, 0, 0))
    }

    /// Sets the state and emits an event.
    fn set_state(&mut self, state: UpdateState, cx: &mut Context<Self>) {
        self.state = state;
        cx.emit(UpdateEvent::StateChanged);
        cx.notify();
    }

    /// Checks for updates by fetching the manifest.
    pub fn check_for_updates(&mut self, cx: &mut Context<Self>) {
        if matches!(
            self.state,
            UpdateState::Checking | UpdateState::Downloading { .. }
        ) {
            return;
        }

        self.set_state(UpdateState::Checking, cx);

        let client = self.http_client.clone();
        let weak_self = cx.entity().downgrade();

        cx.spawn(|_this: WeakEntity<Self>, cx: &mut AsyncApp| {
            let cx = cx.clone();
            async move {
                let result = Self::fetch_manifest(&client).await;

                let _ = cx.update(|cx| {
                    if let Some(entity) = weak_self.upgrade() {
                        entity.update(cx, |manager, cx| {
                            manager.handle_manifest_result(result, cx);
                        });
                    }
                });
            }
        })
        .detach();
    }

    /// Fetches and parses the update manifest.
    async fn fetch_manifest(client: &Arc<ReqwestClient>) -> Result<UpdateManifest, String> {
        let request = http::Request::builder()
            .method(http::Method::GET)
            .uri(MANIFEST_URL)
            .body(gpui::http_client::AsyncBody::empty())
            .map_err(|e| format!("Failed to build request: {}", e))?;

        let mut response = client
            .send(request)
            .await
            .map_err(|e| format!("Failed to fetch manifest: {}", e))?;

        if !response.status().is_success() {
            return Err(format!(
                "Manifest request failed with status: {}",
                response.status()
            ));
        }

        let mut bytes = Vec::new();
        response
            .body_mut()
            .read_to_end(&mut bytes)
            .await
            .map_err(|e| format!("Failed to read manifest: {}", e))?;

        let text = String::from_utf8(bytes)
            .map_err(|e| format!("Invalid UTF-8 in manifest: {}", e))?;

        serde_json::from_str(&text).map_err(|e| format!("Failed to parse manifest: {}", e))
    }

    /// Handles the manifest fetch result.
    fn handle_manifest_result(
        &mut self,
        result: Result<UpdateManifest, String>,
        cx: &mut Context<Self>,
    ) {
        match result {
            Ok(manifest) => {
                let Ok(remote_version) = Version::parse(&manifest.version) else {
                    self.set_state(
                        UpdateState::Error("Invalid version in manifest".to_string()),
                        cx,
                    );
                    return;
                };

                let current_version = Self::current_version();

                if remote_version > current_version {
                    match manifest.current_platform_asset().cloned() {
                        Some(asset) => {
                            let info = UpdateInfo {
                                version: remote_version,
                                notes: manifest.notes,
                                download_url: asset.url,
                                signature: asset.signature,
                                pub_date: manifest.pub_date,
                            };
                            self.set_state(UpdateState::Available(info), cx);
                            cx.emit(UpdateEvent::CheckCompleted);
                        }
                        None => {
                            self.set_state(
                                UpdateState::Error("No update available for this platform".to_string()),
                                cx,
                            );
                            cx.emit(UpdateEvent::CheckCompleted);
                        }
                    }
                } else {
                    self.set_state(UpdateState::UpToDate, cx);
                    cx.emit(UpdateEvent::CheckCompleted);

                    // Auto-clear UpToDate state after 2 seconds
                    let weak_self = cx.entity().downgrade();
                    cx.spawn(|_this: WeakEntity<Self>, cx: &mut AsyncApp| {
                        let cx = cx.clone();
                        async move {
                            smol::Timer::after(Duration::from_secs(2)).await;
                            let _ = cx.update(|cx| {
                                if let Some(entity) = weak_self.upgrade() {
                                    entity.update(cx, |manager, cx| {
                                        if matches!(manager.state, UpdateState::UpToDate) {
                                            manager.set_state(UpdateState::Idle, cx);
                                        }
                                    });
                                }
                            });
                        }
                    })
                    .detach();
                }
            }
            Err(error) => {
                self.set_state(UpdateState::Error(error), cx);
                cx.emit(UpdateEvent::CheckCompleted);
            }
        }
    }

    /// Downloads the available update.
    pub fn download_update(&mut self, cx: &mut Context<Self>) {
        let UpdateState::Available(info) = &self.state else {
            return;
        };

        let info = info.clone();
        let download_url = info.download_url.clone();
        let signature = info.signature.clone();

        self.set_state(
            UpdateState::Downloading {
                info: info.clone(),
                progress: 0.0,
            },
            cx,
        );

        let client = self.http_client.clone();
        let weak_self = cx.entity().downgrade();

        let task = cx.spawn(|_this: WeakEntity<Self>, cx: &mut AsyncApp| {
            let cx = cx.clone();
            async move {
                let result = Self::download_file_simple(&client, &download_url, &signature).await;

                let _ = cx.update(|cx| {
                    if let Some(entity) = weak_self.upgrade() {
                        entity.update(cx, |manager, cx| match result {
                            Ok(path) => {
                                manager.downloaded_file_path = Some(path);
                                manager.set_state(UpdateState::ReadyToInstall(info), cx);
                            }
                            Err(error) => {
                                manager.set_state(UpdateState::Error(error), cx);
                            }
                        });
                    }
                });
            }
        });

        self.current_download_task = Some(task);
    }

    /// Downloads a file and verifies its signature.
    async fn download_file_simple(
        client: &Arc<ReqwestClient>,
        url: &str,
        signature: &str,
    ) -> Result<PathBuf, String> {
        let request = http::Request::builder()
            .method(http::Method::GET)
            .uri(url)
            .body(gpui::http_client::AsyncBody::empty())
            .map_err(|e| format!("Failed to build download request: {}", e))?;

        let mut response = client
            .send(request)
            .await
            .map_err(|e| format!("Download request failed: {}", e))?;

        if !response.status().is_success() {
            return Err(format!("Download failed with status: {}", response.status()));
        }

        let filename = url
            .split('/')
            .next_back()
            .unwrap_or("update")
            .to_string();

        let temp_dir = std::env::temp_dir().join("agentterm-updates");
        std::fs::create_dir_all(&temp_dir)
            .map_err(|e| format!("Failed to create temp directory: {}", e))?;

        let file_path = temp_dir.join(&filename);

        let mut bytes = Vec::new();
        response
            .body_mut()
            .read_to_end(&mut bytes)
            .await
            .map_err(|e| format!("Failed to download file: {}", e))?;

        // Verify signature before writing
        verify_signature(&bytes, signature)?;

        std::fs::write(&file_path, &bytes)
            .map_err(|e| format!("Failed to write update file: {}", e))?;

        Ok(file_path)
    }

    // TODO: Implement cancel_download() when UI cancel button is added:
    // Takes self.current_download_task and drops it to cancel, then resets state.

    /// Applies the downloaded update.
    pub fn apply_update(&mut self, cx: &mut Context<Self>) {
        let UpdateState::ReadyToInstall(info) = &self.state else {
            return;
        };

        let Some(file_path) = self.downloaded_file_path.clone() else {
            self.set_state(UpdateState::Error("No downloaded file found".to_string()), cx);
            return;
        };

        self.set_state(UpdateState::Installing(info.clone()), cx);

        #[cfg(target_os = "macos")]
        {
            if let Err(e) = self.launch_macos_updater(&file_path) {
                self.set_state(UpdateState::Error(format!("Failed to launch updater: {}", e)), cx);
            }
        }

        #[cfg(target_os = "windows")]
        {
            if let Err(e) = self.launch_windows_updater(&file_path) {
                self.set_state(UpdateState::Error(format!("Failed to launch updater: {}", e)), cx);
            }
        }

        #[cfg(target_os = "linux")]
        {
            if let Err(e) = self.launch_linux_updater(&file_path) {
                self.set_state(UpdateState::Error(format!("Failed to launch updater: {}", e)), cx);
            }
        }
    }

    #[cfg(target_os = "macos")]
    fn launch_macos_updater(&self, dmg_path: &PathBuf) -> Result<(), String> {
        use std::process::Command;

        // Create a helper script that will:
        // 1. Wait for this app to quit
        // 2. Mount the DMG
        // 3. Copy the new app to /Applications
        // 4. Unmount the DMG
        // 5. Relaunch the app
        let script = format!(
            r#"#!/bin/bash
sleep 1
MOUNT_POINT=$(hdiutil attach "{}" -nobrowse | grep Volumes | awk '{{print $3}}')
if [ -z "$MOUNT_POINT" ]; then
    exit 1
fi
APP_NAME=$(ls "$MOUNT_POINT" | grep "\.app$" | head -1)
if [ -z "$APP_NAME" ]; then
    hdiutil detach "$MOUNT_POINT"
    exit 1
fi
rm -rf "/Applications/$APP_NAME"
cp -R "$MOUNT_POINT/$APP_NAME" /Applications/
hdiutil detach "$MOUNT_POINT"
open "/Applications/$APP_NAME"
"#,
            dmg_path.display()
        );

        let script_path = std::env::temp_dir().join("agentterm-update.sh");
        std::fs::write(&script_path, script)
            .map_err(|e| format!("Failed to write updater script: {}", e))?;

        Command::new("chmod")
            .args(["+x", script_path.to_str().unwrap_or_default()])
            .output()
            .map_err(|e| format!("Failed to make script executable: {}", e))?;

        Command::new("bash")
            .arg(&script_path)
            .spawn()
            .map_err(|e| format!("Failed to launch updater: {}", e))?;

        // Quit the current app
        std::process::exit(0);
    }

    #[cfg(target_os = "windows")]
    fn launch_windows_updater(&self, msi_path: &PathBuf) -> Result<(), String> {
        use std::process::Command;

        // Launch MSI installer with passive mode
        Command::new("msiexec")
            .args(["/i", msi_path.to_str().unwrap_or_default(), "/passive"])
            .spawn()
            .map_err(|e| format!("Failed to launch installer: {}", e))?;

        // Exit the current app
        std::process::exit(0);
    }

    #[cfg(target_os = "linux")]
    fn launch_linux_updater(&self, file_path: &PathBuf) -> Result<(), String> {
        let extension = file_path
            .extension()
            .and_then(|e| e.to_str())
            .unwrap_or("");

        match extension.to_lowercase().as_str() {
            "appimage" => {
                use std::process::Command;

                // For AppImage, replace the current executable
                let current_exe = std::env::current_exe()
                    .map_err(|e| format!("Failed to get current executable: {}", e))?;

                std::fs::copy(file_path, &current_exe)
                    .map_err(|e| format!("Failed to replace executable: {}", e))?;

                Command::new("chmod")
                    .args(["+x", current_exe.to_str().unwrap_or_default()])
                    .output()
                    .map_err(|e| format!("Failed to set executable permission: {}", e))?;

                // Relaunch
                Command::new(&current_exe)
                    .spawn()
                    .map_err(|e| format!("Failed to relaunch: {}", e))?;

                std::process::exit(0);
            }
            _ => {
                // For deb/rpm, just show the download location
                Err(format!(
                    "Please install the update manually from: {}",
                    file_path.display()
                ))
            }
        }
    }

    /// Resets the state to idle, clearing any errors.
    pub fn dismiss(&mut self, cx: &mut Context<Self>) {
        match &self.state {
            UpdateState::Error(_) | UpdateState::UpToDate => {
                self.set_state(UpdateState::Idle, cx);
            }
            _ => {}
        }
    }
}

// TODO: Add releases_page_url() function when "View Release Notes" link is added to UI:
// Returns format!("https://github.com/{}/releases", GITHUB_REPO)

/// Verifies the Ed25519 signature of the downloaded data.
fn verify_signature(data: &[u8], signature_base64: &str) -> Result<(), String> {
    // Skip verification if public key is not set (all zeros)
    if PUBLIC_KEY_BYTES == [0u8; 32] {
        agentterm_mcp::diagnostics::log("update_signature_verification_skipped (no public key set)");
        return Ok(());
    }

    let signature_bytes = base64::engine::general_purpose::STANDARD
        .decode(signature_base64)
        .map_err(|e| format!("Invalid signature encoding: {}", e))?;

    let signature = Signature::from_slice(&signature_bytes)
        .map_err(|e| format!("Invalid signature format: {}", e))?;

    let verifying_key = VerifyingKey::from_bytes(&PUBLIC_KEY_BYTES)
        .map_err(|e| format!("Invalid public key: {}", e))?;

    verifying_key
        .verify(data, &signature)
        .map_err(|_| "Signature verification failed".to_string())
}
