namespace AgentTerm.Terminal;

/// <summary>
/// Terminal exclusion for one session. Output parsing holds the gate one slice at a time and calls
/// <see cref="YieldToWaiters"/> between slices. A waiter is a caller blocked in <see cref="Enter"/> or
/// <see cref="TryEnter"/>, or a queued operation from <see cref="Reserve"/> until it first holds the gate, so each
/// takes the gate within one slice of reaching it.
/// </summary>
internal sealed class TerminalGate
{
    [ThreadStatic] private static TerminalGate? t_reserved;
    private int _waiters;
    private int _parked;
    private readonly ManualResetEventSlim _drained = new(false);

    public Scope Enter()
    {
        var reserved = TakeReservation();
        if (reserved || !Monitor.TryEnter(this))
        {
            if (!reserved) Interlocked.Increment(ref _waiters);
            try { Monitor.Enter(this); }
            finally { Unreserve(); }
        }
        return new(this);
    }

    /// <summary>Waits at most <paramref name="timeout"/>. Check <see cref="Scope.Entered"/>; disposing a failed scope does nothing.</summary>
    public Scope TryEnter(TimeSpan timeout)
    {
        if (!TakeReservation())
        {
            if (Monitor.TryEnter(this)) return new(this);
            if (timeout <= TimeSpan.Zero) return default;
            Interlocked.Increment(ref _waiters);
        }
        try { return Monitor.TryEnter(this, timeout) ? new(this) : default; }
        finally { Unreserve(); }
    }

    /// <summary>Counts a queued operation from enqueue. Run it with <see cref="RunReserved"/>, or call <see cref="Unreserve"/> if it is not queued.</summary>
    public void Reserve() => Interlocked.Increment(ref _waiters);

    public void Unreserve()
    {
        if (Interlocked.Decrement(ref _waiters) == 0 && Volatile.Read(ref _parked) != 0) _drained.Set();
    }

    /// <summary>Runs a reserved operation. Its first entry on this thread consumes the reservation; otherwise it ends with the operation.</summary>
    public void RunReserved(Action operation)
    {
        t_reserved = this;
        try { operation(); }
        finally { if (TakeReservation()) Unreserve(); }
    }

    private bool TakeReservation()
    {
        if (t_reserved != this) return false;
        t_reserved = null;
        return true;
    }

    /// <summary>Called by the output parser between slices without holding the gate. Sleeps until waiters drain, at most one wait tick.</summary>
    public void YieldToWaiters()
    {
        if (Volatile.Read(ref _waiters) == 0) return;
        _drained.Reset();
        Interlocked.Exchange(ref _parked, 1);
        if (Volatile.Read(ref _waiters) != 0) _drained.Wait(1);
        Volatile.Write(ref _parked, 0);
    }

    public readonly struct Scope(TerminalGate? gate) : IDisposable
    {
        public bool Entered => gate is not null;
        public void Dispose() { if (gate is not null) Monitor.Exit(gate); }
    }
}
