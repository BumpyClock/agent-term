use gpui::{Hsla, Rgba};
use gpui_component::theme::ThemeMode;
use gpui_term::TerminalPalette;

#[derive(Clone, Copy)]
pub struct TerminalSchemeOption {
    pub id: &'static str,
    pub name: &'static str,
    pub has_light_variant: bool,
}

#[derive(Clone, Copy)]
struct TerminalPaletteSpec {
    background: &'static str,
    foreground: &'static str,
    cursor: &'static str,
    selection: &'static str,
    black: &'static str,
    red: &'static str,
    green: &'static str,
    yellow: &'static str,
    blue: &'static str,
    magenta: &'static str,
    cyan: &'static str,
    white: &'static str,
    bright_black: &'static str,
    bright_red: &'static str,
    bright_green: &'static str,
    bright_yellow: &'static str,
    bright_blue: &'static str,
    bright_magenta: &'static str,
    bright_cyan: &'static str,
    bright_white: &'static str,
}

#[derive(Clone, Copy)]
struct TerminalScheme {
    id: &'static str,
    name: &'static str,
    light: Option<TerminalPaletteSpec>,
    dark: TerminalPaletteSpec,
}

pub fn terminal_scheme_options() -> Vec<TerminalSchemeOption> {
    schemes()
        .iter()
        .map(|scheme| TerminalSchemeOption {
            id: scheme.id,
            name: scheme.name,
            has_light_variant: scheme.light.is_some(),
        })
        .collect()
}

pub fn terminal_palette_for_scheme(id: &str, mode: ThemeMode) -> TerminalPalette {
    let scheme = schemes()
        .iter()
        .find(|scheme| scheme.id == id)
        .unwrap_or(&schemes()[0]);

    let palette = if mode.is_dark() || scheme.light.is_none() {
        scheme.dark
    } else {
        scheme.light.unwrap()
    };

    palette_spec_to_palette(palette)
}

pub fn terminal_preview_colors(id: &str, mode: ThemeMode) -> Vec<Hsla> {
    let palette = terminal_palette_for_scheme(id, mode);
    vec![
        palette.black,
        palette.red,
        palette.green,
        palette.yellow,
        palette.blue,
        palette.magenta,
        palette.cyan,
        palette.white,
    ]
}

fn palette_spec_to_palette(spec: TerminalPaletteSpec) -> TerminalPalette {
    TerminalPalette::from_base(
        parse_hex(spec.foreground),
        parse_hex(spec.background),
        parse_hex(spec.cursor),
        parse_hex(spec.selection),
        parse_hex(spec.black),
        parse_hex(spec.red),
        parse_hex(spec.green),
        parse_hex(spec.yellow),
        parse_hex(spec.blue),
        parse_hex(spec.magenta),
        parse_hex(spec.cyan),
        parse_hex(spec.white),
        parse_hex(spec.bright_black),
        parse_hex(spec.bright_red),
        parse_hex(spec.bright_green),
        parse_hex(spec.bright_yellow),
        parse_hex(spec.bright_blue),
        parse_hex(spec.bright_magenta),
        parse_hex(spec.bright_cyan),
        parse_hex(spec.bright_white),
    )
}

fn parse_hex(color: &str) -> Hsla {
    let rgba = Rgba::try_from(color).unwrap_or_else(|_| Rgba {
        r: 0.0,
        g: 0.0,
        b: 0.0,
        a: 1.0,
    });
    rgba.into()
}

