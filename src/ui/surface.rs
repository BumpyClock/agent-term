//! Glass-style surface presets. `backdrop_blur` does not exist in gpui-pre 0.3.7,
//! so surfaces keep the translucent background, stroke, shadow, and noise only.

use gpui_kit::component::ActiveTheme;
use gpui_kit::{
    App, Div, Hsla, ImageSource, IntoElement, ObjectFit, ParentElement, Pixels, Resource, Styled,
    StyledImage, Window, div, img, px,
};

use super::material_theme::{MaterialTheme, ThemeShadowToken};

const GLASS_NOISE_ASSET_PATH: &str = "noise/NoiseAsset_256.png";
const GLASS_NOISE_TILE_SIZE_BASE: f32 = 128.0;

#[derive(Debug, Clone, Copy, Default)]
pub struct SurfaceContext {
    pub blur_enabled: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
#[allow(dead_code, reason = "vendored token catalog keeps the full fork vocabulary")]
pub enum NoiseIntensity {
    None,
    #[default]
    Subtle,
    Heavy,
}

impl NoiseIntensity {
    pub fn opacity(&self) -> f32 {
        match self {
            NoiseIntensity::None => 0.0,
            NoiseIntensity::Subtle => 0.02,
            NoiseIntensity::Heavy => 0.04,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ElevationToken {
    #[default]
    None,
    Xs,
    Sm,
    Md,
    Lg,
    Xl,
}

impl ElevationToken {
    pub fn apply<E: Styled>(&self, element: E) -> E {
        match self {
            ElevationToken::None => element,
            ElevationToken::Xs | ElevationToken::Sm => element.shadow_sm(),
            ElevationToken::Md => element.shadow_md(),
            ElevationToken::Lg => element.shadow_lg(),
            ElevationToken::Xl => element.shadow_xl(),
        }
    }
}

impl From<ThemeShadowToken> for ElevationToken {
    fn from(token: ThemeShadowToken) -> Self {
        match token {
            ThemeShadowToken::None => ElevationToken::None,
            ThemeShadowToken::Xs => ElevationToken::Xs,
            ThemeShadowToken::Sm => ElevationToken::Sm,
            ThemeShadowToken::Md => ElevationToken::Md,
            ThemeShadowToken::Lg => ElevationToken::Lg,
            ThemeShadowToken::Xl => ElevationToken::Xl,
        }
    }
}

#[derive(Debug, Clone, Copy, Default)]
#[allow(dead_code, reason = "vendored token catalog keeps the full fork vocabulary")]
pub enum SurfaceColorSource {
    #[default]
    Popover,
    White,
    Sidebar,
    Background,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
#[allow(dead_code, reason = "vendored token catalog keeps the full fork vocabulary")]
pub enum SurfaceKind {
    #[default]
    Base,
    Flyout,
    Panel,
    Card,
}

#[derive(Debug, Clone, Copy)]
pub struct SurfaceBackground {
    pub color_source: SurfaceColorSource,
    pub light_opacity: f32,
    pub dark_opacity: f32,
}

impl SurfaceBackground {
    pub fn resolve(&self, cx: &App) -> Hsla {
        let base = match self.color_source {
            SurfaceColorSource::Popover => cx.theme().popover,
            SurfaceColorSource::White => gpui_kit::white(),
            SurfaceColorSource::Sidebar => cx.theme().sidebar,
            SurfaceColorSource::Background => cx.theme().background,
        };
        let opacity = if cx.theme().mode.is_dark() {
            self.dark_opacity
        } else {
            self.light_opacity
        };
        base.opacity(opacity)
    }
}

#[derive(Debug, Clone, Copy, Default)]
#[allow(dead_code, reason = "vendored token catalog keeps the full fork vocabulary")]
pub enum StrokeColor {
    #[default]
    Subtle,
    Default,
}

#[derive(Debug, Clone, Copy)]
pub struct StrokeSpec {
    pub width: Pixels,
    pub color: StrokeColor,
}

impl StrokeSpec {
    pub fn subtle() -> Self {
        Self {
            width: px(1.0),
            color: StrokeColor::Subtle,
        }
    }

    #[allow(dead_code, reason = "vendored token catalog keeps the full fork vocabulary")]
    pub fn default_border() -> Self {
        Self {
            width: px(1.0),
            color: StrokeColor::Default,
        }
    }

    pub fn resolve_color(&self, cx: &App) -> Hsla {
        let material = MaterialTheme::get(cx).material;
        let subtle_stroke_opacity = if cx.theme().mode.is_dark() {
            material.subtle_stroke_dark_opacity
        } else {
            material.subtle_stroke_light_opacity
        };
        match self.color {
            StrokeColor::Subtle => cx.theme().border.opacity(subtle_stroke_opacity),
            StrokeColor::Default => cx.theme().border,
        }
    }
}

#[derive(Debug, Clone)]
pub struct SurfacePreset {
    pub kind: SurfaceKind,
    pub noise_intensity: NoiseIntensity,
    pub background: SurfaceBackground,
    pub elevation: ElevationToken,
    pub stroke: Option<StrokeSpec>,
    pub radius: Option<Pixels>,
}

impl SurfacePreset {
    pub fn panel() -> Self {
        Self {
            kind: SurfaceKind::Panel,
            noise_intensity: NoiseIntensity::Heavy,
            background: SurfaceBackground {
                color_source: SurfaceColorSource::Sidebar,
                light_opacity: 0.85,
                dark_opacity: 0.90,
            },
            elevation: ElevationToken::Lg,
            stroke: Some(StrokeSpec::subtle()),
            radius: Some(px(16.0)),
        }
    }

    pub fn wrap_with_bounds(
        &self,
        content: impl IntoElement,
        width: Pixels,
        height: Pixels,
        window: &Window,
        cx: &App,
        context: SurfaceContext,
    ) -> Div {
        let tokens = MaterialTheme::get(cx);
        let radius = self.radius.unwrap_or(cx.theme().radius);
        let background = self.resolve_background(tokens);
        let elevation = self.resolve_elevation(tokens);

        let background_color = background.resolve(cx);
        let noise_opacity = self.noise_intensity.opacity();
        let should_render_noise = context.blur_enabled && noise_opacity > 0.0;

        let mut surface = div().relative().rounded(radius).overflow_hidden();

        if background_color.a > 0.0 {
            surface = surface.bg(background_color);
        }

        if let Some(ref stroke) = self.stroke {
            surface = surface
                .border(stroke.width)
                .border_color(stroke.resolve_color(cx));
        }

        surface = elevation.apply(surface);

        if should_render_noise {
            surface = surface.child(render_noise_overlay(
                width,
                height,
                radius,
                noise_opacity,
                window.scale_factor(),
            ));
        }

        surface.child(content)
    }

    fn resolve_background(&self, tokens: MaterialTheme) -> SurfaceBackground {
        let material = tokens.material;
        match self.kind {
            SurfaceKind::Flyout => SurfaceBackground {
                color_source: self.background.color_source,
                light_opacity: material.flyout_light_opacity,
                dark_opacity: material.flyout_dark_opacity,
            },
            SurfaceKind::Panel => SurfaceBackground {
                color_source: self.background.color_source,
                light_opacity: material.panel_light_opacity,
                dark_opacity: material.panel_dark_opacity,
            },
            SurfaceKind::Card => SurfaceBackground {
                color_source: self.background.color_source,
                light_opacity: material.card_light_opacity,
                dark_opacity: material.card_dark_opacity,
            },
            SurfaceKind::Base => self.background,
        }
    }

    fn resolve_elevation(&self, tokens: MaterialTheme) -> ElevationToken {
        match self.kind {
            SurfaceKind::Flyout => tokens.elevation.surface_flyout_shadow.into(),
            SurfaceKind::Panel => tokens.elevation.surface_panel_shadow.into(),
            SurfaceKind::Card => tokens.elevation.surface_card_shadow.into(),
            SurfaceKind::Base => self.elevation,
        }
    }
}

pub fn render_noise_overlay(
    width: Pixels,
    height: Pixels,
    radius: Pixels,
    opacity: f32,
    scale_factor: f32,
) -> impl IntoElement {
    let tile_size_value = (GLASS_NOISE_TILE_SIZE_BASE / scale_factor.max(1.0)).round();
    let tile_size = px(tile_size_value);
    let columns = ((width / tile_size).max(0.0).ceil() as usize).max(1) + 12;
    let rows = ((height / tile_size).max(0.0).ceil() as usize).max(1) + 12;
    let tiled_width = px(tile_size_value * columns as f32);
    let tiled_height = px(tile_size_value * rows as f32);
    let tiles = columns.saturating_mul(rows);

    div()
        .absolute()
        .inset_0()
        .size_full()
        .overflow_hidden()
        .rounded(radius)
        .child(
            div()
                .absolute()
                .top_0()
                .left_0()
                .w(tiled_width)
                .h(tiled_height)
                .flex()
                .flex_wrap()
                .items_start()
                .justify_start()
                .children((0..tiles).map(move |_| {
                    img(ImageSource::Resource(Resource::Embedded(
                        GLASS_NOISE_ASSET_PATH.into(),
                    )))
                    .w(tile_size)
                    .h(tile_size)
                    .flex_none()
                    .object_fit(ObjectFit::Cover)
                    .opacity(opacity)
                })),
        )
}
