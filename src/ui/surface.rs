//! Glass-style surface presets. `backdrop_blur` does not exist in gpui-pre 0.3.7,
//! so surfaces keep the translucent background, stroke, shadow, and noise only.

use gpui_kit::component::ActiveTheme;
use gpui_kit::{
    App, Div, ImageSource, IntoElement, ObjectFit, ParentElement, Pixels, Resource, Styled,
    StyledImage, Window, div, img, px,
};

const GLASS_NOISE_ASSET_PATH: &str = "noise/NoiseAsset_256.png";
const GLASS_NOISE_TILE_SIZE_BASE: f32 = 128.0;

const PANEL_LIGHT_OPACITY: f32 = 0.85;
const PANEL_DARK_OPACITY: f32 = 0.90;
const STROKE_OPACITY: f32 = 0.5;
const PANEL_RADIUS: f32 = 16.0;

#[derive(Debug, Clone, Copy, Default)]
pub struct SurfaceContext {
    pub blur_enabled: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NoiseIntensity {
    Heavy,
}

impl NoiseIntensity {
    pub fn opacity(&self) -> f32 {
        match self {
            NoiseIntensity::Heavy => 0.04,
        }
    }
}

#[derive(Debug, Clone)]
pub struct SurfacePreset {
    pub noise_intensity: NoiseIntensity,
    pub radius: Pixels,
}

impl SurfacePreset {
    pub fn panel() -> Self {
        Self {
            noise_intensity: NoiseIntensity::Heavy,
            radius: px(PANEL_RADIUS),
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
        let panel_opacity = if cx.theme().mode.is_dark() {
            PANEL_DARK_OPACITY
        } else {
            PANEL_LIGHT_OPACITY
        };
        let background_color = cx.theme().sidebar.opacity(panel_opacity);
        let noise_opacity = self.noise_intensity.opacity();
        let should_render_noise = context.blur_enabled && noise_opacity > 0.0;

        let mut surface = div().relative().rounded(self.radius).overflow_hidden();

        if background_color.a > 0.0 {
            surface = surface.bg(background_color);
        }

        surface = surface
            .border(px(1.0))
            .border_color(cx.theme().border.opacity(STROKE_OPACITY))
            .shadow_lg();

        if should_render_noise {
            surface = surface.child(render_noise_overlay(
                width,
                height,
                self.radius,
                noise_opacity,
                window.scale_factor(),
            ));
        }

        surface.child(content)
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
