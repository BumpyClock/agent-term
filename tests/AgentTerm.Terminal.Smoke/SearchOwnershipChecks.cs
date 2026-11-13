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
                // Exact-case filtering yields after finding provisional matches. Replace one before completion.
                if (replaced || value.Complete || !session.HasSelection) return;
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

    private sealed class ProgressAction(Action<TerminalSearchProgress> action) : IProgress<TerminalSearchProgress>
    {
        public void Report(TerminalSearchProgress value) => action(value);
    }
}