fn schemes() -> &'static [TerminalScheme] {
    const TRANSPARENT: &str = "#00000000";

    const ONE_LIGHT: TerminalPaletteSpec = TerminalPaletteSpec {
        background: TRANSPARENT,
        foreground: "#383A42",
        cursor: "#383A42",
        selection: "#E5E5E6",
        black: "#383A42",
        red: "#E45649",
        green: "#50A14F",
        yellow: "#986801",
        blue: "#4078F2",
        magenta: "#A626A4",
        cyan: "#0184BC",
        white: "#A0A1A7",
        bright_black: "#4F525F",
        bright_red: "#E45649",
        bright_green: "#50A14F",
        bright_yellow: "#986801",
        bright_blue: "#4078F2",
        bright_magenta: "#A626A4",
        bright_cyan: "#0184BC",
        bright_white: "#FAFAFA",
    };

    const ONE_DARK: TerminalPaletteSpec = TerminalPaletteSpec {
        background: TRANSPARENT,
        foreground: "#ABB2BF",
        cursor: "#ABB2BF",
        selection: "#3E4452",
        black: "#282C34",
        red: "#E06C75",
        green: "#98C379",
        yellow: "#E5C07B",
        blue: "#61AFEF",
        magenta: "#C678DD",
        cyan: "#56B6C2",
        white: "#5C6370",
        bright_black: "#3E4452",
        bright_red: "#E06C75",
        bright_green: "#98C379",
        bright_yellow: "#E5C07B",
        bright_blue: "#61AFEF",
        bright_magenta: "#C678DD",
        bright_cyan: "#56B6C2",
        bright_white: "#ABB2BF",
    };

    const NORD_DARK: TerminalPaletteSpec = TerminalPaletteSpec {
        background: TRANSPARENT,
        foreground: "#D8DEE9",
        cursor: "#D8DEE9",
        selection: "#434C5E",
        black: "#3B4252",
        red: "#BF616A",
        green: "#A3BE8C",
        yellow: "#EBCB8B",
        blue: "#81A1C1",
        magenta: "#B48EAD",
        cyan: "#88C0D0",
        white: "#E5E9F0",
        bright_black: "#4C566A",
        bright_red: "#BF616A",
        bright_green: "#A3BE8C",
        bright_yellow: "#EBCB8B",
        bright_blue: "#81A1C1",
        bright_magenta: "#B48EAD",
        bright_cyan: "#8FBCBB",
        bright_white: "#ECEFF4",
    };

    const FLEXOKI_LIGHT: TerminalPaletteSpec = TerminalPaletteSpec {
        background: TRANSPARENT,
        foreground: "#100F0F",
        cursor: "#100F0F",
        selection: "#E6E4D9",
        black: "#100F0F",
        red: "#AF3029",
        green: "#66800B",
        yellow: "#AD8301",
        blue: "#205EA6",
        magenta: "#A02F6F",
        cyan: "#24837B",
        white: "#6F6E69",
        bright_black: "#575653",
        bright_red: "#AF3029",
        bright_green: "#66800B",
        bright_yellow: "#AD8301",
        bright_blue: "#205EA6",
        bright_magenta: "#A02F6F",
        bright_cyan: "#24837B",
        bright_white: "#FFFCF0",
    };

    const FLEXOKI_DARK: TerminalPaletteSpec = TerminalPaletteSpec {
        background: TRANSPARENT,
        foreground: "#CECDC3",
        cursor: "#CECDC3",
        selection: "#403E3C",
        black: "#100F0F",
        red: "#D14D41",
        green: "#879A39",
        yellow: "#D0A215",
        blue: "#4385BE",
        magenta: "#CE5D97",
        cyan: "#3AA99F",
        white: "#878580",
        bright_black: "#575653",
        bright_red: "#D14D41",
        bright_green: "#879A39",
        bright_yellow: "#D0A215",
        bright_blue: "#4385BE",
        bright_magenta: "#CE5D97",
        bright_cyan: "#3AA99F",
        bright_white: "#CECDC3",
    };

    static SCHEMES: [TerminalScheme; 3] = [
        TerminalScheme {
            id: "one",
            name: "One",
            light: Some(ONE_LIGHT),
            dark: ONE_DARK,
        },
        TerminalScheme {
            id: "nord",
            name: "Nord",
            light: None,
            dark: NORD_DARK,
        },
        TerminalScheme {
            id: "flexoki",
            name: "Flexoki",
            light: Some(FLEXOKI_LIGHT),
            dark: FLEXOKI_DARK,
        },
    ];

    &SCHEMES
}
