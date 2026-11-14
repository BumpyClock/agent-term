using AgentTerm_App;

internal static class TrailingThrottleChecks
{
    internal static void Run()
    {
        var timerRunning = false;
        var timerStarts = 0;
        var runs = 0;
        var throttle = new TrailingThrottle(() => { timerRunning = true; timerStarts++; }, () => timerRunning = false, () => runs++);
        void Tick() { Check(timerRunning, "Only a started timer can tick."); throttle.Flush(); }

        for (var request = 0; request < 40; request++) throttle.Request();
        Check(runs == 0 && timerRunning && timerStarts == 1, "A burst of requests starts one timer and runs nothing yet.");
        Tick();
        Check(runs == 1 && !timerRunning, "The tick runs once for the whole burst.");

        throttle.Request();
        throttle.Flush();
        Check(runs == 2 && !timerRunning, "A flush runs at once and cancels the pending tick.");
        throttle.Flush();
        Check(runs == 3, "A flush without a pending request still runs.");

        throttle.Request();
        throttle.Cancel();
        Check(runs == 3 && !timerRunning, "A cancel drops the pending tick without running.");
        throttle.Request();
        Check(timerRunning && timerStarts == 4, "A request after a cancel starts the timer again.");
        Console.WriteLine("Trailing throttle checks passed coalescing, immediate flushes, and cancellation.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
