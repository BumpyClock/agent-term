using System.Globalization;
using System.Text;
using AgentTerm.Terminal;

/// <summary>Pins branded palettes to verbatim upstream Ghostty theme files listed in docs/theme-sources.md.</summary>
internal static class BrandedThemeChecks
{
    // ghostty-org/ghostty built-in "Flexoki Light" (mbadolato/iTerm2-Color-Schemes@5528a1a, ghostty/Flexoki Light).
    private const string FlexokiLight = """
        palette = 0=#100f0f
        palette = 1=#af3029
        palette = 2=#66800b
        palette = 3=#ad8301
        palette = 4=#205ea6
        palette = 5=#a02f6f
        palette = 6=#24837b
        palette = 7=#6f6e69
        palette = 8=#b7b5ac
        palette = 9=#d14d41
        palette = 10=#879a39
        palette = 11=#d0a215
        palette = 12=#4385be
        palette = 13=#ce5d97
        palette = 14=#3aa99f
        palette = 15=#cecdc3
        background = #fffcf0
        foreground = #100f0f
        cursor-color = #100f0f
        cursor-text = #fffcf0
        selection-background = #cecdc3
        selection-foreground = #100f0f
        """;

    // ghostty-org/ghostty built-in "Flexoki Dark" (mbadolato/iTerm2-Color-Schemes@5528a1a, ghostty/Flexoki Dark).
    private const string FlexokiDark = """
        palette = 0=#100f0f
        palette = 1=#d14d41
        palette = 2=#879a39
        palette = 3=#d0a215
        palette = 4=#4385be
        palette = 5=#ce5d97
        palette = 6=#3aa99f
        palette = 7=#878580
        palette = 8=#575653
        palette = 9=#af3029
        palette = 10=#66800b
        palette = 11=#ad8301
        palette = 12=#205ea6
        palette = 13=#a02f6f
        palette = 14=#24837b
        palette = 15=#cecdc3
        background = #100f0f
        foreground = #cecdc3
        cursor-color = #cecdc3
        cursor-text = #100f0f
        selection-background = #403e3c
        selection-foreground = #cecdc3
        """;

    // catppuccin/ghostty@b0b03cc themes/catppuccin-latte.conf (split-divider-color omitted, not a terminal color).
    private const string CatppuccinLatte = """
        palette = 0=#5c5f77
        palette = 1=#d20f39
        palette = 2=#40a02b
        palette = 3=#df8e1d
        palette = 4=#1e66f5
        palette = 5=#ea76cb
        palette = 6=#179299
        palette = 7=#acb0be
        palette = 8=#6c6f85
        palette = 9=#d20f39
        palette = 10=#40a02b
        palette = 11=#df8e1d
        palette = 12=#1e66f5
        palette = 13=#ea76cb
        palette = 14=#179299
        palette = 15=#bcc0cc
        background = eff1f5
        foreground = 4c4f69
        cursor-color = dc8a78
        cursor-text = eff1f5
        selection-background = d8dae1
        selection-foreground = 4c4f69
        """;

    // catppuccin/ghostty@b0b03cc themes/catppuccin-mocha.conf (split-divider-color omitted, not a terminal color).
    private const string CatppuccinMocha = """
        palette = 0=#45475a
        palette = 1=#f38ba8
        palette = 2=#a6e3a1
        palette = 3=#f9e2af
        palette = 4=#89b4fa
        palette = 5=#f5c2e7
        palette = 6=#94e2d5
        palette = 7=#a6adc8
        palette = 8=#585b70
        palette = 9=#f38ba8
        palette = 10=#a6e3a1
        palette = 11=#f9e2af
        palette = 12=#89b4fa
        palette = 13=#f5c2e7
        palette = 14=#94e2d5
        palette = 15=#bac2de
        background = 1e1e2e
        foreground = cdd6f4
        cursor-color = f5e0dc
        cursor-text = 11111b
        selection-background = 353749
        selection-foreground = cdd6f4
        """;

