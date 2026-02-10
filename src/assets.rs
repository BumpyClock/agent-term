//! Embedded assets for agentterm-gpui.
//!
//! Uses rust-embed to bundle Lucide icons at compile time.

use gpui::{AssetSource, SharedString};
use rust_embed::RustEmbed;
use std::borrow::Cow;

#[derive(RustEmbed)]
#[folder = "assets"]
#[include = "icons/*.svg"]
#[include = "tool-icons/*"]
#[include = "noise/*"]
pub struct Assets;

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

        Err(anyhow::anyhow!("asset not found: {path}"))
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
}
