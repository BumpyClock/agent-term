use std::rc::Rc;

use gpui::{App, Window, WindowAppearance};
use gpui_component::theme::{Theme as GpuiTheme, ThemeConfig, ThemeConfigColors, ThemeMode};
use gpui_term::set_terminal_palette;

use crate::settings::{AppSettings, Theme};
use crate::terminal_schemes;

#[derive(Clone, Copy)]
pub struct AccentColor {
    pub id: &'static str,
    pub name: &'static str,
    pub hex: &'static str,
    pub description: &'static str,
}

pub fn accent_colors() -> &'static [AccentColor] {
    static COLORS: [AccentColor; 4] = [
        AccentColor {
            id: "periwinkle",
            name: "Periwinkle",
            hex: "#9D8FD4",
            description: "Soft blue-violet, calming",
        },
        AccentColor {
            id: "dusty-rose",
            name: "Dusty Rose",
            hex: "#D4A5A5",
            description: "Warm pink-mauve",
        },
        AccentColor {
            id: "sage-green",
            name: "Sage Green",
            hex: "#8FBC8F",
            description: "Natural, easy on eyes",
        },
        AccentColor {
            id: "soft-teal",
            name: "Soft Teal",
            hex: "#7FBFBF",
            description: "Professional, calming",
        },
    ];

    &COLORS
}

pub fn default_accent_color_id() -> &'static str {
    "periwinkle"
}

pub fn resolve_accent_color(id: &str) -> AccentColor {
    if let Some(color) = accent_colors().iter().find(|color| color.id == id) {
        return *color;
    }

    if id.starts_with('#') {
        // Leak the string to get a 'static lifetime for custom hex colors
        let hex: &'static str = Box::leak(id.to_string().into_boxed_str());
        return AccentColor {
            id: "custom",
            name: "Custom",
            hex,
            description: "Custom accent color",
        };
    }

    accent_colors()
        .iter()
        .find(|color| color.id == default_accent_color_id())
        .copied()
        .unwrap_or(AccentColor {
            id: "periwinkle",
            name: "Periwinkle",
            hex: "#9D8FD4",
            description: "Soft blue-violet, calming",
        })
}

pub fn apply_theme_from_settings(
    settings: &AppSettings,
    window: Option<&mut Window>,
    cx: &mut App,
) -> ThemeMode {
    let accent = resolve_accent_color(&settings.accent_color);
    let light_config = build_theme_config(ThemeMode::Light, accent);
    let dark_config = build_theme_config(ThemeMode::Dark, accent);

    // Resolve theme mode based on settings and window appearance
    let mode = match settings.theme {
        Theme::Light => ThemeMode::Light,
        Theme::Dark => ThemeMode::Dark,
        Theme::System => {
            let appearance = window
                .as_ref()
                .map(|w| w.appearance())
                .unwrap_or_else(|| cx.window_appearance());
            match appearance {
                WindowAppearance::Dark | WindowAppearance::VibrantDark => ThemeMode::Dark,
                WindowAppearance::Light | WindowAppearance::VibrantLight => ThemeMode::Light,
            }
        }
    };

    {
        let theme = GpuiTheme::global_mut(cx);
        theme.light_theme = Rc::new(light_config);
        theme.dark_theme = Rc::new(dark_config);
    }

    GpuiTheme::change(mode, window, cx);
    GpuiTheme::global_mut(cx).colors.background = gpui::transparent_black();
    mode
}

pub fn apply_terminal_scheme(settings: &AppSettings, mode: ThemeMode) {
    let palette =
        terminal_schemes::terminal_palette_for_scheme(&settings.terminal_color_scheme, mode);
    set_terminal_palette(palette);
}

#[derive(Clone, Copy)]
struct AppPalette {
    background: &'static str,
    foreground: &'static str,
    primary_foreground: &'static str,
    secondary: &'static str,
    secondary_foreground: &'static str,
    muted: &'static str,
    muted_foreground: &'static str,
    accent_foreground: &'static str,
    danger: &'static str,
    border: &'static str,
    input: &'static str,
    popover: &'static str,
    popover_foreground: &'static str,
    sidebar: &'static str,
    sidebar_foreground: &'static str,
    sidebar_primary_foreground: &'static str,
    sidebar_accent: &'static str,
    sidebar_accent_foreground: &'static str,
    sidebar_border: &'static str,
    chart_1: &'static str,
    chart_2: &'static str,
    chart_3: &'static str,
    chart_4: &'static str,
    chart_5: &'static str,
}

