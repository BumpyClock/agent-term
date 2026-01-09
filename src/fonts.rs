//! Font presets for terminal font selection.

/// A font option for the terminal font selector.
#[derive(Debug, Clone)]
pub struct FontOption {
    /// Display name shown in the dropdown
    pub name: &'static str,
    /// Font family name used by the system
    pub family: &'static str,
}

/// Returns a curated list of monospace fonts suitable for terminal use.
///
/// The list includes:
/// - Bundled fonts (to be added to assets/fonts)
/// - Common system monospace fonts available on macOS, Windows, and Linux
pub fn font_presets() -> Vec<FontOption> {
    vec![
        // Popular programming/terminal fonts (often bundled or commonly installed)
        FontOption {
            name: "JetBrains Mono",
            family: "JetBrains Mono",
        },
        FontOption {
            name: "Fira Code",
            family: "Fira Code",
        },
        FontOption {
            name: "FiraCode Nerd Font",
            family: "FiraCode Nerd Font",
        },
        // macOS system fonts
        FontOption {
            name: "SF Mono",
            family: "SF Mono",
        },
        FontOption {
            name: "Menlo",
            family: "Menlo",
        },
        FontOption {
            name: "Monaco",
            family: "Monaco",
        },
        // Windows system fonts
        FontOption {
            name: "Consolas",
            family: "Consolas",
        },
        FontOption {
            name: "Cascadia Code",
            family: "Cascadia Code",
        },
        FontOption {
            name: "Cascadia Mono",
            family: "Cascadia Mono",
        },
        // Cross-platform fonts
        FontOption {
            name: "Source Code Pro",
            family: "Source Code Pro",
        },
        FontOption {
            name: "Ubuntu Mono",
            family: "Ubuntu Mono",
        },
        FontOption {
            name: "IBM Plex Mono",
            family: "IBM Plex Mono",
        },
        FontOption {
            name: "Hack",
            family: "Hack",
        },
        FontOption {
            name: "Iosevka",
            family: "Iosevka",
        },
        FontOption {
            name: "Inconsolata",
            family: "Inconsolata",
        },
        // Fallback system fonts
        FontOption {
            name: "Courier New",
            family: "Courier New",
        },
    ]
}

/// Find the index of a font by its family name.
pub fn find_font_index(family: &str) -> Option<usize> {
    font_presets()
        .iter()
        .position(|f| f.family == family)
}
