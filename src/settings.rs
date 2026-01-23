use serde::{Deserialize, Serialize};
use std::path::PathBuf;

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[derive(Default)]
pub enum Theme {
    Light,
    Dark,
    #[default]
    System,
}


#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct CustomTool {
    pub id: String,
    pub name: String,
    pub command: String,
    pub args: Vec<String>,
    pub icon: Option<String>,
    pub description: Option<String>,
    pub is_shell: bool,
    pub enabled: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct AppSettings {
    // General - Updates
    pub check_for_updates: bool,
    pub auto_update: bool,
    #[serde(default)]
    pub last_update_check: Option<u64>,
    #[serde(default = "default_update_check_interval_hours")]
    pub update_check_interval_hours: u32,

    // Appearance
    pub theme: Theme,
    pub accent_color: String,
    pub terminal_color_scheme: String,
    pub font_family: String,
    pub font_size: f32,
    pub line_height: f32,
    pub letter_spacing: f32,
    /// Window transparency (0.0 = solid, 1.0 = fully transparent)
    #[serde(default, alias = "window_opacity")]
    pub window_transparency: f32,
    /// Enable macOS vibrancy blur effect
    #[serde(default = "default_blur_enabled")]
    pub blur_enabled: bool,
    /// Warm search index on startup (background thread)
    #[serde(default = "default_warm_search_index")]
    pub warm_search_index: bool,

    // Tools
    pub default_shell_id: Option<String>,
    pub custom_tools: Vec<CustomTool>,

    // Diagnostics
    #[serde(default)]
    pub write_diagnostics_logs: bool,
}

fn default_blur_enabled() -> bool {
    true
}

fn default_update_check_interval_hours() -> u32 {
    24
}

fn default_warm_search_index() -> bool {
    true
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            // General - Updates
            check_for_updates: true,
            auto_update: false,
            last_update_check: None,
            update_check_interval_hours: 24,

            // Appearance
            theme: Theme::System,
            accent_color: "periwinkle".to_string(),
            terminal_color_scheme: "one".to_string(),
            font_family: "JetBrainsMono Nerd Font".to_string(),
            font_size: 14.0,
            line_height: 1.4,
            letter_spacing: 0.0,
            window_transparency: 0.0, // 0.0 = solid (no transparency)
            blur_enabled: true,
            warm_search_index: true,

            // Tools
            default_shell_id: None,
            custom_tools: Vec::new(),

            // Diagnostics
            write_diagnostics_logs: false,
        }
    }
}

impl AppSettings {
    /// Get the settings file path
    fn settings_path() -> Option<PathBuf> {
        dirs::config_dir().map(|p| p.join("agentterm").join("settings.toml"))
    }

    /// Updates the last update check timestamp to now.
    pub fn update_last_check_time(&mut self) {
        use std::time::{SystemTime, UNIX_EPOCH};
        if let Ok(duration) = SystemTime::now().duration_since(UNIX_EPOCH) {
            self.last_update_check = Some(duration.as_secs());
        }
    }

    /// Returns true if enough time has passed since the last update check.
    pub fn should_check_for_updates(&self) -> bool {
        if !self.check_for_updates {
            return false;
        }

        let Some(last_check) = self.last_update_check else {
            return true;
        };

        use std::time::{SystemTime, UNIX_EPOCH};
        let Ok(now) = SystemTime::now().duration_since(UNIX_EPOCH) else {
            return true;
        };

        let interval_secs = u64::from(self.update_check_interval_hours) * 3600;
        now.as_secs().saturating_sub(last_check) >= interval_secs
    }

    /// Load settings from file, or return defaults if file doesn't exist
    pub fn load() -> Self {
        Self::settings_path()
            .and_then(|path| {
                if path.exists() {
                    std::fs::read_to_string(&path).ok()
                } else {
                    None
                }
            })
            .and_then(|content| toml::from_str(&content).ok())
            .unwrap_or_default()
    }

    /// Save settings to file
    pub fn save(&self) -> Result<(), Box<dyn std::error::Error>> {
        if let Some(path) = Self::settings_path() {
            // Ensure directory exists
            if let Some(parent) = path.parent() {
                std::fs::create_dir_all(parent)?;
            }
            let content = toml::to_string_pretty(self)?;
            std::fs::write(path, content)?;
        }
        Ok(())
    }
}
