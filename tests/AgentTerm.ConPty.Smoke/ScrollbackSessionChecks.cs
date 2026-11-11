using AgentTerm.Terminal;

internal static class ScrollbackSessionChecks
{
    public static async Task RunAsync()
    {
        await using var session = new TerminalSession(100, 10, TestShell.Profile);
        session.Start();
        await WaitAsync(session, frame => Text(frame).Contains("PS "));
        session.Send("1..100 | ForEach-Object { 'HISTORY_' + $_.ToString('D3') }\r");
        await WaitAsync(session, frame => Text(frame).Contains("HISTORY_100") && Text(frame).Contains("PS "));
        session.ScrollLines(-20);
        var history = session.ReadFrame();
        if (history.Viewport.IsAtBottom || history.Cursor.Visible)
            throw new Exception("History navigation should leave the live screen and hide its cursor.");

        var row = Enumerable.Range(0, history.Rows).First(y => Row(history, y).StartsWith("HISTORY_"));
        var expected = Row(history, row).TrimEnd();
        session.BeginSelection(0, row);
        session.ExtendSelection(expected.Length - 1, row);
        session.EndSelection();
        if (session.ReadSelectionText() != expected || !session.ReadFrame().HasSelection)
            throw new Exception("A selected history line should copy its exact text.");

        session.SendKey(0x10, TerminalModifiers.Shift, TerminalKeyAction.Press);
        session.SendKey(0x10, TerminalModifiers.None, TerminalKeyAction.Release);
        if (session.ReadFrame().Viewport.IsAtBottom || session.ReadSelectionText() != expected)
            throw new Exception("Modifier-only input must preserve the history viewport and selection.");
        session.SendKey(0x58, TerminalModifiers.None, TerminalKeyAction.Press, "x");
        if (!session.ReadFrame().Viewport.IsAtBottom || session.ReadFrame().HasSelection)
            throw new Exception("Typed input must clear selection and return to the live screen.");
        session.SendKey(0x43, TerminalModifiers.Control, TerminalKeyAction.Press);
        session.Send("exit\r");
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        await session.StopAsync();
        await session.StopAsync();
        session.ScrollLines(-20);
        var stopped = session.ReadFrame();
        if (stopped.Viewport.IsAtBottom)
            throw new Exception("Completed sessions must retain scrollable history.");
        row = Enumerable.Range(0, stopped.Rows).First(y => Row(stopped, y).StartsWith("HISTORY_"));
        expected = Row(stopped, row).TrimEnd();
        session.BeginSelection(0, row);
        session.ExtendSelection(expected.Length - 1, row);
        session.EndSelection();
        if (session.ReadSelectionText() != expected)
            throw new Exception("Completed sessions must retain selectable history.");
        session.Resize(110, 12);
        if (session.ReadFrame().Columns != 110)
            throw new Exception("Completed sessions must resize without a live ConPTY.");

        await using var stoppedSession = new TerminalSession(100, 10, TestShell.Profile);
        stoppedSession.Start();
        await WaitAsync(stoppedSession, frame => Text(frame).Contains("PS "));
        stoppedSession.Send("1..40 | ForEach-Object { 'STOP_' + $_.ToString('D3') }\r");
        await WaitAsync(stoppedSession, frame => Text(frame).Contains("STOP_040") && Text(frame).Contains("PS "));
        using var process = System.Diagnostics.Process.GetProcessById(stoppedSession.ProcessId);
        await stoppedSession.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        if (!process.HasExited) throw new Exception("Stop must release the owned shell process.");
        stoppedSession.ScrollLines(-10);
        if (stoppedSession.ReadFrame().Viewport.IsAtBottom || !Text(stoppedSession.ReadFrame()).Contains("STOP_"))
            throw new Exception("Stopping a running session must retain its scrollback.");
        Console.WriteLine("Scrollback session checks passed navigation, selection, input snap, and retained history after exit.");
    }

    private static string Row(TerminalFrame frame, int row) =>
        string.Concat(frame.Cells.Skip(row * frame.Columns).Take(frame.Columns).Select(cell => cell.Text));

    private static string Text(TerminalFrame frame) =>
        string.Join("\n", Enumerable.Range(0, frame.Rows).Select(row => Row(frame, row)));

    private static async Task WaitAsync(TerminalSession session, Func<TerminalFrame, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate(session.ReadFrame())) return;
            await Task.Delay(30);
        }
        throw new TimeoutException("Scrollback fixture output did not arrive. " + Text(session.ReadFrame()));
    }
}
