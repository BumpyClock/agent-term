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
        terminal.ScrollToBottom();
        var bottom = terminal.ReadFrame();
        if (bottom.OverscanBelow.Length != 0 || frame.OverscanBelow[0].Cells[0].Text != "f")
            throw new InvalidOperationException("Bottom overscan must end at history and old frames remain stable.");
        terminal.SetOverscanEnabled(false);
        if (terminal.ReadFrame().OverscanAbove.Length != 0)
            throw new InvalidOperationException("Disabling overscan must clear captured extra rows.");
    }
}
