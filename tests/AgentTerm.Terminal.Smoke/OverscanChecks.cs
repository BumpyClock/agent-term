using AgentTerm.Terminal;

internal static class OverscanChecks
{
    public static void Run()
    {
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
}
