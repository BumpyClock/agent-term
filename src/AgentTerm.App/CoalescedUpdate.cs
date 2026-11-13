namespace AgentTerm_App;

/// <summary>Collapses change notifications from any thread into one pending update. A change during the update queues one more.</summary>
internal sealed class CoalescedUpdate(Func<bool> post, Action update)
{
    private int _queued;
    public void Request()
    {
        if (Interlocked.Exchange(ref _queued, 1) == 0 && !post()) Volatile.Write(ref _queued, 0);
    }
    public void Run()
    {
        Volatile.Write(ref _queued, 0);
        update();
    }
}
