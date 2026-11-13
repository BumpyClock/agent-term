using AgentTerm_App;

static class CoalescingChecks
{
    public static void Run()
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        var frames = new Queue<Action>();
        var updates = 0;
        CoalescedUpdate? gate = null;
        gate = new(() => { frames.Enqueue(gate!.Run); return true; }, () => updates++);
        for (var ms = 0; ms < 1000; ms++)
        {
            if (ms % 10 == 0) gate.Request();
            if (ms * 60 / 1000 != (ms + 1) * 60 / 1000) while (frames.TryDequeue(out var frame)) frame();
        }
        Check(updates is >= 59 and <= 60, $"100 changes per second at 60 Hz render at most once per frame, got {updates}.");

        var again = 0;
        CoalescedUpdate? nested = null;
        nested = new(() => { frames.Enqueue(nested!.Run); return true; }, () => { if (again++ == 0) nested!.Request(); });
        nested.Request(); nested.Request();
        Check(frames.Count == 1, "A burst queues one update.");
        while (frames.TryDequeue(out var frame)) frame();
        Check(again == 2, "A change during an update queues exactly one more update.");

        var posts = 0;
        var retried = new CoalescedUpdate(() => { posts++; return posts > 1; }, () => { });
        retried.Request(); retried.Request();
        Check(posts == 2, "A refused post must not leave the update stuck as queued.");
    }
}
