//! Embedded assets for agentterm-gpui.
//!
//! Uses rust-embed to bundle Lucide icons at compile time.

use anyhow::anyhow;
use gpui::{AssetSource, SharedString};
use gpui_component_assets::Assets as ComponentAssets;
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

        if let Some(file) = Self::get(path) {
            return Ok(Some(file.data));
        }

        if !path.contains('/') {
            let noise_path = format!("noise/{path}");
            if let Some(file) = Self::get(&noise_path) {
                return Ok(Some(file.data));
            }
        }

        ComponentAssets
            .load(path)?
            .map(Some)
            .ok_or_else(|| anyhow!("asset not found: {path}"))
    }

    fn list(&self, path: &str) -> anyhow::Result<Vec<SharedString>> {
        let mut asset_paths: Vec<SharedString> = Self::iter()
            .filter(|p| p.starts_with(path))
            .map(|p| SharedString::from(p.to_string()))
            .collect();
        asset_paths.extend(ComponentAssets.list(path)?);
        asset_paths.sort();
        asset_paths.dedup();
        Ok(asset_paths)
    }
}

#[cfg(test)]
mod tests {
    use super::Assets;
    use gpui::AssetSource;

    #[test]
    fn loads_noise_asset_by_bare_filename() {
        let assets = Assets;
        let from_bare_name = assets
            .load("NoiseAsset_256.png")
            .expect("loading bare filename should succeed")
            .expect("asset should exist");
        let from_embedded_path = assets
            .load("noise/NoiseAsset_256.png")
            .expect("loading embedded path should succeed")
            .expect("asset should exist");

        assert_eq!(from_bare_name.as_ref(), from_embedded_path.as_ref());
    }

    #[test]
    fn loads_gpui_component_surface_noise_asset() {
        let assets = Assets;
        let from_surface_path = assets
            .load("surface/NoiseAsset_256.png")
            .expect("loading surface path should succeed")
            .expect("asset should exist");

        assert!(!from_surface_path.is_empty());
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
