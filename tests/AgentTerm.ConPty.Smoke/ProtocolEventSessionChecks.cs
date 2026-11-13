using System.Collections.Concurrent;
using AgentTerm.Terminal;

/// <summary>Session protocol events arrive in stream order, after session state updates, outside the session lock.</summary>
internal static class ProtocolEventSessionChecks
{
    internal static async Task RunAsync()
    {
        await using var session = new TerminalSession(120, 20, TestShell.Profile);
        var events = new ConcurrentQueue<TerminalProtocolEvent>();
        var lockFree = true;
        session.ProtocolEvent += item =>
        {
            // Another thread must be able to take the session lock while a handler runs.
            if (!Task.Run(() => session.Title).Wait(TimeSpan.FromSeconds(5))) lockFree = false;
            events.Enqueue(item);
        };
        await TestShell.StartLegacyAsync(session);
        while (events.TryDequeue(out _)) { }

        // One write: status, title, prompt start (clears working records), title after it, then a directory report.
        session.Send("[Console]::Write([char]27 + ']7501;state=working:app=fixture' + [char]7 + [char]27 + ']2;early' + [char]7 + " +
            "[char]27 + ']133;A' + [char]7 + [char]27 + ']2;late' + [char]7 + [char]27 + ']7;file://localhost/C:/Windows' + [char]7)\r");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!events.Any(item => item is TerminalWorkingDirectoryChanged { Report: "file://localhost/C:/Windows" }))
            await Task.Delay(25, timeout.Token);

        var observed = events.Where(item => item is not TerminalWorkingDirectoryChanged || ((TerminalWorkingDirectoryChanged)item).Report.Contains("C:/Windows"))
            .Select(item => item switch
            {
                TerminalProgramStatusReport report => "status " + report.State + " " + report.App,
                TerminalTitleChanged title => "title " + title.Title,
                TerminalSemanticPrompt prompt => "prompt " + prompt.Step,
                TerminalWorkingDirectoryChanged directory => "pwd " + directory.Report,
                TerminalReset => "reset",
                _ => item.ToString()
            }).SkipWhile(line => !line.StartsWith("status", StringComparison.Ordinal)).Take(5).ToArray();
        var expected = new[] { "status Working fixture", "title early", "prompt PromptStart", "title late", "pwd file://localhost/C:/Windows" };
        if (!observed.SequenceEqual(expected)) throw new Exception("Session protocol events must keep stream order: " + string.Join(" | ", observed));
        if (!lockFree) throw new Exception("Protocol event handlers must run outside the session lock.");
        if (session.ReadProgramStatuses().Any(status => status.IsActive && status.State == ProgramState.Working))
            throw new Exception("OSC 133 prompt start must clear working program status in the same chunk.");
        Console.WriteLine("Protocol event session checks passed stream order, state application and lock-free dispatch.");
    }
}
