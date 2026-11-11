using AgentTerm.Terminal;

internal static class FullScreenChecks
{
    public static async Task RunAsync()
    {
        await using var session = new TerminalSession(80, 24, TestShell.Profile);
        session.Start();
        await WaitAsync(session, frame => Text(frame).Contains("PS "), "initial prompt");
        string executable = Path.Combine(AppContext.BaseDirectory, "AgentTerm.ConPty.Smoke.exe");
        session.Send($"& '{executable.Replace("'", "''")}' --tui-fixture\r");
        var initial = await WaitAsync(session, frame => Text(frame).Contains("FULL SCREEN FIXTURE"), "alternate screen");
        if (initial.Cells[0].Background == initial.Cells[80 * 2].Background)
            throw new Exception("Full-screen title background color was lost.");
        var highlight = initial.Cells[80 * 4].Background;
        session.SendKey(0x28, TerminalModifiers.None, TerminalKeyAction.Press);
        await WaitAsync(session, frame => frame.Cells[80 * 5].Background == highlight && frame.Cells[80 * 4].Background != highlight, "Down arrow selection");
        session.SendKey(0x71, TerminalModifiers.None, TerminalKeyAction.Press);
        await WaitAsync(session, frame => Text(frame).Contains("F2=on"), "F2 key");
        session.SendKey(0x58, TerminalModifiers.None, TerminalKeyAction.Press, "x");
        await WaitAsync(session, frame => Text(frame).Contains("Typed=x") && frame.Cursor.Visible, "typed character and cursor");
        session.Resize(90, 28);
        session.SendKey(0x26, TerminalModifiers.None, TerminalKeyAction.Press);
        await WaitAsync(session, frame => frame.Columns == 90 && frame.Rows == 28 && frame.Cells[90 * 4].Background == highlight, "resized full-screen redraw");
        session.SendKey(0x1b, TerminalModifiers.None, TerminalKeyAction.Press);
        await WaitAsync(session, frame => Text(frame).Contains("FULL_SCREEN_FIXTURE_EXITED") && Text(frame).Contains("PS "), "primary screen restoration");
        session.Send("exit\r");
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        Console.WriteLine("Full-screen ConPTY fixture passed colors, encoded arrows/F2/text, cursor, resize, and alternate-screen restoration.");
    }

    private static async Task<TerminalFrame> WaitAsync(TerminalSession session, Func<TerminalFrame, bool> predicate, string step)
    {
        var limit = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        TerminalFrame frame;
        do
        {
            frame = session.ReadFrame();
            if (predicate(frame)) return frame;
            if (session.Completion.IsCompleted) throw new Exception($"Shell exited during {step}. {Text(frame)}");
            await Task.Delay(30);
        } while (DateTime.UtcNow < limit);
        throw new TimeoutException($"Full-screen test timed out during {step}.\n{Text(frame)}");
    }

    private static string Text(TerminalFrame frame) => string.Join("\n", Enumerable.Range(0, frame.Rows)
        .Select(row => string.Concat(frame.Cells.Skip(row * frame.Columns).Take(frame.Columns).Select(cell => cell.Text))));
}
