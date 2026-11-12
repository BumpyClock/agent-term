using System.Globalization;

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
        string cursor, string selectionBackground, string selectionForeground, string ansi)
    {
        Id = id;
        Name = name;
        IsLight = light;
        Background = Parse(background);
        Foreground = Parse(foreground);
        Cursor = Parse(cursor);
        CursorText = Background;
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

public static class TerminalThemes
{
    public static IReadOnlyList<TerminalTheme> All { get; } = Array.AsReadOnly<TerminalTheme>(
    [
        new("agent-dark", "Agent Dark", false, "000000", "ffffff", "ffffff", "2d5c96", "ffffff",
            "1d1f21 cc6666 b5bd68 f0c674 81a2be b294bb 8abeb7 c5c8c6 666666 d54e53 b9ca4a e7c547 7aa6da c397d8 70c0b1 eaeaea"),
        new("agent-light", "Agent Light", true, "fafafa", "242424", "242424", "2d5c96", "ffffff",
            "2e3436 a40000 3b6500 806000 204a87 75507b 005c64 555753 666866 c00000 467000 8f6800 3465a4 8d4684 007880 2e3436"),
        new("midnight", "Midnight", false, "171923", "e2e8f0", "b4a6ff", "45416a", "ffffff",
            "323442 ef8791 92c99a e8c582 90b7ed c4a1e8 87cbd3 d1d7e0 73798e ffa1aa aee2b5 ffdf9c aacbff d9b7ff a0e5eb f5f7ff"),
        new("ocean", "Ocean", false, "0c202b", "d7e9ef", "69d8dc", "24576c", "ffffff",
            "223d49 ee8c85 92c9a0 e1c48a 83b7e8 b8a4df 70c6cc c5dbe3 698995 ffa59e abe3b7 f5dfa5 9ed1ff d1beff 90e3e6 effaff"),
        new("forest", "Forest", false, "14221c", "e0e8da", "bed595", "3a5a46", "ffffff",
            "304338 e38f88 a6c78b e1c68b 91b6d0 bea2cc 88c3b3 cdd8c4 788d7b f7a69e c2e1a6 f5dfa4 aacfe8 d8bde6 a3ddce f3f7ea"),
        new("paper", "Paper", true, "f5f0e6", "34302a", "885333", "d9c9aa", "302a22",
            "34302a 9e3434 436029 785800 285c86 79508a 006365 655d50 756b5c b02222 346400 825600 1b60a0 88419c 006a70 34302a"),
    ]);

    /// <summary>Unknown stored IDs fall back to Automatic, preserving settings from older builds.</summary>
    public static string NormalizeId(string? id) =>
        All.FirstOrDefault(theme => string.Equals(theme.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase))?.Id ?? "";

    public static TerminalTheme Resolve(string? id, bool light) =>
        All.FirstOrDefault(theme => string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[light ? 1 : 0];
}
