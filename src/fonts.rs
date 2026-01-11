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
/// - Bundled Nerd Fonts (in assets/fonts)
/// - Common system monospace fonts available on macOS, Windows, and Linux
pub fn font_presets() -> Vec<FontOption> {
    vec![
        // ===== Bundled Nerd Fonts (these match the TTF files in assets/fonts) =====
        FontOption {
            name: "JetBrainsMono Nerd Font",
            family: "JetBrainsMono Nerd Font",
        },
        FontOption {
            name: "FiraCode Nerd Font",
            family: "FiraCode Nerd Font",
        },
        FontOption {
            name: "CaskaydiaCove Nerd Font",
            family: "CaskaydiaCove Nerd Font",
        },
        FontOption {
            name: "Hack Nerd Font",
            family: "Hack Nerd Font",
        },
        FontOption {
            name: "MesloLGS NF",
            family: "MesloLGS NF",
        },
        FontOption {
            name: "NotoSans Nerd Font",
            family: "NotoSans Nerd Font",
        },
        // ===== macOS System Fonts =====
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
        // ===== Windows System Fonts =====
        FontOption {
            name: "Consolas",
            family: "Consolas",
        },
        FontOption {
            name: "Cascadia Code",
            family: "Cascadia Code",
        },
        // ===== Cross-platform / Commonly Installed =====
        FontOption {
            name: "Source Code Pro",
            family: "Source Code Pro",
        },
        FontOption {
            name: "Ubuntu Mono",
            family: "Ubuntu Mono",
        },
        // ===== Fallback =====
        FontOption {
            name: "Courier New",
            family: "Courier New",
        },
    ]
}

/// Find the index of a font by its family name.
pub fn find_font_index(family: &str) -> Option<usize> {
    font_presets().iter().position(|f| f.family == family)
}
