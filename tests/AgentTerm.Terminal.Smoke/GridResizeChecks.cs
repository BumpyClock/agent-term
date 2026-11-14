using AgentTerm_App;

internal static class GridResizeChecks
{
    internal static void Run()
    {
        TerminalGrid? target = null;
        var timerRunning = false;
        var sent = new List<TerminalGrid>();
        var throttle = new GridResizeThrottle(() => target, () => timerRunning = true, () => timerRunning = false, sent.Add);
        void Tick() { Check(timerRunning, "Only a started timer can tick."); throttle.Flush(); }

        target = new(80, 24, 9, 19);
        throttle.Flush();
        Check(sent.SequenceEqual([new TerminalGrid(80, 24, 9, 19)]), "A session start sends its first size at once.");

        for (ushort columns = 81; columns <= 120; columns++)
        {
            target = new(columns, 24, 9, 19);
            throttle.Request();
        }
        Check(sent.Count == 1 && timerRunning, "Layout steps wait for the timer instead of resizing the session each pass.");
        Tick();
        Check(sent.Count == 2 && sent[^1] == new TerminalGrid(120, 24, 9, 19) && !timerRunning,
            "The tick sends only the latest size of the burst.");

        target = new(90, 24, 9, 19);
        throttle.Request();
        target = new(120, 24, 9, 19);
        throttle.Request();
        Tick();
        Check(sent.Count == 2, "A burst that returns to the sent size sends nothing.");
        throttle.Request();
        Check(!timerRunning, "A request for the sent size does not start the timer.");

        target = new(100, 30, 9, 19);
        throttle.Request();
        target = new(100, 30, 10, 21);
        throttle.Flush();
        Check(sent.Count == 3 && sent[^1] == new TerminalGrid(100, 30, 10, 21) && !timerRunning,
            "A font change sends the latest size at once and cancels the pending tick.");

        target = new(110, 30, 10, 21);
        throttle.Request();
        target = null;
        Tick();
        Check(sent.Count == 3, "A tick after the session stops running or is disposed sends nothing.");

        target = new(110, 30, 10, 21);
        throttle.Request();
        Check(timerRunning, "A new size starts the timer.");
        throttle.Reset();
        Check(!timerRunning, "Releasing the session cancels the pending tick.");
        target = new(100, 30, 10, 21);
        throttle.Flush();
        Check(sent.Count == 4 && sent[^1] == new TerminalGrid(100, 30, 10, 21),
            "A restarted session receives its size even when it matches the size sent to the old session.");
        Console.WriteLine("Grid resize throttle checks passed coalescing, latest-size delivery, no-op skips, immediate flushes, and session release.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
