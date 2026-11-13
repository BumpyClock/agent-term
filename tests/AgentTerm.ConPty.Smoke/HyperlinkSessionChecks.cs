using AgentTerm.Terminal;

internal static class HyperlinkSessionChecks
{
    internal static async Task RunAsync()
    {
        await using var session = new TerminalSession(100, 20, TestShell.Profile);
        session.Start();
        await WaitAsync(() => session.PromptState == ShellPromptState.Ready, "PowerShell should reach its prompt.", session);

        // The link is printed by a running program, so the prompt cannot be what created it.
        session.Send("$e=[char]27; $label='link'+'text'; [Console]::Write(\"$e]8;;https://link.test/path?q=1$([char]7)$label$e]8;;$([char]7) plain\"); " +
            "[Console]::Write(\"$e]22;pointer$e\\\"); Start-Sleep -Seconds 3\r");
        TerminalFrame frame = null!;
        (int Column, int Row) cell = (-1, -1);
        await WaitAsync(() =>
        {
            frame = session.ReadFrame();
            cell = Find(frame, "linktext");
            return cell.Column >= 0;
        }, "A link printed through ConPTY should reach the frame.", session);

        var link = session.ReadLinkAt(cell.Column + 2, cell.Row, frame) ?? throw new Exception("The session found no OSC 8 link through ConPTY.");
        if (link.Target.Text != "https://link.test/path?q=1" || link.Target.Decision != TerminalLinkDecision.Allow)
            throw new Exception($"The session link carried <{link.Target.Text}> with decision {link.Target.Decision}.");
        if (!link.Contains(cell.Column, cell.Row) || link.Contains(cell.Column + "linktext".Length, cell.Row))
            throw new Exception($"The link range should cover only its text. Spans {string.Join(",", link.Spans)}.");
        if (session.ReadLinkAt(cell.Column + "linktext".Length + 2, cell.Row, frame) is not null)
            throw new Exception("Text after the link should have no link.");

        var stale = frame with { Viewport = frame.Viewport with { Offset = frame.Viewport.Offset + 1 } };
        if (session.ReadLinkAt(cell.Column, cell.Row, stale) is not null)
            throw new Exception("A frame the terminal has scrolled away from must not resolve a link.");
        if (session.ReadLinkAt(cell.Column, cell.Row, frame with { ContentVersion = frame.ContentVersion - 1 }) is not null)
            throw new Exception("Unchanged viewport coordinates must not make an old content revision clickable.");
        if (session.ReadLinkAt(-1, 0, frame) is not null || session.ReadLinkAt(0, 1000, frame) is not null)
            throw new Exception("An out of range cell must have no link.");

        // ConPTY forwards OSC 22. Prompts do not inject output to reset a program's chosen shape.
        if (session.ReadMouseShape() != TerminalMouseShape.Pointer)
            throw new Exception($"OSC 22 through ConPTY should reach the session, got {session.ReadMouseShape()}.");
        if (session.ReadPointerMode().Shape != TerminalMouseShape.Pointer)
            throw new Exception("The pointer mode should report the requested shape.");
        await WaitAsync(() => session.PromptState == ShellPromptState.Ready, "The shell should return to its prompt.", session);
        if (session.ReadMouseShape() != TerminalMouseShape.Pointer) throw new Exception("A prompt must not rewrite the native pointer shape.");
        session.Send("[Console]::Write(\"$([char]27)]22;$([char]27)\\\")\r");
        await WaitAsync(() => session.ReadMouseShape() == TerminalMouseShape.Text, "An explicit OSC 22 reset should restore the default pointer.", session);
        var mode = session.ReadPointerMode();
        if (mode.Shape != TerminalMouseShape.Text) throw new Exception("The pointer mode should report the default shape.");

        await session.DisposeAsync();
        if (session.ReadLinkAt(cell.Column, cell.Row, frame) is not null || session.ReadMouseShape() != TerminalMouseShape.Text ||
            session.ReadPointerMode().MouseTracking)
            throw new Exception("A disposed session must report no link, the default pointer, and no mouse tracking.");
        Console.WriteLine("Hyperlink session checks passed OSC 8 and OSC 22 through ConPTY, stale frame rejection, explicit pointer reset, and disposal.");
    }

    private static (int Column, int Row) Find(TerminalFrame frame, string text)
    {
        for (var row = 0; row < frame.Rows; row++)
        {
            var line = string.Concat(Enumerable.Range(0, frame.Columns).Select(column => frame.Cells[row * frame.Columns + column].Text.PadRight(1)));
            var index = line.IndexOf(text, StringComparison.Ordinal);
            if (index >= 0) return (index, row);
        }
        return (-1, -1);
    }

    private static async Task WaitAsync(Func<bool> condition, string message, TerminalSession session)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            while (!condition()) await Task.Delay(40, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new Exception($"{message} Screen {session.ReadText()}");
        }
    }
}
