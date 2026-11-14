using System.Text;
using AgentTerm.Terminal;

internal static class RowReuseChecks
{
    internal static void Run()
    {
        const int columns = 24, rows = 6;
        using var terminal = new GhosttyTerminal(columns, rows);
        var history = new List<Action<GhosttyTerminal>>();
        var retained = new List<(TerminalFrame Frame, TerminalCell[] Cells)>();
        Apply(t => t.Write(Encoding.UTF8.GetBytes("\x1b[?25l" + string.Concat(Enumerable.Range(0, rows)
            .Select(row => $"\x1b[{row + 1};1H\x1b[0;38;5;{row + 1}mrow-{row} 界e\u0301 ┼█\x1b[0m")))), "initial");
        var original = retained[^1].Frame;
        var up = Apply(t => t.Write("\x1b[S"u8), "up one");
        for (var row = 0; row < rows - 1; row++)
        {
            Check(original.Cells.SharesRowWith(up.Cells, row + 1, row), "Upward scroll must share each unchanged shifted row.");
            Check(up.Cells[row * columns + 6].Width == 2 && up.Cells[row * columns + 7].Width == 0,
                "Shifted CJK cells must retain their head and continuation.");
        }
        var restyled = Apply(t => t.Write("\x1b[S\x1b[1;1H\x1b[38;2;17;34;51mr\x1b[0m"u8), "style-only edit on moved row");
        Check(up.Cells.Skip(columns).Take(columns).Select(cell => cell.Text)
            .SequenceEqual(restyled.Cells.Take(columns).Select(cell => cell.Text)),
            "Restyling a moved row must preserve its text.");
        Check(restyled.Cells[0].Foreground == new TerminalColor(17, 34, 51) &&
            !up.Cells.SharesRowWith(restyled.Cells, 1, 0),
            "A style-only edit must replace a moved row even when its native identity and text match.");
        var edited = Apply(t => t.Write("\x1b[2;1HEDIT"u8), "stable identity edit");
        Check(edited.Cells[columns].Text == "E" && !restyled.Cells.SharesRowWith(edited.Cells, 1),
            "Editing a row with a stable native identity must replace its cells.");
        Apply(t => t.Write(Encoding.UTF8.GetBytes("\x1b[3;7H語\x1b[1;1H\x1b[1;3;4:3;9;53;5;58;2;20;30;40mZ\x1b[0m")), "wide and style edit");
        var beforeMulti = retained[^1].Frame;
        var multi = Apply(t => t.Write("\x1b[2S"u8), "up two");
        for (var row = 0; row < rows - 2; row++)
            Check(beforeMulti.Cells.SharesRowWith(multi.Cells, row + 2, row), "Multirow upward scroll must share surviving rows.");
        var down = Apply(t => t.Write("\x1b[T\x1b[1;1Hnew"u8), "down fallback");
        Check(down.Cells[0].Text == "n" && down.Cells.Skip(columns).Take(columns).SequenceEqual(multi.Cells.Take(columns)),
            "Downward scroll must capture the inserted row and shifted content even when IDs change.");
        for (var index = 0; index < 8; index++)
        {
            var write = index % 2 == 0 ? "\x1b[S\x1b[6;1Hbottom"u8.ToArray() : "\x1b[T\x1b[1;1Htop"u8.ToArray();
            Apply(t => t.Write(write), $"alternating {index}");
        }
        Apply(t => t.Write("\x1b[2;5r\x1b[2S\x1b[r"u8), "partial scroll region");
        Apply(t => t.Write("\x1b[2;1Hleft\x1b[5;1Hright"u8), "partial multirow edit");
        Apply(t => { t.BeginSelection(0, 1); t.ExtendSelection(8, 2); t.EndSelection(); }, "selection");
        Apply(t => t.Write("\x1b[S"u8), "selected scroll");
        Apply(t => t.ClearSelection(), "clear selection");
        Apply(t => t.Write("\x1b]4;2;rgb:12/34/56\x1b\\\x1b[1;1H\x1b[38;5;2;48;5;2mcolor\x1b[0m"u8), "palette content");
        Apply(t => t.Write("\x1b[S\x1b]4;2;rgb:ab/cd/ef\x1b\\"u8), "palette plus scroll");
        Apply(t => t.Write("\x1b[?5h\x1b[S"u8), "reverse scroll");
        Apply(t => t.Write("\x1b[?5l\x1b[?1049h\x1b[Halt\x1b[2;1Hscreen\x1b[S"u8), "alternate scroll");
        Apply(t => t.Write("\x1b[T\x1b[1;1Hinsert"u8), "alternate down");
        Apply(t => t.Write("\x1b[?1049l\x1b[S"u8), "primary return");
        Apply(t => t.Resize(28, 8), "resize larger");
        Apply(t => t.Write("\x1b[8;1H\r\noutput"u8), "output scroll after resize");
        Apply(t => t.Resize(columns, rows), "resize original");
        Apply(t => t.Write("\x1b[2;3Hfinal"u8), "edit after resize");
        Console.WriteLine("Scroll row reuse and retained-frame checks passed.");

        TerminalFrame Apply(Action<GhosttyTerminal> action, string name)
        {
            history.Add(action);
            action(terminal);
            var frame = terminal.ReadFrame();
            using var fresh = new GhosttyTerminal(columns, rows);
            foreach (var mutation in history) mutation(fresh);
            var expected = fresh.ReadFrame();
            Check(frame.Columns == expected.Columns && frame.Rows == expected.Rows && frame.Cells.SequenceEqual(expected.Cells) &&
                frame.Cursor == expected.Cursor && frame.Foreground == expected.Foreground && frame.Background == expected.Background &&
                frame.Viewport == expected.Viewport && frame.HasSelection == expected.HasSelection, $"Incremental capture differs from a fresh capture after {name}.");
            foreach (var snapshot in retained)
                Check(snapshot.Frame.Cells.SequenceEqual(snapshot.Cells), $"A retained frame changed after {name}.");
            retained.Add((frame, frame.Cells.ToArray()));
            return frame;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
