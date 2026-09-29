use gpui_kit::{App, Global};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[allow(dead_code, reason = "vendored token catalog keeps the full fork vocabulary")]
pub enum ThemeShadowToken {
    None,
    Xs,
    Sm,
    Md,
    Lg,
    Xl,
}

#[derive(Clone, Copy, Debug)]
pub struct ThemeElevation {
    pub surface_flyout_shadow: ThemeShadowToken,
    pub surface_panel_shadow: ThemeShadowToken,
    pub surface_card_shadow: ThemeShadowToken,
}

#[derive(Clone, Copy, Debug)]
pub struct ThemeMaterial {
    pub flyout_light_opacity: f32,
    pub flyout_dark_opacity: f32,
    pub panel_light_opacity: f32,
    pub panel_dark_opacity: f32,
    pub card_light_opacity: f32,
    pub card_dark_opacity: f32,
    pub subtle_stroke_light_opacity: f32,
    pub subtle_stroke_dark_opacity: f32,
}

/// Material and elevation tokens that gpui-component's `Theme` cannot hold.
#[derive(Clone, Copy, Debug)]
pub struct MaterialTheme {
    pub elevation: ThemeElevation,
    pub material: ThemeMaterial,
}

impl Default for MaterialTheme {
    fn default() -> Self {
        Self {
            elevation: ThemeElevation {
                surface_flyout_shadow: ThemeShadowToken::Sm,
                surface_panel_shadow: ThemeShadowToken::Lg,
                surface_card_shadow: ThemeShadowToken::Sm,
            },
            material: ThemeMaterial {
                flyout_light_opacity: 0.75,
                flyout_dark_opacity: 0.85,
                panel_light_opacity: 0.85,
                panel_dark_opacity: 0.90,
                card_light_opacity: 0.70,
                card_dark_opacity: 0.05,
                subtle_stroke_light_opacity: 0.5,
                subtle_stroke_dark_opacity: 0.5,
            },
        }
    }
}

impl Global for MaterialTheme {}

impl MaterialTheme {
    pub fn get(cx: &App) -> Self {
        cx.try_global::<Self>().copied().unwrap_or_default()
    }

    pub fn set(self, cx: &mut App) {
        cx.set_global(self);
    }
}

