using System.Globalization;
using System.Text.Json.Serialization;

namespace AgentTerm.Terminal;

/// <summary>Immutable sRGB terminal defaults. Extended indices retain the standard xterm cube and ramp.</summary>
public sealed class TerminalTheme
{
    public string Id { get; }
    public string Name { get; }
    public bool IsLight { get; }
    public TerminalColor Background { get; }
    public TerminalColor Foreground { get; }
    public TerminalColor Cursor { get; }
    public TerminalColor CursorText { get; }
    public TerminalColor SelectionBackground { get; }
    public TerminalColor SelectionForeground { get; }
    public IReadOnlyList<TerminalColor> Ansi { get; }

    internal TerminalTheme(string id, string name, bool light, string background, string foreground,
        string cursor, string selectionBackground, string selectionForeground, string ansi, string? cursorText = null)
    {
        Id = id;
        Name = name;
        IsLight = light;
        Background = Parse(background);
        Foreground = Parse(foreground);
        Cursor = Parse(cursor);
        CursorText = cursorText is null ? Background : Parse(cursorText);
        SelectionBackground = Parse(selectionBackground);
        SelectionForeground = Parse(selectionForeground);
        var colors = ansi.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Parse).ToArray();
        if (colors.Length != 16) throw new ArgumentException("A terminal theme needs 16 ANSI colors.", nameof(ansi));
        Ansi = Array.AsReadOnly(colors);
    }

    private static TerminalColor Parse(string hex)
    {
        var value = uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<TerminalColorScheme>))]
public enum TerminalColorScheme { Auto, Light, Dark }

public static class TerminalThemes
{
    public static IReadOnlyList<TerminalTheme> All { get; } = Array.AsReadOnly<TerminalTheme>(
    [
        new("agent-dark", "Agent Dark", false, "000000", "ffffff", "ffffff", "2d5c96", "ffffff",
            "1d1f21 cc6666 b5bd68 f0c674 81a2be b294bb 8abeb7 c5c8c6 666666 d54e53 b9ca4a e7c547 7aa6da c397d8 70c0b1 eaeaea"),
        new("agent-light", "Agent Light", true, "fafafa", "242424", "242424", "2d5c96", "ffffff",
            "2e3436 a40000 3b6500 806000 204a87 75507b 005c64 555753 666866 c00000 467000 8f6800 3465a4 8d4684 007880 2e3436"),
        new("midnight-dark", "Midnight Dark", false, "171923", "e2e8f0", "b4a6ff", "45416a", "ffffff",
            "323442 ef8791 92c99a e8c582 90b7ed c4a1e8 87cbd3 d1d7e0 73798e ffa1aa aee2b5 ffdf9c aacbff d9b7ff a0e5eb f5f7ff"),
        new("ocean-dark", "Ocean Dark", false, "0c202b", "d7e9ef", "69d8dc", "24576c", "ffffff",
            "223d49 ee8c85 92c9a0 e1c48a 83b7e8 b8a4df 70c6cc c5dbe3 698995 ffa59e abe3b7 f5dfa5 9ed1ff d1beff 90e3e6 effaff"),
        new("forest-dark", "Forest Dark", false, "14221c", "e0e8da", "bed595", "3a5a46", "ffffff",
            "304338 e38f88 a6c78b e1c68b 91b6d0 bea2cc 88c3b3 cdd8c4 788d7b f7a69e c2e1a6 f5dfa4 aacfe8 d8bde6 a3ddce f3f7ea"),
        new("paper-light", "Paper Light", true, "f5f0e6", "34302a", "885333", "d9c9aa", "302a22",
            "34302a 9e3434 436029 785800 285c86 79508a 006365 655d50 756b5c b02222 346400 825600 1b60a0 88419c 006a70 34302a"),
        new("midnight-light", "Midnight Light", true, "f1f0fa", "302d44", "64549a", "d7d0eb", "302d44",
            "302d44 a42f4f 3f632e 765800 345da0 794398 00656b 625b75 716a83 b12649 376700 835600 295db3 873aa2 00656b 302d44"),
        new("ocean-light", "Ocean Light", true, "edf6f8", "243b47", "006c7a", "bddce4", "243b47",
            "243b47 a03835 3d623b 785900 245d94 754b92 00636c 556b73 586d79 b12b2d 31682f 825600 1b5ea5 80409b 006771 243b47"),
        new("forest-light", "Forest Light", true, "eff5eb", "2e3b2c", "426537", "ccdec4", "2e3b2c",
            "2e3b2c a33a32 426328 795800 315f87 79528b 00675a 5c6b52 606e56 b12d26 376927 825600 265e9c 854599 00685b 2e3b2c"),
        new("paper-dark", "Paper Dark", false, "26221c", "e8ddc9", "d5ae79", "594933", "fff4e0",
            "423a30 e59b8d b9c58f e1c18a 9cb6cf c6a3ca 92c3b5 d8cbb6 93836d f6b0a2 d0dda4 f6d9a1 b1cfe6 ddbae2 acddce f8edd8"),
        // Branded palettes are verbatim upstream values. See docs/theme-sources.md before changing them.
        new("flexoki-light", "Flexoki Light", true, "fffcf0", "100f0f", "100f0f", "cecdc3", "100f0f",
            "100f0f af3029 66800b ad8301 205ea6 a02f6f 24837b 6f6e69 b7b5ac d14d41 879a39 d0a215 4385be ce5d97 3aa99f cecdc3"),
        new("flexoki-dark", "Flexoki Dark", false, "100f0f", "cecdc3", "cecdc3", "403e3c", "cecdc3",
            "100f0f d14d41 879a39 d0a215 4385be ce5d97 3aa99f 878580 575653 af3029 66800b ad8301 205ea6 a02f6f 24837b cecdc3"),
        new("catppuccin-latte", "Catppuccin Latte", true, "eff1f5", "4c4f69", "dc8a78", "d8dae1", "4c4f69",
            "5c5f77 d20f39 40a02b df8e1d 1e66f5 ea76cb 179299 acb0be 6c6f85 d20f39 40a02b df8e1d 1e66f5 ea76cb 179299 bcc0cc"),
        new("catppuccin-mocha", "Catppuccin Mocha", false, "1e1e2e", "cdd6f4", "f5e0dc", "353749", "cdd6f4",
            "45475a f38ba8 a6e3a1 f9e2af 89b4fa f5c2e7 94e2d5 a6adc8 585b70 f38ba8 a6e3a1 f9e2af 89b4fa f5c2e7 94e2d5 bac2de",
            cursorText: "11111b"),
    ]);

    /// <summary>Validate a stored theme ID against its appearance slot.</summary>
    public static string NormalizeId(string? id, bool light)
    {
        var canonical = id?.Trim().ToLowerInvariant() switch
        {
            "midnight" => "midnight-dark",
            "ocean" => "ocean-dark",
            "forest" => "forest-dark",
            "paper" => "paper-light",
            var value => value,
        };
        return All.FirstOrDefault(theme => theme.Id == canonical && theme.IsLight == light)?.Id
            ?? (light ? "agent-light" : "agent-dark");
    }

    public static TerminalTheme Resolve(string? id, bool light) =>
        All.First(theme => theme.Id == NormalizeId(id, light));

    public static TerminalTheme Resolve(TerminalColorScheme scheme, string lightId, string darkId, bool systemLight)
    {
        var light = scheme switch
        {
            TerminalColorScheme.Light => true,
            TerminalColorScheme.Dark => false,
            _ => systemLight,
        };
        return Resolve(light ? lightId : darkId, light);
    }
}
