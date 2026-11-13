using System.Diagnostics;
using AgentTerm.Terminal;

internal static class HistorySearchChecks
{
    public static async Task RunAsync()
    {
        await using var session = new TerminalSession(100, 20, TestShell.Profile);
        await TestShell.StartLegacyAsync(session);
        session.Send("1..14000 | ForEach-Object { 'needle row ' + $_ }; ('HISTORY_' + 'READY')\r");
        var deadline = Stopwatch.StartNew();
        while (!session.ReadText().Contains("HISTORY_READY") || session.PromptState != ShellPromptState.Ready ||
            session.ExportHistory(TerminalExportFormat.Plain)!.Split("needle row").Length < 1001)
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(60)) throw new Exception("History fixture timed out.");
            await Task.Delay(20);
        }
        using var cancel = new CancellationTokenSource();
        var old = session.FindAsync("needle", true, cancellationToken: cancel.Token);
        cancel.Cancel();
        try { await old; throw new Exception("Search was not cancelled."); } catch (OperationCanceledException) { }
        int updates = 0;
        var current = await session.FindAsync("needle row", false, progress: new SearchProgress(() => Interlocked.Increment(ref updates)));
        if (!current.Found || current.Matches < 1000 || updates < 2) throw new Exception($"Large search did not progress through retained history. Found={current.Found}, matches={current.Matches}, updates={updates}.");
        var exact = await session.FindAsync("needle row", true, true);
        if (!exact.Found || exact.Matches != current.Matches) throw new Exception("Exact-case count must include all matching history candidates.");
        var sensitive = await session.FindAsync("NEEDLE ROW", true, true);
        if (sensitive.Found || sensitive.Matches != 0) throw new Exception("Case filtering must reject ASCII case variants.");
        session.Send("('newneedle_' + 'marker')\r");
        deadline.Restart();
        while (!session.ReadText().Contains("newneedle_marker") || session.PromptState != ShellPromptState.Ready)
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new Exception("Output fixture timed out.");
            await Task.Delay(10);
        }
        var updated = await session.FindAsync("newneedle_marker", false);
        if (!updated.Found) throw new Exception("A new query must see concurrent terminal output.");
        session.Resize(55, 20);
        if (!(await session.FindAsync("needle row", true)).Found) throw new Exception("Reflow must refresh search results.");
        var resizedDuringSearch = 0;
        var reflowed = await session.FindAsync("needle", false, progress: new SearchProgress(() =>
        {
            if (Interlocked.Exchange(ref resizedDuringSearch, 1) == 0) session.Resize(63, 20);
        })).WaitAsync(TimeSpan.FromSeconds(30));
        if (!reflowed.Found || resizedDuringSearch == 0 || reflowed.HistoryVersion != session.HistoryVersion)
            throw new Exception("Reflow between incremental ticks must re-feed and publish the current revision.");
        var pending = session.FindAsync("needle row", false);
        var disposal = session.DisposeAsync().AsTask();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); } catch (ObjectDisposedException) { }
        await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        Console.WriteLine("History search session checks passed.");
    }
    private sealed class SearchProgress(Action update) : IProgress<TerminalSearchProgress>
    {
        public void Report(TerminalSearchProgress value) => update();
    }
}
