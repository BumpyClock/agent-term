using System.Text;
using AgentTerm.Terminal;

internal static class ThemeChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Run()
    {
        using var terminal = new GhosttyTerminal(24, 3);
        terminal.SetLightTheme(false);
        terminal.Write("D\x1b[31;44mI\x1b[0;38;2;10;20;30;48;2;40;50;60mR\x1b[0;38;5;196mE\x1b[0m"u8);
        var dark = terminal.ReadFrame();
        terminal.SetLightTheme(true);
        var light = terminal.ReadFrame();
        Check(light.Foreground == new TerminalColor(0x24, 0x24, 0x24) && light.Background == new TerminalColor(0xfa, 0xfa, 0xfa),
            "Light mode must expose dark default text on a near-white terminal background.");
        Check(light.Cells[0].Foreground == light.Foreground && light.Cells[0].Background == light.Background,
            "Changing theme must recolor existing default cells.");
        Check(light.Cells[1].Foreground != dark.Cells[1].Foreground && light.Cells[1].Background != dark.Cells[1].Background,
            "Changing theme must recolor existing indexed foreground and background cells.");
        Check(light.Cursor.Color == light.Foreground && light.Cursor.Color != dark.Cursor.Color &&
            light.Cursor.Column == dark.Cursor.Column && light.Cursor.Row == dark.Cursor.Row,
            "Changing theme must update default cursor color without moving it.");
        Check(light.Cells[2] == dark.Cells[2] && light.Cells[2].Foreground == new TerminalColor(10, 20, 30) &&
            light.Cells[2].Background == new TerminalColor(40, 50, 60), "Application RGB colors must survive a theme change.");
        Check(light.Cells[3].Foreground == dark.Cells[3].Foreground, "Extended indexed colors must retain Ghostty defaults.");
        terminal.SetLightTheme(true);
        Check(ReferenceEquals(light, terminal.ReadFrame()), "Reapplying the same theme must preserve an unchanged frame.");
        terminal.Write("\x1b[?2026h"u8);
        terminal.SetLightTheme(false);
        var restored = terminal.ReadFrame();
        Check(restored.Foreground == dark.Foreground && restored.Background == dark.Background && restored.Cursor == dark.Cursor &&
            restored.Cells.SequenceEqual(dark.Cells), "Returning to dark mode must restore existing cells even during synchronized output.");
        terminal.Write("\x1b[?2026l"u8);

        BrandedThemeChecks.Run();
        CheckPairedSlots();
        CheckNamedThemes();
        CheckApplicationOverrides();
        CheckHistoryAndAlternateScreen();
        CheckAnsiContrast();
        CheckSchemeReports();
    }

    private static void CheckPairedSlots()
    {
        var families = TerminalThemes.All.GroupBy(theme => theme.Id[..theme.Id.LastIndexOf('-')]);
        Check(families.Count() == 7 && families.All(family => family.Count() == 2 &&
            family.Count(theme => theme.IsLight) == 1), "Every theme family must provide one light and one dark variant.");
        Check(TerminalThemes.All.Select(theme => theme.Id).Distinct().Count() == TerminalThemes.All.Count,
            "Theme IDs must be unique for persistent slot choices.");
        Check(TerminalThemes.Resolve(TerminalColorScheme.Auto, "ocean-light", "forest-dark", true).Id == "ocean-light" &&
            TerminalThemes.Resolve(TerminalColorScheme.Auto, "ocean-light", "forest-dark", false).Id == "forest-dark",
            "Auto must select independently saved light and dark themes from the system appearance.");
        Check(TerminalThemes.Resolve(TerminalColorScheme.Light, "paper-light", "midnight-dark", false).Id == "paper-light" &&
            TerminalThemes.Resolve(TerminalColorScheme.Dark, "paper-light", "midnight-dark", true).Id == "midnight-dark",
            "Forced Light and Dark must ignore the system appearance without replacing either slot.");
        Check(TerminalThemes.NormalizeId("ocean-dark", true) == "agent-light" &&
            TerminalThemes.NormalizeId("paper-light", false) == "agent-dark" &&
            TerminalThemes.NormalizeId("missing", true) == "agent-light" &&
            TerminalThemes.NormalizeId(" OCEAN-LIGHT ", true) == "ocean-light",
            "Stored slots must reject opposite appearances and unknown IDs, and normalize case and whitespace.");
        using var terminal = new GhosttyTerminal(24, 3);
        terminal.Write("default\x1b[31m red\x1b[0m\r\nhistory\r\nlast\r\n"u8);
        terminal.ScrollTo(0);
        var original = terminal.ReadFrame();
        foreach (var systemLight in new[] { true, false, true })
        {
            var theme = TerminalThemes.Resolve(TerminalColorScheme.Auto, "ocean-light", "forest-dark", systemLight);
            terminal.SetTheme(theme);
            var frame = terminal.ReadFrame();
            Check(frame.Cells.Select(cell => cell.Text).SequenceEqual(original.Cells.Select(cell => cell.Text)) &&
                frame.Viewport == original.Viewport && frame.Background == theme.Background,
                "System appearance transitions must switch the saved slot without changing retained content or viewport.");
            terminal.Write("\x1b[?996n"u8);
            Check(Encoding.UTF8.GetString(terminal.TakeResponses().Single()) ==
                (systemLight ? "\x1b[?997;2n" : "\x1b[?997;1n"), "Scheme reports must follow the active slot.");
        }
        foreach (var theme in TerminalThemes.All.Where(theme => theme.IsLight &&
            theme.Id is not ("flexoki-light" or "catppuccin-latte")))
        for (var index = 0; index < 16; index++)
            Check((Luminance(theme.Background) + 0.05) / (Luminance(theme.Ansi[index]) + 0.05) >= 4.5,
                $"{theme.Name} ANSI {index} must remain readable on its light background.");
    }

    private static void CheckNamedThemes()
    {
        using var terminal = new GhosttyTerminal(32, 2);
        terminal.SetTheme(TerminalThemes.Resolve("midnight-dark", false));
        terminal.Write("D\x1b[31;44mI\x1b[0;38;2;10;20;30mR\x1b[0;38;5;196mE\x1b[0m"u8);
        var midnight = terminal.ReadFrame();
        Check(midnight.Background == new TerminalColor(0x17, 0x19, 0x23) &&
            midnight.Cells[1].Foreground == new TerminalColor(0xef, 0x87, 0x91),
            "Midnight must supply its background and ANSI red to Ghostty.");
        terminal.BeginSelection(0, 0);
        terminal.ExtendSelection(1, 0);
        terminal.EndSelection();
        terminal.SetTheme(TerminalThemes.Resolve("paper-light", true));
        var paper = terminal.ReadFrame();
        Check(paper.Background == new TerminalColor(0xf5, 0xf0, 0xe6) &&
            paper.Foreground == new TerminalColor(0x34, 0x30, 0x2a) &&
            paper.Cells[1].Foreground == new TerminalColor(0x9e, 0x34, 0x34) &&
            paper.Cells[1].Background == new TerminalColor(0x28, 0x5c, 0x86),
            "A named theme switch must recolor default and indexed cells without new output.");
        Check(paper.HasSelection && terminal.ReadSelectionText() == "DI" &&
            paper.SelectionBackground == new TerminalColor(0xd9, 0xc9, 0xaa) &&
            paper.SelectionForeground == new TerminalColor(0x30, 0x2a, 0x22),
            "Selections must survive a named theme switch and use its selection colors.");
        Check(paper.Cells[2].Foreground == new TerminalColor(10, 20, 30) &&
            paper.Cells[3].Foreground == new TerminalColor(255, 0, 0),
            "Named themes must preserve truecolor and the xterm extended palette.");
        Check(paper.Cursor.Color == new TerminalColor(0x88, 0x53, 0x33) &&
            paper.CursorText == paper.Background, "Named themes must expose cursor and cursor-text colors.");
        terminal.Write("\x1b[?996n"u8);
        Check(Encoding.UTF8.GetString(terminal.TakeResponses().Single()) == "\x1b[?997;2n",
            "A fixed light theme must report light even when resolved against a dark system.");
        foreach (var theme in TerminalThemes.All)
        {
            terminal.SetTheme(theme);
            var frame = terminal.ReadFrame();
            Check(frame.Background == theme.Background && frame.Foreground == theme.Foreground,
                $"{theme.Name} defaults must reach the native frame.");
            terminal.Write("\x1b]10;?\x1b\\\x1b]11;?\x1b\\"u8);
            var replies = terminal.TakeResponses().Select(Encoding.UTF8.GetString).ToArray();
            Check(replies.Length == 2 && replies[0].Contains("]10;rgb:") && replies[1].Contains("]11;rgb:"),
                "Named themes must retain foreground and background query replies.");
        }
        terminal.Write("\x1b]10;#112233\x07\x1b]4;1;#abcdef\x07"u8);
        terminal.SetTheme(TerminalThemes.Resolve("ocean-dark", false));
        Check(terminal.ReadFrame().Foreground == new TerminalColor(0x11, 0x22, 0x33) &&
            terminal.ReadFrame().Cells[1].Foreground == new TerminalColor(0xab, 0xcd, 0xef),
            "Named theme changes must preserve OSC overrides.");
        terminal.Write("\x1b]110\x07\x1b]104;1\x07"u8);
        Check(terminal.ReadFrame().Foreground == new TerminalColor(0xd7, 0xe9, 0xef) &&
            terminal.ReadFrame().Cells[1].Foreground == new TerminalColor(0xee, 0x8c, 0x85),
            "OSC reset must reveal the latest named theme defaults.");
    }

    private static void CheckApplicationOverrides()
    {
        using var terminal = new GhosttyTerminal(12, 2);
        terminal.SetLightTheme(false);
        terminal.Write("\x1b]10;rgb:11/22/33\x1b\\\x1b]11;rgb:44/55/66\x1b\\\x1b]12;rgb:77/88/99\x1b\\\x1b]4;1;rgb:ab/cd/ef\x1b\\D\x1b[31mI"u8);
        var before = terminal.ReadFrame();
        terminal.SetLightTheme(true);
        var after = terminal.ReadFrame();
        Check(after.Foreground == before.Foreground && after.Background == before.Background && after.Cursor == before.Cursor &&
            after.Cells.SequenceEqual(before.Cells), "OSC default, cursor, and palette overrides must survive host theme changes.");
        terminal.Write("\x1b]110\x1b\\\x1b]111\x1b\\\x1b]112\x1b\\\x1b]104;1\x1b\\"u8);
        var reset = terminal.ReadFrame();
        Check(reset.Foreground == new TerminalColor(0x24, 0x24, 0x24) && reset.Background == new TerminalColor(0xfa, 0xfa, 0xfa) &&
            reset.Cursor.Color == reset.Foreground && reset.Cells[1].Foreground == new TerminalColor(0xa4, 0, 0),
            "Resetting application overrides must reveal the current host theme defaults.");
    }

    private static void CheckHistoryAndAlternateScreen()
    {
        using var terminal = new GhosttyTerminal(8, 2);
        terminal.SetLightTheme(false);
        terminal.Write("D\x1b[31mI\x1b[0m\r\nsecond\r\nthird"u8);
        terminal.ScrollTo(0);
        terminal.BeginSelection(0, 0);
        terminal.ExtendSelection(1, 0);
        terminal.EndSelection();
        var before = terminal.ReadFrame();
        terminal.SetLightTheme(true);
        var light = terminal.ReadFrame();
        Check(light.Viewport == before.Viewport && light.HasSelection && terminal.ReadSelectionText() == "DI" &&
            light.Cells[0].Foreground == light.Foreground && light.Cells[1].Foreground != before.Cells[1].Foreground,
            "Theme changes must recolor retained history while preserving scroll position and selection.");
        terminal.Write("\x1b[?1049h\x1b[Halt"u8);
        terminal.SetLightTheme(false);
        Check(terminal.ReadFrame().Cells[0].Foreground == new TerminalColor(255, 255, 255), "The alternate screen must use the current theme.");
        terminal.Write("\x1b[?1049l"u8);
        terminal.ScrollTo(0);
        var restored = terminal.ReadFrame();
        Check(restored.Cells[0].Text == "D" && restored.Cells[1].Text == "I" &&
            restored.Cells[1].Foreground == before.Cells[1].Foreground, "The primary history must retain content and restore its dark palette.");
    }

    private static void CheckAnsiContrast()
    {
        using var terminal = new GhosttyTerminal(20, 2);
        terminal.SetLightTheme(true);
        for (var index = 0; index < 16; index++)
            terminal.Write(Encoding.UTF8.GetBytes($"\x1b[38;5;{index}mX"));
        var frame = terminal.ReadFrame();
        for (var index = 0; index < 16; index++)
            Check((Luminance(frame.Background) + 0.05) / (Luminance(frame.Cells[index].Foreground) + 0.05) >= 4.5,
                $"Light ANSI color {index} must meet 4.5:1 contrast for terminal text, including warning yellow.");
    }

    private static double Luminance(TerminalColor color)
    {
        static double Linear(byte component)
        {
            var value = component / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static void CheckSchemeReports()
    {
        using var terminal = new GhosttyTerminal();
        terminal.SetLightTheme(true);
        terminal.Write("\x1b[?996n"u8);
        Check(Encoding.UTF8.GetString(terminal.TakeResponses().Single()) == "\x1b[?997;2n", "Color scheme queries must report light mode.");
        terminal.SetLightTheme(false);
        terminal.Write("\x1b[?996n"u8);
        Check(Encoding.UTF8.GetString(terminal.TakeResponses().Single()) == "\x1b[?997;1n", "Color scheme queries must report dark mode.");
    }
}
