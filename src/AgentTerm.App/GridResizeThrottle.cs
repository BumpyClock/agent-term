namespace AgentTerm_App;

/// <summary>A grid size as the session receives it. Cell dimensions are device pixels.</summary>
internal readonly record struct TerminalGrid(ushort Columns, ushort Rows, uint CellWidth, uint CellHeight);

/// <summary>
/// Throttles grid sizes sent to the session. Each send costs a ConPTY resize and a libghostty reflow, so sending one per layout
/// pass during a live window resize delays program output by seconds. As in Ghostty's termio thread, the first request starts a
/// 25 ms timer, later requests only update the target, and the tick sends the latest target once.
/// </summary>
internal sealed class GridResizeThrottle(Func<TerminalGrid?> target, Action startTimer, Action stopTimer, Action<TerminalGrid> send)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(25);
    private TerminalGrid? _sent;
    private bool _scheduled;

    /// <summary>Sends the target at the next tick. Reads the target again then, so the latest size wins.</summary>
    public void Request()
    {
        if (_scheduled || target() is not { } grid || grid == _sent) return;
        _scheduled = true;
        startTimer();
    }

    /// <summary>Sends the target now if the session does not have it. The timer tick calls this.</summary>
    public void Flush()
    {
        Cancel();
        if (target() is not { } grid || grid == _sent) return;
        _sent = grid;
        send(grid);
    }

    /// <summary>Forgets the sent size when the session is released, so the next session receives its first size.</summary>
    public void Reset()
    {
        Cancel();
        _sent = null;
    }

    private void Cancel()
    {
        if (!_scheduled) return;
        _scheduled = false;
        stopTimer();
    }
}
