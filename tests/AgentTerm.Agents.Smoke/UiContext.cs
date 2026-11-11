using System.Collections.Concurrent;

sealed class UiContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
    public static void Run(Func<Task> body)
    {
        var previous = Current;
        var context = new UiContext();
        SetSynchronizationContext(context);
        try
        {
            var task = body();
            while (!task.IsCompleted)
            {
                if (!context._queue.TryTake(out var work, TimeSpan.FromSeconds(10))) throw new TimeoutException("UI fixture stalled.");
                work.Callback(work.State);
            }
            task.GetAwaiter().GetResult();
        }
        finally { SetSynchronizationContext(previous); }
    }
}
