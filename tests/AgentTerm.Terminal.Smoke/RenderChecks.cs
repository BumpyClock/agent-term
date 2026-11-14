using System.Text;
using AgentTerm.Terminal;

internal static class RenderChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Run()
    {
        CheckSnapshotOwnership();
        CheckBackgroundPresence();
        OverscanChecks.Run();
        using var terminal = new GhosttyTerminal(12, 3);
        terminal.Write(Encoding.UTF8.GetBytes("\x1b]4;1;rgb:12/34/56\x1b\\\x1b[1;3;4;9;2;8;38;2;10;20;30;48;2;40;50;60mA\x1b[0;7;38;5;1;48;2;1;2;3mB\x1b[0m界e\u0301"));
        var frame = terminal.ReadFrame();
        var a = frame.Cells[0]; var b = frame.Cells[1];
        Check(a.Text == "A" && a.Foreground == new TerminalColor(10, 20, 30) && a.Background == new TerminalColor(40, 50, 60), "RGB style must be preserved.");
        Check(a.Bold && a.Italic && a.Underline && a.Strikethrough && a.Dim && a.Hidden, "Text attributes must be preserved.");
        Check(b.Foreground == new TerminalColor(1, 2, 3) && b.Background == new TerminalColor(0x12, 0x34, 0x56), "Inverse must swap RGB and indexed colors.");
        Check(frame.Cells[2].Text == "界" && frame.Cells[2].Width == 2 && frame.Cells[3].Width == 0, "Wide characters need a continuation cell.");
        Check(frame.Cells[4].Text == "e\u0301", "Combining characters must stay in one cell.");
        Check(ReferenceEquals(frame, terminal.ReadFrame()), "Unchanged captures should retain their identity.");
        terminal.Write("\x1b[?25l"u8);
        var hiddenCursor = terminal.ReadFrame();
        Check(!hiddenCursor.Cursor.Visible, "Cursor visibility changes must invalidate the frame.");
        terminal.Write("\x1b]4;1;rgb:78/9a/bc\x1b\\"u8);
        Check(terminal.ReadFrame().Cells[1].Background == new TerminalColor(0x78, 0x9a, 0xbc), "Palette updates must recolor existing cells.");
        terminal.Write("\x1b[?5h"u8);
        var reverse = terminal.ReadFrame();
        Check(reverse.Cells[0].Foreground == a.Background && reverse.Cells[0].Background == a.Foreground, "Reverse screen must swap cell colors.");
        terminal.Write("\x1b[?5l\x1b[2;4H\x1b[6 q\x1b[?25l\x1b]12;rgb:aa/bb/cc\x1b\\"u8);
        var cursor = terminal.ReadFrame().Cursor;
        Check(cursor.Column == 3 && cursor.Row == 1 && !cursor.Visible && cursor.Shape == TerminalCursorShape.Bar && !cursor.Blink && cursor.Color == new TerminalColor(0xaa, 0xbb, 0xcc), "Cursor state must follow terminal controls.");
        terminal.Write("\x1b[?1049h\x1b[Halt"u8);
        Check(terminal.ReadFrame().Cells[0].Text == "a", "Alternate screen must be captured.");
        terminal.Write("\x1b[?1049l"u8);
        Check(terminal.ReadFrame().Cells[0].Text == "A", "Primary screen must return after leaving alternate screen.");
        terminal.Resize(14, 4);
        var resized = terminal.ReadFrame();
        Check(resized.Columns == 14 && resized.Rows == 4 && resized.Cells.Length == 56, "Resize must update the full grid.");
        Check(frame.Columns == 12 && frame.Cells[0].Text == "A", "Older frames must remain stable.");
        terminal.Write("\x1b[?2026hchanged"u8);
        Check(ReferenceEquals(resized, terminal.ReadFrame()), "Synchronized output must hold the previous frame.");
        Check(terminal.PendingFrameRefreshDelay is { TotalMilliseconds: > 0 and <= 1000 },
            "A held frame must expose its pending refresh delay to demand-driven callers.");
        Thread.Sleep(1100);
        var timedOut = terminal.ReadFrame();
        Check(!ReferenceEquals(resized, timedOut), "A stalled synchronized update must refresh after the hold timeout.");
        Check(terminal.PendingFrameRefreshDelay is null, "A timed-out hold must not keep scheduling recaptures.");
        terminal.Write("\x1b[?2026l!"u8);
        Check(!ReferenceEquals(timedOut, terminal.ReadFrame()), "Releasing synchronized output must capture pending changes.");
        Check(terminal.PendingFrameRefreshDelay is null, "Releasing synchronized output must clear its refresh deadline.");
    }

    private static void CheckBackgroundPresence()
    {
        using var terminal = new GhosttyTerminal(8, 2);
        var bg = terminal.ReadFrame().Background;
        terminal.Write(Encoding.ASCII.GetBytes($"\x1b[?25l\x1b[0m \x1b[48;2;{bg.R};{bg.G};{bg.B}m \x1b[0;7m \x1b[0;7;38;2;{bg.R};{bg.G};{bg.B}m "));
        var normal = terminal.ReadFrame();
        Check(!normal.Cells[0].HasBackground && normal.Cells[1].HasBackground && normal.Cells[1].Background == bg,
            "Explicit default-colored backgrounds must remain distinguishable from the default background.");
        Check(normal.Cells[2].HasBackground && normal.Cells[3].HasBackground && normal.Cells[3].Background == bg,
            "Inverse cells must cover the background even when their effective color equals the default.");
        terminal.Write("\x1b[?5h"u8);
        var reverse = terminal.ReadFrame();
        Check(!reverse.Cells[0].HasBackground && reverse.Cells[1].HasBackground == false &&
            reverse.Cells[2].HasBackground && reverse.Cells[3].HasBackground,
            "Reverse-screen mode must follow the effective color source while preserving inverse backgrounds.");
    }

    private static void CheckSnapshotOwnership()
    {
        using var terminal = new GhosttyTerminal(12, 4);
        terminal.SetBottomAnchored(true);
        terminal.Write("\x1b[?1049h\x1b[1;1Hfirst\x1b[2;1Hsecond\x1b[3;1Hthird\x1b[4;1Hfourth"u8);
        var original = terminal.ReadFrame();
        var originalCells = original.Cells.ToArray();
        terminal.Write("\x1b[3;1HTHIRD"u8);
        var edited = terminal.ReadFrame();
        Check(string.Concat(edited.Cells.Skip(24).Take(5).Select(cell => cell.Text)) == "THIRD", "A dirty row must expose its new text.");
        Check(original.Cells.SequenceEqual(originalCells), "A later dirty-row capture must not mutate an older snapshot.");
        Check(original.Cells.SharesRowWith(edited.Cells, 0) && !original.Cells.SharesRowWith(edited.Cells, 2),
            "A sparse capture must retain unchanged rows and replace the dirty row.");
        for (var index = 0; index < original.Cells.Length; index++)
            if (index < 24 || index >= 29)
                Check(edited.Cells[index] == originalCells[index], "Editing one row must preserve all other cell values.");

        terminal.Write("\x1b[2;7H\x1b[6 q"u8);
        var moved = terminal.ReadFrame();
        Check(moved.Cursor.Column == 6 && moved.Cursor.Row == 1 && moved.Cursor.Shape == TerminalCursorShape.Bar,
            "A cursor-only update must publish the new cursor.");
        Check(ReferenceEquals(moved.Cells, edited.Cells), "A cursor-only update must keep the published cell grid.");
        terminal.Write("\x1b[3;1HTHIRD"u8);
        var rewritten = terminal.ReadFrame();
        Check(ReferenceEquals(rewritten.Cells, edited.Cells), "Rewriting identical text must keep the published cell grid.");
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            terminal.Write(i % 2 == 0 ? "\x1b[3;1HTHIRD\x1b[2;7H"u8 : "\x1b[3;1HTHIRD\x1b[1;1H"u8);
            terminal.ReadFrame();
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Console.WriteLine($"Unchanged dirty-row captures: {allocated} managed bytes over 100 captures.");
        Check(allocated < 100 * 512, "Captures whose dirty rows did not change must not allocate replacement rows.");
        terminal.Write("\x1b[1;1HFIRST"u8);
        var afterDroppedFrame = terminal.ReadFrame();
        Check(!original.Cells.SharesRowWith(afterDroppedFrame.Cells, 0) && !original.Cells.SharesRowWith(afterDroppedFrame.Cells, 2),
            "Comparing retained frames must reveal all changed rows even when an intermediate frame was dropped.");

        terminal.BeginSelection(0, 0);
        terminal.ExtendSelection(2, 0);
        terminal.EndSelection();
        var selected = terminal.ReadFrame();
        Check(selected.Cells.Take(3).All(cell => cell.Selected) && !selected.Cells[3].Selected,
            "Selection must appear when only selection metadata changes.");
        terminal.ExtendSelection(4, 0);
        var extended = terminal.ReadFrame();
        Check(extended.Cells.Take(5).All(cell => cell.Selected) && !extended.Cells[5].Selected,
            "Moving an existing selection must update its cell range.");
        Check(!selected.Cells[4].Selected, "Extending a selection must leave an older selected snapshot unchanged.");
        terminal.ClearSelection();
        Check(terminal.ReadFrame().Cells.All(cell => !cell.Selected), "Clearing selection must restore unselected cells.");
        Check(edited.Cells.SequenceEqual(moved.Cells), "Selection captures must not mutate earlier unselected snapshots.");

        terminal.Write("\x1b[?1049lprimary"u8);
        var primary = terminal.ReadFrame();
        Check(!primary.Viewport.IsAlternateScreen && string.Concat(primary.Cells.Take(7).Select(cell => cell.Text)) == "primary",
            "A screen switch must replace rows even when their coordinates match the prior screen.");
        Check(original.Cells.SequenceEqual(originalCells), "Screen switches must leave retained snapshots unchanged.");
    }
}
