using AgentTerm.Terminal;

internal static class KeyboardSelectionChecks
{
    internal static void Run()
    {
        using (var shortTerminal = new GhosttyTerminal(12, 6))
        {
            shortTerminal.SetBottomAnchored(true);
            shortTerminal.Write("one\r\ntwo\x1b[H"u8);
            shortTerminal.BeginKeyboardSelection();
            Equal("o", shortTerminal.ReadSelectionText(), "A short bottom-aligned viewport selects the native cursor, without its visual inset");
        }
        using (var history = new GhosttyTerminal(12, 4))
        {
            history.Write(System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", Enumerable.Range(0, 20).Select(i => $"row {i:00}"))));
            history.ScrollTo(0);
            history.BeginKeyboardSelection();
            history.AdjustSelection(TerminalSelectionAdjustment.PageDown);
            Equal("row 00\nrow 01\nrow 02\nrow 03\nr", history.ReadSelectionText(), "PageDown extends through native history rows");
            if (history.ReadPresentedViewport().Offset != 1) throw new InvalidOperationException("Keyboard selection must reveal its endpoint.");
            history.AdjustSelection(TerminalSelectionAdjustment.PageUp);
            Equal("r", history.ReadSelectionText(), "PageUp shrinks back to the original history anchor");
        }
        using var terminal = new GhosttyTerminal(12, 4);
        terminal.Write("alpha beta\r\ngamma\r\nlast\x1b[1;1H"u8);
        terminal.BeginKeyboardSelection();
        Equal("a", terminal.ReadSelectionText(), "Mark starts at the cursor");
        terminal.AdjustSelection(TerminalSelectionAdjustment.Right);
        Equal("al", terminal.ReadSelectionText(), "Right extends the logical endpoint");
        terminal.AdjustSelection(TerminalSelectionAdjustment.EndOfLine);
        Equal("alpha beta", terminal.ReadSelectionText()?.TrimEnd(), "End selects the line");
        terminal.AdjustSelection(TerminalSelectionAdjustment.BeginningOfLine);
        Equal("a", terminal.ReadSelectionText(), "Home shrinks back to its anchor");
        terminal.AdjustSelection(TerminalSelectionAdjustment.Down);
        Equal("alpha beta\ng", terminal.ReadSelectionText(), "Down uses native row selection");

        terminal.BeginSelection(3, 1);
        terminal.ExtendSelection(3, 1);
        terminal.EndSelection();
        terminal.AdjustSelection(TerminalSelectionAdjustment.Left);
        Equal("mm", terminal.ReadSelectionText(), "Reverse selection moves its logical end");
        terminal.Write("\x1b[4;1Houtput\r\nmore"u8);
        terminal.Resize(10, 4);
        terminal.AdjustSelection(TerminalSelectionAdjustment.Right);
        if (!terminal.HasSelection()) throw new InvalidOperationException("Tracked native selection must survive output and resize.");

        terminal.ClearSelection();
        terminal.Write("\x1b[2J\x1b[H界éz"u8);
        terminal.ScrollToBottom();
        terminal.BeginSelection(0, 0);
        terminal.ExtendSelection(0, 0);
        terminal.EndSelection();
        terminal.AdjustSelection(TerminalSelectionAdjustment.Right);
        Equal("界é", terminal.ReadSelectionText(), "Native adjustment skips a wide-cell tail and retains combining text");
        terminal.Write("\x1b[?1049h\x1b[Hnew\x1b[H"u8);
        terminal.AdjustSelection(TerminalSelectionAdjustment.Right);
        Equal("ne", terminal.ReadSelectionText(), "A screen switch starts a fresh native mark");
    }

    private static void Equal(string expected, string? actual, string label)
    {
        if (expected != actual) throw new InvalidOperationException($"{label}. Expected <{expected}>, got <{actual}>.");
    }
}
