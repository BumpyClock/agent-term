use serde::{Deserialize, Serialize};
use std::path::PathBuf;

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub enum Theme {
    Light,
    Dark,
    System,
}

impl Default for Theme {
    fn default() -> Self {
        Theme::System
    }
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
    // General
    pub check_for_updates: bool,
    pub auto_update: bool,

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
}

fn default_blur_enabled() -> bool {
    true
}

fn default_warm_search_index() -> bool {
    true
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            // General
            check_for_updates: true,
            auto_update: false,

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
        }
    }
}

impl AppSettings {
    /// Get the settings file path
    fn settings_path() -> Option<PathBuf> {
        dirs::config_dir().map(|p| p.join("agentterm").join("settings.toml"))
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
