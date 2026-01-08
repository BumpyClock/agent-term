//! Lucide icon metadata for search functionality.
//!
//! Provides a list of all embedded Lucide icons for the icon picker.

use once_cell::sync::Lazy;
use rust_embed::RustEmbed;

/// Reference to the embedded assets for iteration.
#[derive(RustEmbed)]
#[folder = "assets"]
#[include = "icons/*.svg"]
struct IconAssets;

/// Metadata for a Lucide icon.
#[derive(Debug, Clone)]
pub struct LucideIconMeta {
    /// Icon name (filename without extension)
    pub name: String,
    /// Human-readable display name
    pub display_name: String,
}

/// All Lucide icons, lazily initialized from embedded assets.
pub static LUCIDE_ICONS: Lazy<Vec<LucideIconMeta>> = Lazy::new(|| {
    let mut icons: Vec<_> = IconAssets::iter()
        .filter(|p| p.starts_with("icons/") && p.ends_with(".svg"))
        .filter_map(|p| {
            let name = p
                .strip_prefix("icons/")?
                .strip_suffix(".svg")?
                .to_string();
            let display_name = to_display_name(&name);
            Some(LucideIconMeta { name, display_name })
        })
        .collect();

    // Sort alphabetically by display name
    icons.sort_by(|a, b| a.display_name.cmp(&b.display_name));
    icons
});

/// Convert kebab-case icon name to Title Case display name.
fn to_display_name(name: &str) -> String {
    name.split('-')
        .map(|word| {
            let mut chars = word.chars();
            match chars.next() {
                Some(first) => {
                    first.to_uppercase().chain(chars).collect::<String>()
                }
                None => String::new(),
            }
        })
        .collect::<Vec<_>>()
        .join(" ")
}

/// Search Lucide icons by query string.
/// Returns icons whose name or display name contains the query.
pub fn search_lucide_icons(query: &str) -> Vec<&LucideIconMeta> {
    let query = query.to_lowercase();
    if query.is_empty() {
        return LUCIDE_ICONS.iter().collect();
    }
    LUCIDE_ICONS
        .iter()
        .filter(|icon| {
            icon.name.to_lowercase().contains(&query)
                || icon.display_name.to_lowercase().contains(&query)
        })
        .collect()
}

/// Get total count of available Lucide icons.
pub fn lucide_icon_count() -> usize {
    LUCIDE_ICONS.len()
}
