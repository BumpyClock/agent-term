using AgentTerm.Terminal;

internal static class OverscanChecks
{
    public static void Run()
    {
        SparseUpdates();
        CheckTransitions();
        CheckPalette();
        using var terminal = new GhosttyTerminal(8, 3);
        terminal.Write("one\r\ntwo\r\nthree\r\nfour\r\nfive"u8);
        terminal.ScrollTo(1);
        terminal.SetOverscanEnabled(true);
        var frame = terminal.ReadFrame();
        if (frame.Cells.Length != 24 || frame.OverscanAbove.Length != 1 || frame.OverscanBelow.Length != 1 ||
            frame.OverscanAbove[0].ViewportY != -1 || frame.OverscanBelow[0].ViewportY != 3 ||
            frame.OverscanAbove[0].Cells[0].Text != "o" || frame.OverscanBelow[0].Cells[0].Text != "f")
            throw new InvalidOperationException("Overscan must capture owned rows at signed viewport coordinates.");
        var begin = typeof(GhosttyTerminal).GetMethod("BeginFrame", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var end = typeof(GhosttyTerminal).GetMethod("CompleteFrame", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        terminal.ScrollTo(1);
        var capture = begin.Invoke(terminal, null);
        terminal.SetOverscanEnabled(false);
        var frozen = (TerminalFrame)end.Invoke(terminal, [capture])!;
        if (frozen.OverscanAbove.Length != 1 || frozen.OverscanBelow.Length != 1)
            throw new InvalidOperationException("Completion must use its frozen overscan request even after a later request change.");
        terminal.SetOverscanEnabled(true);
        terminal.ScrollToBottom();
        var bottom = terminal.ReadFrame();
        if (bottom.OverscanBelow.Length != 0 || frame.OverscanBelow[0].Cells[0].Text != "f")
            throw new InvalidOperationException("Bottom overscan must end at history and old frames remain stable.");
        terminal.SetOverscanEnabled(false);
        if (terminal.ReadFrame().OverscanAbove.Length != 0)
            throw new InvalidOperationException("Disabling overscan must clear captured extra rows.");
    }

    private static void SparseUpdates()
    {
        using var terminal = new GhosttyTerminal(80, 24);
        terminal.SetOverscanEnabled(true);
        terminal.Write("\x1b[?25l"u8);
        for (var i = 0; i < 30; i++) terminal.Write("line\r\n"u8);
        var frame = terminal.ReadFrame();
        for (var i = 0; i < 10; i++)
        {
            terminal.Write(i % 2 == 0 ? "\x1b[12;1HA"u8 : "\x1b[12;1HB"u8);
            frame = terminal.ReadFrame();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var shared = 0;
        for (var i = 0; i < 100; i++)
        {
            terminal.Write(i % 2 == 0 ? "\x1b[12;1HA"u8 : "\x1b[12;1HB"u8);
            var next = terminal.ReadFrame();
            if (next.Cells[11 * 80].Text != (i % 2 == 0 ? "A" : "B")) throw new InvalidOperationException("Sparse update was not captured.");
            for (var row = 0; row < 24; row++) if (frame.Cells.SharesRowWith(next.Cells, row)) shared++;
            frame = next;
        }
        Console.WriteLine($"Overscan sparse captures: {GC.GetAllocatedBytesForCurrentThread() - allocated} managed bytes, {shared} shared visible rows over 100 captures.");
        if (shared != 2300) throw new InvalidOperationException("Sparse overscan captures must reuse the 23 unchanged visible rows.");
    }

    private static void CheckTransitions()
    {
        using var terminal = new GhosttyTerminal(8, 3);
        terminal.Write("one\r\ntwo\r\nthree\r\nfour\r\nfive"u8);
        terminal.ScrollTo(1);
        terminal.SetOverscanEnabled(true);
        var initial = terminal.ReadFrame();
        terminal.Write("\x1b[3;1HFIVE"u8);
        var below = terminal.ReadFrame();
        if (below.OverscanBelow[0].Cells[0].Text != "F" || initial.OverscanBelow[0].Cells[0].Text != "f" ||
            !ReferenceEquals(initial.OverscanAbove[0].Cells, below.OverscanAbove[0].Cells) ||
            !initial.Cells.SharesRowWith(below.Cells, 0))
            throw new InvalidOperationException("A dirty below-viewport row must update without changing retained rows.");
        terminal.BeginSelection(0, 0);
        terminal.ExtendSelection(2, 0);
        terminal.EndSelection();
        var selected = terminal.ReadFrame();
        if (!selected.Cells[0].Selected || below.Cells[0].Selected) throw new InvalidOperationException("Selection must update without mutating older frames.");
        terminal.ClearSelection();
        if (terminal.ReadFrame().Cells[0].Selected) throw new InvalidOperationException("Clearing selection must update the frame.");
        terminal.ScrollTo(0);
        var top = terminal.ReadFrame();
        if (top.OverscanAbove.Length != 0 || top.OverscanBelow.Length != 1) throw new InvalidOperationException("Top coverage must be rebuilt.");
        terminal.BeginSelection(0, 0);
        terminal.ExtendSelection(1, 0);
        terminal.EndSelection();
        terminal.ScrollTo(1);
        var selectedAbove = terminal.ReadFrame();
        if (!selectedAbove.OverscanAbove[0].Cells[0].Selected) throw new InvalidOperationException("Selection must follow a row above the viewport.");
        terminal.ClearSelection();
        if (terminal.ReadFrame().OverscanAbove[0].Cells[0].Selected || !selectedAbove.OverscanAbove[0].Cells[0].Selected)
            throw new InvalidOperationException("Clearing an above-viewport selection must preserve the old frame.");
        terminal.ScrollToBottom();
        var bottom = terminal.ReadFrame();
        if (bottom.OverscanAbove.Length != 1 || bottom.OverscanBelow.Length != 0) throw new InvalidOperationException("Bottom coverage must be rebuilt.");
        terminal.Write("\x1b[?2026h\x1b[1;1Hhold"u8);
        if (!ReferenceEquals(bottom, terminal.ReadFrame())) throw new InvalidOperationException("Synchronized output must retain the overscan frame.");
        terminal.Write("\x1b[?2026l"u8);
        if (terminal.ReadFrame().Cells[0].Text != "h") throw new InvalidOperationException("Released synchronized output must appear.");
        terminal.Write("\x1b[?1049h\x1b[Halt"u8);
        var alternate = terminal.ReadFrame();
        if (!alternate.Viewport.IsAlternateScreen || alternate.Cells[0].Text != "a" || alternate.OverscanAbove.Length != 0)
            throw new InvalidOperationException("Switching screens must rebuild overscan coverage.");
        terminal.Write("\x1b[?1049l"u8);
        if (terminal.ReadFrame().Viewport.IsAlternateScreen) throw new InvalidOperationException("The primary screen must return.");
        terminal.Resize(10, 4);
        var resized = terminal.ReadFrame();
        if (resized.Columns != 10 || resized.Rows != 4 || resized.Cells.Length != 40)
            throw new InvalidOperationException("Resize must rebuild the overscan grid.");
        terminal.SetOverscanEnabled(false);
        var disabled = terminal.ReadFrame();
        if (disabled.OverscanAbove.Length != 0 || disabled.OverscanBelow.Length != 0) throw new InvalidOperationException("Disabling overscan must clear coverage.");
        terminal.SetOverscanEnabled(true);
        if (terminal.ReadFrame().OverscanAbove.Length != 1) throw new InvalidOperationException("Enabling overscan must restore coverage.");
    }

    private static void CheckPalette()
    {
        using var terminal = new GhosttyTerminal(8, 3);
        terminal.Write("\x1b[31mone\r\ntwo\r\nthree\r\nfour\r\nfive"u8);
        terminal.ScrollTo(1);
        terminal.SetOverscanEnabled(true);
        var before = terminal.ReadFrame();
        terminal.Write("\x1b]4;1;rgb:12/34/56\x1b\\"u8);
        var after = terminal.ReadFrame();
        var color = new TerminalColor(0x12, 0x34, 0x56);
        if (after.OverscanAbove[0].Cells[0].Foreground != color || after.Cells[0].Foreground != color || after.OverscanBelow[0].Cells[0].Foreground != color ||
            before.OverscanAbove[0].Cells[0].Foreground == color)
            throw new InvalidOperationException("Palette changes must recolor visible and overscan rows without changing old frames.");
    }
}
