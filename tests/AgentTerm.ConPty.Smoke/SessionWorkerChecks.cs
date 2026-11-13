using AgentTerm.Terminal;

internal static class SessionWorkerChecks
{
    public static async Task RunAsync()
    {
        await using var busy = new TerminalSession(80, 24, TestShell.Profile);
        await using var quiet = new TerminalSession(80, 24, TestShell.Profile);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocked = busy.DispatchAsync(_ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Worker test release missing.");
        });
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Worker did not start.");
            var first = busy.CaptureFrameAsync();
            var coalesced = busy.CaptureFrameAsync();
            if (!ReferenceEquals(first, coalesced)) throw new Exception("Pending captures did not coalesce.");
            var order = new List<int>();
            var operations = Enumerable.Range(0, 20).Select(index => busy.DispatchAsync(_ => order.Add(index))).ToArray();
            var afterCommands = busy.CaptureFrameAsync();
            if (ReferenceEquals(first, afterCommands)) throw new Exception("A command reused an older pending capture.");
            var independent = await quiet.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (independent.Frame.Cells.Length != 80 * 24) throw new Exception("Independent pane frame incomplete.");
            if (first.IsCompleted) throw new Exception("Busy pane escaped its serialized owner.");
            release.Set();
            await blocked;
            await Task.WhenAll(operations);
            var latest = await afterCommands.WaitAsync(TimeSpan.FromSeconds(10));
            if (!order.SequenceEqual(Enumerable.Range(0, 20))) throw new Exception("Session operations were reordered.");
            if (!ReferenceEquals(latest.Frame, busy.LatestFrame)) throw new Exception("Latest owned frame was not published.");
        }
        finally { release.Set(); }

        await busy.DisposeAsync();
        try { await busy.CaptureFrameAsync(); throw new Exception("Disposed session accepted capture."); }
        catch (ObjectDisposedException) { }
        try { await busy.DispatchAsync(_ => { }); throw new Exception("Disposed session accepted an operation."); }
        catch (ObjectDisposedException) { }
        await CheckCapacityAsync();
        await CheckIdleMaintenanceYieldsAsync();
        await CheckSelectionReadOrderingAsync();
        await CheckCompressionQuietWindowAsync();
        await OutputFairnessChecks.RunAsync();
        Console.WriteLine("Session worker checks passed. Independent panes, ordered commands, coalesced capture, publication and disposal.");
    }

    private static async Task CheckSelectionReadOrderingAsync()
    {
        await using var session = new TerminalSession(80, 24, TestShell.Profile);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var terminal = (GhosttyTerminal)typeof(TerminalSession).GetField("_terminal", flags)!.GetValue(session)!;
        terminal.Write("HELLO worker selection"u8);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocked = session.DispatchAsync(_ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Selection ordering release missing.");
        });
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Selection worker did not start.");
            var selected = session.DispatchAsync(owner =>
            {
                owner.BeginSelection(0, 0);
                owner.ExtendSelection(4, 0);
                owner.EndSelection();
            });
            var rich = session.CopySelectionRichAsync();
            var exported = session.ExportHistoryAsync(TerminalExportFormat.Plain, selectionOnly: true);
            await Task.WhenAny(Task.WhenAll(rich, exported), Task.Delay(100));
            if (rich.IsCompleted || exported.IsCompleted)
                throw new Exception("A selection read bypassed the queued selection mutation.");
            release.Set();
            await blocked;
            await selected;
            var copy = await rich.WaitAsync(TimeSpan.FromSeconds(10));
            var export = await exported.WaitAsync(TimeSpan.FromSeconds(10));
            if (copy?.Text != "HELLO" || export?.Text != "HELLO")
                throw new Exception("Queued selection was not observed by rich copy and selection export.");
        }
        finally { release.Set(); }
    }

    private static async Task CheckCompressionQuietWindowAsync()
    {
        await using var session = new TerminalSession(80, 24, TestShell.Profile);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var terminal = (GhosttyTerminal)typeof(TerminalSession).GetField("_terminal", flags)!.GetValue(session)!;
        var renderGate = typeof(TerminalSession).GetField("_renderGate", flags)!.GetValue(session)!;
        var gate = typeof(TerminalSession).GetField("_gate", flags)!.GetValue(session)!;
        lock (renderGate) lock (gate)
        {
            terminal.Write(System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("cold history compression fixture\r\n", 4000))));
            session.ScrollToBottom();
            var callback = typeof(TerminalSession).GetMethod("CompressIdle", flags)!;
            var activityAt = typeof(TerminalSession).GetField("_compressionActivityAt", flags)!;
            long CompressedPages() => long.Parse(System.Text.RegularExpressions.Regex.Match(terminal.ReadDiagnosticReport(), @"Compressed pages (\d+)").Groups[1].Value);
            for (var index = 0; index < 20; index++)
            {
                activityAt.SetValue(session, System.Diagnostics.Stopwatch.GetTimestamp());
                callback.Invoke(session, [null]);
            }
            if (CompressedPages() != 0) throw new Exception("Early idle callbacks compressed history before the quiet deadline.");
            activityAt.SetValue(session, System.Diagnostics.Stopwatch.GetTimestamp() - 3 * System.Diagnostics.Stopwatch.Frequency);
            for (var index = 0; index < 20 && CompressedPages() == 0; index++) callback.Invoke(session, [null]);
            if (CompressedPages() == 0) throw new Exception("Compression fixture did not compress eligible history after its deadline.");
        }
    }

    private static async Task CheckIdleMaintenanceYieldsAsync()
    {
        await using var session = new TerminalSession(80, 24, TestShell.Profile);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var callback = typeof(TerminalSession).GetMethod("CompressIdle", flags)!;
        foreach (var field in new[] { "_renderGate", "_gate" })
        {
            var gate = typeof(TerminalSession).GetField(field, flags)!.GetValue(session)!;
            Task callbackTask;
            bool returned;
            lock (gate)
            {
                callbackTask = Task.Run(() => callback.Invoke(session, [null]));
                returned = callbackTask.Wait(TimeSpan.FromSeconds(5));
            }
            await callbackTask;
            if (!returned) throw new Exception("Idle compression waited behind active terminal work on " + field);
        }
    }

    private static async Task CheckCapacityAsync()
    {
        await using var session = new TerminalSession(80, 24, TestShell.Profile);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocked = session.DispatchAsync(_ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Capacity test release missing.");
        });
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Capacity worker did not start.");
            var completed = 0;
            var accepted = Enumerable.Range(0, 1024).Select(_ => session.DispatchAsync(_ => completed++)).ToArray();
            try { await session.DispatchAsync(_ => completed++); throw new Exception("Full worker queue accepted an extra operation."); }
            catch (InvalidOperationException error) when (error.Message == "The terminal operation queue is full.") { }
            release.Set();
            await blocked;
            await Task.WhenAll(accepted).WaitAsync(TimeSpan.FromSeconds(10));
            if (completed != 1024) throw new Exception("Accepted operations were lost or a rejected operation executed.");
        }
        finally { release.Set(); }
    }
}
