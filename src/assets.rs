//! Embedded assets for agentterm-gpui.
//!
//! Uses rust-embed to bundle Lucide icons at compile time.

use anyhow::anyhow;
use gpui::{AssetSource, SharedString};
use rust_embed::RustEmbed;
use std::borrow::Cow;
use std::path::Path;

#[derive(RustEmbed)]
#[folder = "assets"]
#[include = "fonts/*"]
#[include = "icons/*.svg"]
#[include = "tool-icons/*"]
#[include = "noise/*"]
pub struct Assets;

pub fn embedded_font_data() -> Vec<Cow<'static, [u8]>> {
    Assets::iter()
        .filter(|path| path.starts_with("fonts/"))
        .filter(|path| {
            matches!(
                Path::new(path.as_ref())
                    .extension()
                    .and_then(|extension| extension.to_str()),
                Some("ttf" | "otf" | "ttc")
            )
        })
        .filter_map(|path| Assets::get(&path).map(|file| file.data))
        .collect()
}

impl AssetSource for Assets {
    fn load(&self, path: &str) -> anyhow::Result<Option<Cow<'static, [u8]>>> {
        if path.is_empty() {
            return Ok(None);
        }

        Self::get(path)
            .map(|file| Some(file.data))
            .ok_or_else(|| anyhow!("asset not found: {path}"))
    }

    fn list(&self, path: &str) -> anyhow::Result<Vec<SharedString>> {
        Ok(Self::iter()
            .filter(|p| p.starts_with(path))
            .map(|p| SharedString::from(p.to_string()))
            .collect())
    }
}

#[cfg(test)]
mod tests {
    use super::Assets;
    use gpui::AssetSource;
    use gpui_component_assets::{Assets as ComponentAssets, chain as chain_asset_sources};

    #[test]
    fn app_assets_chain_falls_back_to_gpui_component_assets() {
        let assets = chain_asset_sources(Assets, ComponentAssets);
        let from_surface_path = assets
            .load("surface/NoiseAsset_256.png")
            .expect("loading surface path should succeed")
            .expect("asset should exist");

        assert!(!from_surface_path.is_empty());
        assert!(assets.load("NoiseAsset_256.png").is_err());
    }

    #[test]
    fn embeds_terminal_fonts() {
        let font_data = super::embedded_font_data();

        assert!(
            !font_data.is_empty(),
            "expected bundled terminal fonts to be embedded"
        );
    }
}
