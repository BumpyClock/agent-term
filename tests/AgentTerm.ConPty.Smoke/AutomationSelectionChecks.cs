using System.Reflection;
using AgentTerm.Terminal;

internal static class AutomationSelectionChecks
{
    private static readonly TimeSpan Ample = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);

    public static async Task RunAsync()
    {
        await using var session = new TerminalSession(80, 24, TestShell.Profile);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var terminal = (GhosttyTerminal)typeof(TerminalSession).GetField("_terminal", flags)!.GetValue(session)!;
        terminal.Write("HELLO automation"u8);
        var frame = session.ReadFrame();

        Expect(TerminalSelectionOutcome.Applied, session.SelectFrameRange(frame, 0, 0, 4, 0, Ample), "A current frame range");
        Selected(session, "HELLO", "A current frame range selects its cells");

        await CheckOrderAfterQueuedClearAsync(session, frame);
        await CheckBlockedWorkerAsync(session, frame);
        foreach (var field in new[] { "_renderGate", "_gate" })
            await CheckHeldGateAsync(session, frame, typeof(TerminalSession).GetField(field, flags)!.GetValue(session)!, field);

        Expect(TerminalSelectionOutcome.Applied, session.ClearSelection(Ample), "An idle clear");
        Selected(session, null, "An idle clear removes the selection");
        terminal.Write(" changed"u8);
        Expect(TerminalSelectionOutcome.Stale, session.SelectFrameRange(frame, 0, 0, 4, 0, Ample), "A range from an older frame");
        Selected(session, null, "A stale range selects nothing");

        await session.DisposeAsync();
        try
        {
            session.SelectFrameRange(frame, 0, 0, 4, 0, Ample);
            throw new Exception("A disposed session accepted an automation selection.");
        }
        catch (ObjectDisposedException) { }
        Console.WriteLine("Automation selection checks passed. Ordered, bounded, stale-validated and never applied after a busy result.");
    }

    private static async Task CheckOrderAfterQueuedClearAsync(TerminalSession session, TerminalFrame frame)
    {
        using var release = new ManualResetEventSlim();
        var blocked = Block(session, release);
        var cleared = session.DispatchAsync(owner => owner.ClearSelection());
        var selected = Task.Run(() => session.SelectFrameRange(frame, 6, 0, 15, 0, Ample));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var workerGate = typeof(TerminalSession).GetField("_workerGate", flags)!.GetValue(session)!;
        var outstanding = typeof(TerminalSession).GetField("_outstandingOperations", flags)!;
        try
        {
            if (!SpinWait.SpinUntil(() =>
            {
                lock (workerGate) return (int)outstanding.GetValue(session)! == 3;
            }, Ample)) throw new TimeoutException("Selection was not queued behind the clear.");
        }
        finally { release.Set(); }
        await blocked;
        await cleared;
        Expect(TerminalSelectionOutcome.Applied, await selected.WaitAsync(Ample), "A selection behind a queued clear");
        Selected(session, "automation", "A selection behind a queued clear survives it");
    }

    private static async Task CheckBlockedWorkerAsync(TerminalSession session, TerminalFrame frame)
    {
        using var release = new ManualResetEventSlim();
        var blocked = Block(session, release);
        try
        {
            Expect(TerminalSelectionOutcome.Busy, session.ClearSelection(Short), "A clear behind a blocked worker");
            Expect(TerminalSelectionOutcome.Busy, session.SelectFrameRange(frame, 0, 0, 4, 0, Short), "A selection behind a blocked worker");
        }
        finally { release.Set(); }
        await blocked;
        await session.DispatchAsync(_ => { });
        Selected(session, "automation", "Abandoned selection changes never apply later");
    }

    private static async Task CheckHeldGateAsync(TerminalSession session, TerminalFrame frame, object gate, string name)
    {
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Factory.StartNew(() =>
        {
            lock (gate)
            {
                held.Set();
                if (!release.Wait(Ample)) throw new TimeoutException("Gate holder release missing.");
            }
        }, TaskCreationOptions.LongRunning);
        try
        {
            if (!held.Wait(Ample)) throw new TimeoutException("Gate holder did not start.");
            Expect(TerminalSelectionOutcome.Busy, session.SelectFrameRange(frame, 0, 0, 4, 0, Short), "A selection while native work holds " + name);
            Expect(TerminalSelectionOutcome.Busy, session.ClearSelection(Short), "A clear while native work holds " + name);
        }
        finally { release.Set(); }
        await holder;
        await session.DispatchAsync(_ => { });
        Selected(session, "automation", "A busy result leaves the selection unchanged after " + name + " is released");
    }

    private static Task Block(TerminalSession session, ManualResetEventSlim release)
    {
        using var entered = new ManualResetEventSlim();
        var blocked = session.DispatchAsync(_ =>
        {
            entered.Set();
            if (!release.Wait(Ample)) throw new TimeoutException("Worker release missing.");
        });
        if (!entered.Wait(Ample)) throw new TimeoutException("Worker did not start.");
        return blocked;
    }

    private static void Expect(TerminalSelectionOutcome expected, TerminalSelectionOutcome actual, string label)
    {
        if (expected != actual) throw new Exception($"{label}. Expected {expected}, got {actual}.");
    }

    private static void Selected(TerminalSession session, string? expected, string label)
    {
        var actual = session.ReadSelectionText();
        if (expected != actual) throw new Exception($"{label}. Expected <{expected}>, got <{actual}>.");
    }
}
