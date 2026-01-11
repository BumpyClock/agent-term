use gpui::Hsla;
use std::sync::{LazyLock, RwLock};

#[derive(Clone, Copy, Debug)]
pub struct TerminalPalette {
    pub foreground: Hsla,
    pub background: Hsla,
    pub cursor: Hsla,
    pub selection: Hsla,
    pub black: Hsla,
    pub red: Hsla,
    pub green: Hsla,
    pub yellow: Hsla,
    pub blue: Hsla,
    pub magenta: Hsla,
    pub cyan: Hsla,
    pub white: Hsla,
    pub bright_black: Hsla,
    pub bright_red: Hsla,
    pub bright_green: Hsla,
    pub bright_yellow: Hsla,
    pub bright_blue: Hsla,
    pub bright_magenta: Hsla,
    pub bright_cyan: Hsla,
    pub bright_white: Hsla,
    pub bright_foreground: Hsla,
    pub dim_foreground: Hsla,
    pub dim_black: Hsla,
    pub dim_red: Hsla,
    pub dim_green: Hsla,
    pub dim_yellow: Hsla,
    pub dim_blue: Hsla,
    pub dim_magenta: Hsla,
    pub dim_cyan: Hsla,
    pub dim_white: Hsla,
}

impl TerminalPalette {
    pub fn from_base(
        foreground: Hsla,
        background: Hsla,
        cursor: Hsla,
        selection: Hsla,
        black: Hsla,
        red: Hsla,
        green: Hsla,
        yellow: Hsla,
        blue: Hsla,
        magenta: Hsla,
        cyan: Hsla,
        white: Hsla,
        bright_black: Hsla,
        bright_red: Hsla,
        bright_green: Hsla,
        bright_yellow: Hsla,
        bright_blue: Hsla,
        bright_magenta: Hsla,
        bright_cyan: Hsla,
        bright_white: Hsla,
    ) -> Self {
        let dim_foreground = dim_color(foreground);
        let dim_black = dim_color(black);
        let dim_red = dim_color(red);
        let dim_green = dim_color(green);
        let dim_yellow = dim_color(yellow);
        let dim_blue = dim_color(blue);
        let dim_magenta = dim_color(magenta);
        let dim_cyan = dim_color(cyan);
        let dim_white = dim_color(white);

        Self {
            foreground,
            background,
            cursor,
            selection,
            black,
            red,
            green,
            yellow,
            blue,
            magenta,
            cyan,
            white,
            bright_black,
            bright_red,
            bright_green,
            bright_yellow,
            bright_blue,
            bright_magenta,
            bright_cyan,
            bright_white,
            bright_foreground: bright_white,
            dim_foreground,
            dim_black,
            dim_red,
            dim_green,
            dim_yellow,
            dim_blue,
            dim_magenta,
            dim_cyan,
            dim_white,
        }
    }
}

impl Default for TerminalPalette {
    fn default() -> Self {
        Self::from_base(
            Hsla::white(),
            Hsla::transparent_black(),
            Hsla::white(),
            Hsla::black(),
            hsla_from_rgb(0x00, 0x00, 0x00),
            hsla_from_rgb(0xCD, 0x00, 0x00),
            hsla_from_rgb(0x00, 0xCD, 0x00),
            hsla_from_rgb(0xCD, 0xCD, 0x00),
            hsla_from_rgb(0x00, 0x00, 0xEE),
            hsla_from_rgb(0xCD, 0x00, 0xCD),
            hsla_from_rgb(0x00, 0xCD, 0xCD),
            hsla_from_rgb(0xE5, 0xE5, 0xE5),
            hsla_from_rgb(0x7F, 0x7F, 0x7F),
            hsla_from_rgb(0xFF, 0x00, 0x00),
            hsla_from_rgb(0x00, 0xFF, 0x00),
            hsla_from_rgb(0xFF, 0xFF, 0x00),
            hsla_from_rgb(0x5C, 0x5C, 0xFF),
            hsla_from_rgb(0xFF, 0x00, 0xFF),
            hsla_from_rgb(0x00, 0xFF, 0xFF),
            hsla_from_rgb(0xFF, 0xFF, 0xFF),
        )
    }
}

pub fn set_terminal_palette(palette: TerminalPalette) {
    if let Ok(mut current) = TERMINAL_PALETTE.write() {
        *current = palette;
    }
}

pub fn terminal_palette() -> TerminalPalette {
    TERMINAL_PALETTE
        .read()
        .map(|palette| *palette)
        .unwrap_or_default()
}

fn dim_color(color: Hsla) -> Hsla {
    let l = (color.l * 0.7).clamp(0.0, 1.0);
    Hsla { l, ..color }
}

fn hsla_from_rgb(r: u8, g: u8, b: u8) -> Hsla {
    let rgba = gpui::Rgba {
        r: r as f32 / 255.0,
        g: g as f32 / 255.0,
        b: b as f32 / 255.0,
        a: 1.0,
    };
    rgba.into()
}

static TERMINAL_PALETTE: LazyLock<RwLock<TerminalPalette>> =
    LazyLock::new(|| RwLock::new(TerminalPalette::default()));
