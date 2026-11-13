using System.Reflection;
using System.Text;
using AgentTerm.Terminal;

internal static class SearchOwnershipChecks
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (bool clearWithOutput in new[] { false, true })
        {
            await using var session = new TerminalSession(60, 4);
            var terminal = (GhosttyTerminal)typeof(TerminalSession).GetField("_terminal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            var gate = typeof(TerminalSession).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            lock (gate)
            {
                terminal.Write(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 14000).Select(i => $"needle {i:D5}\r\n"))));
            }
            bool replaced = false;
            var result = await session.FindAsync("needle", true, true, new ProgressAction(value =>
            {
                // Progress runs on the search thread, so pausing here pushes a fast search past the provisional delay.
                // Then replace the provisional match before completion.
                if (replaced || value.Complete) return;
                if (!session.HasSelection) { Thread.Sleep(120); return; }
                replaced = true;
                if (clearWithOutput)
                {
                    lock (gate)
                    {
                        terminal.Write("\u001bc"u8);
                        if (terminal.HasSelection()) throw new Exception("Reset output did not clear the provisional selection.");
                        terminal.Write(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("needle after reset\r\n", 200))));
                    }
                }
                else
                {
                    session.BeginSelection(40, 3); session.ExtendSelection(42, 3); session.EndSelection();
                }
            })).WaitAsync(TimeSpan.FromSeconds(30));
            if (!replaced) throw new Exception("Fixture did not replace a provisional selection.");
            if (!result.Found || session.ReadSelectionText() != "needle")
                throw new Exception($"Find must recover the current query selection after {(clearWithOutput ? "output" : "pointer selection")}. Found={result.Found}, selected=<{session.ReadSelectionText()}>.");
        }
        await CheckRefreshAsync();
        await CheckLifetimeAsync();
        await CheckCachedNavigationAsync();
        Console.WriteLine("Search ownership checks passed.");
    }
    private static async Task CheckRefreshAsync()
    {
        await using var session = new TerminalSession(60, 4);
        var terminal = (GhosttyTerminal)typeof(TerminalSession).GetField("_terminal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        terminal.Write(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 50).Select(i => $"needle {i}\r\n" + string.Concat(Enumerable.Repeat("blank\r\n", 10))))));
        if (!(await session.FindAsync("needle", false)).Found) throw new Exception("Refresh fixture needs a selected match.");
        var before = session.ReadFrame().Viewport.Offset;
        terminal.Write("other output\r\n"u8);
        var refreshed = await session.FindAsync("needle", true, retainSelection: true);
        if (!refreshed.Found || session.ReadSelectionText() != "needle" || session.ReadFrame().Viewport.Offset != before)
            throw new Exception("Automatic refresh must retain the selected match and viewport.");
        await session.FindAsync("needle", false);
        if (session.ReadFrame().Viewport.Offset >= before) throw new Exception("Explicit previous must advance to an older match.");
        await session.FindAsync("needle", true);
        if (session.ReadFrame().Viewport.Offset != before) throw new Exception("Explicit next must return to the newer match.");
        terminal.Write("\u001b[2J\u001b[HNEEDLE K"u8);
        var exact = await session.FindAsync("NEEDLE", false, true, retainSelection: true);
        if (!exact.Found || exact.Matches != 1 || session.ReadSelectionText() != "NEEDLE") throw new Exception("Query/case changes must replace retained selection.");
        terminal.Write("\u001bcK"u8);
        if ((await session.FindAsync("k", true)).Found) throw new Exception("Native ASCII case folding must not fold Kelvin sign.");
    }

    private static async Task CheckLifetimeAsync()
    {
        await using var session = new TerminalSession(60, 4);
        var terminal = (GhosttyTerminal)typeof(TerminalSession).GetField("_terminal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        terminal.Write(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("needle\r\n", 14000))));
        using var cancel = new CancellationTokenSource();
        try
        {
            await session.FindAsync("needle", true, true, new ProgressAction(_ => cancel.Cancel()), cancel.Token);
            throw new Exception("Progress cancellation must cancel search.");
        }
        catch (OperationCanceledException) { }
        if (!(await session.FindAsync("needle", true)).Found) throw new Exception("Cancelled search must release ownership.");
        var pending = session.FindAsync("needle", true, true);
        var disposal = session.DisposeAsync().AsTask();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); } catch (ObjectDisposedException) { }
        await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        try { await session.FindAsync("needle", true); throw new Exception("Disposed search must fail."); }
        catch (ObjectDisposedException) { }
    }

    private static async Task CheckCachedNavigationAsync()
    {
        await using var session = new TerminalSession(60, 4);
        var terminal = (GhosttyTerminal)typeof(TerminalSession).GetField("_terminal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        terminal.Write(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 2000).Select(i => i % 2 == 0 ? "needle\r\n" : "NEEDLE\r\n"))));
        var first = await session.FindAsync("needle", false, true);
        if (!first.Found || first.Matches != 1000 || first.SelectedOrdinal is null) throw new Exception("Exact-case fixture must find 1000 matches with an ordinal.");
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var previous = first;
        for (var i = 0; i < 20; i++)
        {
            // Capture highlights between navigation calls, as the app does. It feeds native search independently.
            session.ReadFrame();
            var next = await session.FindAsync("needle", true, true);
            var expected = previous.SelectedOrdinal % previous.Matches + 1;
            if (next.Matches != first.Matches || next.SelectedOrdinal != expected || session.ReadSelectionText() != "needle")
                throw new Exception("Repeated exact navigation must retain counts, order, and exact selected text across highlight feeds.");
            previous = next;
        }
        Console.WriteLine($"Exact navigation allocated {GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore} bytes across 20 actions including frame capture.");
        terminal.Write("needle\r\n"u8);
        var changed = await session.FindAsync("needle", true, true);
        if (changed.Matches != 1001) throw new Exception("New output must invalidate cached exact counts.");
        terminal.Resize(4, 4);
        if ((await session.FindAsync("needle", true, true)).Matches != 1001) throw new Exception("Reflow must preserve exact matching after cache invalidation.");
        terminal.Write("\u001bcNEEDLE"u8);
        if ((await session.FindAsync("needle", true, true)).Matches != 0 || (await session.FindAsync("NEEDLE", true, true)).Matches != 1)
            throw new Exception("Reset and query replacement must discard exact classification.");
    }

    private sealed class ProgressAction(Action<TerminalSearchProgress> action) : IProgress<TerminalSearchProgress>
    {
        public void Report(TerminalSearchProgress value) => action(value);
    }
}