    private static readonly (string Id, bool Light, string Name, string Source)[] Branded =
    [
        ("flexoki-light", true, "Flexoki Light", FlexokiLight),
        ("flexoki-dark", false, "Flexoki Dark", FlexokiDark),
        ("catppuccin-latte", true, "Catppuccin Latte", CatppuccinLatte),
        ("catppuccin-mocha", false, "Catppuccin Mocha", CatppuccinMocha),
    ];

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Run()
    {
        foreach (var (id, light, name, source) in Branded)
        {
            var upstream = Parse(source);
            CheckCatalog(id, light, name, upstream);
            CheckNative(id, light, upstream);
        }
        CheckSlots();
        Check(TerminalThemes.Resolve("catppuccin-mocha", false).CursorText != TerminalThemes.Resolve("catppuccin-mocha", false).Background,
            "Catppuccin Mocha must keep its distinct upstream cursor-text instead of the background fallback.");
    }

    private static void CheckCatalog(string id, bool light, string name, Upstream upstream)
    {
        var theme = TerminalThemes.All.SingleOrDefault(theme => theme.Id == id);
        Check(theme is not null, $"{id} must be in the theme catalog.");
        Check(theme!.Name == name && theme.IsLight == light, $"{id} must keep its upstream name and appearance slot.");
        Check(theme.Background == upstream.Background && theme.Foreground == upstream.Foreground &&
            theme.Cursor == upstream.Cursor && (theme.CursorText ?? theme.Background) == upstream.CursorText &&
            theme.SelectionBackground == upstream.SelectionBackground && theme.SelectionForeground == upstream.SelectionForeground,
            $"{name} defaults, cursor, and selection colors must match the upstream file exactly.");
        Check(theme.Ansi.SequenceEqual(upstream.Palette), $"{name} ANSI 0-15 must match the upstream palette exactly.");
    }

    private static void CheckNative(string id, bool light, Upstream upstream)
    {
        var theme = TerminalThemes.Resolve(id, light);
        using var terminal = new GhosttyTerminal(24, 3);
        terminal.SetTheme(theme);
        var builder = new StringBuilder("D");
        for (var index = 0; index < 16; index++) builder.Append($"\x1b[38;5;{index}mF");
        builder.Append("\x1b[0m\r\n");
        for (var index = 0; index < 16; index++) builder.Append($"\x1b[48;5;{index}m ");
        builder.Append("\x1b[0;38;2;10;20;30;48;2;40;50;60mR\x1b[0;38;5;196mE\x1b[0m");
        terminal.Write(Encoding.UTF8.GetBytes(builder.ToString()));
        var frame = terminal.ReadFrame();
        var columns = frame.Columns;

        Check(frame.Foreground == upstream.Foreground && frame.Background == upstream.Background &&
            frame.Cells[0].Foreground == upstream.Foreground && frame.Cells[0].Background == upstream.Background,
            $"{theme.Name} default foreground and background must reach Ghostty.");
        for (var index = 0; index < 16; index++)
        {
            Check(frame.Cells[1 + index].Foreground == upstream.Palette[index], $"{theme.Name} SGR 38;5;{index} must render the upstream color.");
            Check(frame.Cells[columns + index].Background == upstream.Palette[index], $"{theme.Name} SGR 48;5;{index} must render the upstream color.");
        }
        Check(frame.Cells[columns + 16].Foreground == new TerminalColor(10, 20, 30) &&
            frame.Cells[columns + 16].Background == new TerminalColor(40, 50, 60),
            $"{theme.Name} must not alter application truecolor.");
        Check(frame.Cells[columns + 17].Foreground == new TerminalColor(255, 0, 0),
            $"{theme.Name} must keep the xterm extended palette outside ANSI 0-15.");
        Check(frame.Cursor.Color == upstream.Cursor && frame.CursorText == upstream.CursorText,
            $"{theme.Name} cursor and cursor-text colors must match upstream.");

        terminal.Write("\x1b]10;?\x1b\\\x1b]11;?\x1b\\"u8);
        var replies = terminal.TakeResponses().Select(Encoding.UTF8.GetString).ToArray();
        Check(replies.Length == 2 && replies[0].Contains($"]10;{OscRgb(upstream.Foreground)}") &&
            replies[1].Contains($"]11;{OscRgb(upstream.Background)}"),
            $"{theme.Name} OSC 10/11 queries must report the upstream defaults.");
        terminal.Write("\x1b[?996n"u8);
        Check(Encoding.UTF8.GetString(terminal.TakeResponses().Single()) == (light ? "\x1b[?997;2n" : "\x1b[?997;1n"),
            $"{theme.Name} must report its appearance to applications.");

        terminal.BeginSelection(0, 0);
        terminal.ExtendSelection(3, 0);
        terminal.EndSelection();
        var selected = terminal.ReadFrame();
        Check(selected.HasSelection && selected.Cells[0].Selected && selected.Cells[3].Selected && !selected.Cells[5].Selected &&
            selected.SelectionBackground == upstream.SelectionBackground && selected.SelectionForeground == upstream.SelectionForeground,
            $"{theme.Name} selections must use the upstream selection colors.");
    }