fn build_theme_config(mode: ThemeMode, accent: AccentColor) -> ThemeConfig {
    let palette = palette_for_mode(mode);
    let mut colors = ThemeConfigColors::default();

    colors.background = Some(palette.background.into());
    colors.foreground = Some(palette.foreground.into());
    colors.border = Some(palette.border.into());
    colors.muted = Some(palette.muted.into());
    colors.muted_foreground = Some(palette.muted_foreground.into());
    colors.primary = Some(accent.hex.into());
    colors.primary_foreground = Some(palette.primary_foreground.into());
    colors.secondary = Some(palette.secondary.into());
    colors.secondary_foreground = Some(palette.secondary_foreground.into());
    colors.accent = Some(accent.hex.into());
    colors.accent_foreground = Some(palette.accent_foreground.into());
    colors.danger = Some(palette.danger.into());
    colors.input = Some(palette.input.into());
    colors.ring = Some(accent.hex.into());
    colors.popover = Some(palette.popover.into());
    colors.popover_foreground = Some(palette.popover_foreground.into());
    colors.sidebar = Some(palette.sidebar.into());
    colors.sidebar_foreground = Some(palette.sidebar_foreground.into());
    colors.sidebar_primary = Some(accent.hex.into());
    colors.sidebar_primary_foreground = Some(palette.sidebar_primary_foreground.into());
    colors.sidebar_accent = Some(palette.sidebar_accent.into());
    colors.sidebar_accent_foreground = Some(palette.sidebar_accent_foreground.into());
    colors.sidebar_border = Some(palette.sidebar_border.into());
    colors.chart_1 = Some(palette.chart_1.into());
    colors.chart_2 = Some(palette.chart_2.into());
    colors.chart_3 = Some(palette.chart_3.into());
    colors.chart_4 = Some(palette.chart_4.into());
    colors.chart_5 = Some(palette.chart_5.into());

    ThemeConfig {
        is_default: true,
        name: match mode {
            ThemeMode::Light => "AgentTerm Light".into(),
            ThemeMode::Dark => "AgentTerm Dark".into(),
        },
        mode,
        colors,
        ..ThemeConfig::default()
    }
}

fn palette_for_mode(mode: ThemeMode) -> AppPalette {
    match mode {
        ThemeMode::Light => AppPalette {
            background: "#f9fafb",
            foreground: "#202127",
            primary_foreground: "#0b0b0b",
            secondary: "#edeef2",
            secondary_foreground: "#202127",
            muted: "#e6e8eb",
            muted_foreground: "#616369",
            accent_foreground: "#202127",
            danger: "#cc272e",
            border: "#d5d7de",
            input: "#d5d7de",
            popover: "#fffffff2",
            popover_foreground: "#202127",
            sidebar: "#f4f5f9",
            sidebar_foreground: "#202127",
            sidebar_primary_foreground: "#0b0b0b",
            sidebar_accent: "#e6e8eb",
            sidebar_accent_foreground: "#202127",
            sidebar_border: "#d5d7de",
            chart_1: "#f54900",
            chart_2: "#009689",
            chart_3: "#104e64",
            chart_4: "#ffb900",
            chart_5: "#fe9a00",
        },
        ThemeMode::Dark => AppPalette {
            background: "#111111",
            foreground: "#cdcdcd",
            primary_foreground: "#0b0b0b",
            secondary: "#2d2d2d",
            secondary_foreground: "#cdcdcd",
            muted: "#232323",
            muted_foreground: "#797979",
            accent_foreground: "#cdcdcd",
            danger: "#ff6468",
            border: "#2d2d2d",
            input: "#2d2d2d",
            popover: "#111111f2",
            popover_foreground: "#cdcdcd",
            sidebar: "#19191952",
            sidebar_foreground: "#cdcdcd",
            sidebar_primary_foreground: "#0b0b0b",
            sidebar_accent: "#282828",
            sidebar_accent_foreground: "#cdcdcd",
            sidebar_border: "#2d2d2d",
            chart_1: "#1048e6",
            chart_2: "#00bc7c",
            chart_3: "#fe9900",
            chart_4: "#ad46ff",
            chart_5: "#ff2058",
        },
    }
}

pub fn surface_background(mode: ThemeMode) -> gpui::Hsla {
    parse_hex_color(palette_for_mode(mode).background)
}

fn parse_hex_color(color: &str) -> gpui::Hsla {
    let rgba = gpui::Rgba::try_from(color).unwrap_or(gpui::Rgba {
        r: 0.0,
        g: 0.0,
        b: 0.0,
        a: 1.0,
    });
    rgba.into()
}
