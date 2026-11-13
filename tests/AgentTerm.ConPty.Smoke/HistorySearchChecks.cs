using System.Diagnostics;
using AgentTerm.Agents;
using AgentTerm.Terminal;

internal static class HistorySearchChecks
{
    public static async Task RunAsync()
    {
        await using var session = new TerminalSession(100, 20, TestShell.Profile);
        await TestShell.StartLegacyAsync(session);
        foreach (var marker in new[] { "ONE", "TWO" })
        {
            session.Send("1..30 | ForEach-Object { 'prompt navigation output ' + $_ }; ('PROMPT_' + '" + marker + "')\r");
            var promptWait = Stopwatch.StartNew();
            while (!session.ReadText().Contains("PROMPT_" + marker) || session.PromptState != ShellPromptState.Ready)
            {
                if (promptWait.Elapsed > TimeSpan.FromSeconds(15)) throw new Exception("Integrated prompt fixture timed out.");
                await Task.Delay(20);
            }
        }
        // Owned agent output is the screen result: CR progress rewrites collapse to what the terminal displays.
        var owned = await session.ExecuteAsync(new("Write-Host -NoNewline ('10%' + [char]13 + '20%' + [char]13 + 'done 100%'); Write-Output ''; Write-Output ('after-' + 'progress')")).WaitAsync(TimeSpan.FromSeconds(25));
        if (owned.Status != TerminalExecutionStatus.Completed || owned.Output.Replace("\r\n", "\n") != "done 100%\nafter-progress\n")
            throw new Exception("Owned output must be screen-semantic: " + System.Text.Json.JsonSerializer.Serialize(owned.Output));
        var frame = session.ReadFrame();
        var outputRow = Enumerable.Range(0, frame.Rows).Last(row => string.Concat(Enumerable.Range(0, frame.Columns).Select(c => frame.Cells[row * frame.Columns + c].Text)).StartsWith("after-progress"));
        var semantic = session.ReadCommandOutput(frame.Viewport.Offset + outputRow, 0);
        if (semantic is null || !semantic.Contains("done 100%") || !semantic.Contains("after-progress"))
            throw new Exception("Native semantic output must cover the shell's OSC 133 output region: " + semantic);
        var promptBottom = session.ReadFrame().Viewport.MaximumOffset;
        session.NavigatePrompt(-1);
        if (session.ReadFrame().Viewport.Offset >= promptBottom) throw new Exception("Default PowerShell integration must provide native prompt navigation markers.");
        session.ScrollTo(promptBottom);
        // One console write per row costs PowerShell about 1 ms of CPU each, which dominated this fixture on a loaded
        // machine. A single batched write produces the same rows in well under a second.
        session.Send("[Console]::Out.Write(((1..14000 -replace '^','needle row ') -join \"`r`n\") + \"`r`n\"); ('HISTORY_' + 'READY')\r");
        var deadline = Stopwatch.StartNew();
        var retained = 0;
        while (!session.ReadText().Contains("HISTORY_READY") || session.PromptState != ShellPromptState.Ready ||
            (retained = (await session.ExportHistoryAsync(TerminalExportFormat.Plain))!.Text.Split("needle row").Length - 1) < 1000)
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(60))
            {
                var screen = session.ReadText().TrimEnd();
                throw new Exception($"History fixture timed out: prompt {session.PromptState}, {retained} retained rows, screen tail <{screen[^Math.Min(200, screen.Length)..]}>.");
            }
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
        session.Send("1..300 | ForEach-Object { ('FLOW_' + $_); Start-Sleep -Milliseconds 10 }\r");
        deadline.Restart();
        while (!session.ReadText().Contains("FLOW_1"))
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new Exception("Streaming fixture did not start.");
            await Task.Delay(10);
        }
        using var streamingCancellation = new CancellationTokenSource();
        var visibleMatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamingSearch = session.FindAsync("needle row", true, true, new MatchProgress(visibleMatch), streamingCancellation.Token);
        var first = await Task.WhenAny(visibleMatch.Task, streamingSearch).WaitAsync(TimeSpan.FromSeconds(2));
        if (first == streamingSearch && !(await streamingSearch).Found) throw new Exception("Streaming search returned no visible match.");
        streamingCancellation.Cancel();
        try { await streamingSearch; } catch (OperationCanceledException) { }
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
    private sealed class MatchProgress(TaskCompletionSource match) : IProgress<TerminalSearchProgress>
    {
        public void Report(TerminalSearchProgress value) { if (value.Found) match.TrySetResult(); }
    }
}