    private static void CheckSlots()
    {
        Check(TerminalThemes.NormalizeId("flexoki-light", true) == "flexoki-light" &&
            TerminalThemes.NormalizeId("catppuccin-latte", true) == "catppuccin-latte" &&
            TerminalThemes.NormalizeId("flexoki-dark", false) == "flexoki-dark" &&
            TerminalThemes.NormalizeId(" Catppuccin-Mocha ", false) == "catppuccin-mocha",
            "Branded themes must be selectable in their matching appearance slots.");
        Check(TerminalThemes.NormalizeId("flexoki-light", false) == "agent-dark" &&
            TerminalThemes.NormalizeId("catppuccin-latte", false) == "agent-dark" &&
            TerminalThemes.NormalizeId("flexoki-dark", true) == "agent-light" &&
            TerminalThemes.NormalizeId("catppuccin-mocha", true) == "agent-light",
            "Branded themes must be rejected from the opposite appearance slot.");
        Check(TerminalThemes.Resolve(TerminalColorScheme.Auto, "catppuccin-latte", "flexoki-dark", true).Id == "catppuccin-latte" &&
            TerminalThemes.Resolve(TerminalColorScheme.Auto, "catppuccin-latte", "flexoki-dark", false).Id == "flexoki-dark" &&
            TerminalThemes.Resolve(TerminalColorScheme.Light, "flexoki-light", "catppuccin-mocha", false).Id == "flexoki-light" &&
            TerminalThemes.Resolve(TerminalColorScheme.Dark, "flexoki-light", "catppuccin-mocha", true).Id == "catppuccin-mocha",
            "Branded themes must mix across families through the light and dark slots.");
    }

    private static string OscRgb(TerminalColor color) => $"rgb:{color.R:x2}{color.R:x2}/{color.G:x2}{color.G:x2}/{color.B:x2}{color.B:x2}";

    private sealed record Upstream(TerminalColor Background, TerminalColor Foreground, TerminalColor Cursor, TerminalColor CursorText,
        TerminalColor SelectionBackground, TerminalColor SelectionForeground, TerminalColor[] Palette);

    private static Upstream Parse(string source)
    {
        var palette = new TerminalColor?[16];
        var keys = new Dictionary<string, TerminalColor>();
        foreach (var line in source.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (key, value) = (line[..line.IndexOf('=')].Trim(), line[(line.IndexOf('=') + 1)..].Trim());
            if (key == "palette")
            {
                var slot = int.Parse(value[..value.IndexOf('=')], CultureInfo.InvariantCulture);
                palette[slot] = Color(value[(value.IndexOf('=') + 1)..]);
            }
            else keys[key] = Color(value);
        }
        Check(palette.All(color => color is not null) && keys.Count == 6, "Upstream fixtures must define all 16 slots and 6 named colors.");
        return new(keys["background"], keys["foreground"], keys["cursor-color"], keys["cursor-text"],
            keys["selection-background"], keys["selection-foreground"], palette.Select(color => color!.Value).ToArray());
    }

    private static TerminalColor Color(string hex)
    {
        var value = uint.Parse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
}
