using System.Diagnostics;

namespace AgentTerm.Terminal;

public enum TerminalSelectionOutcome { Applied, Stale, Busy }

public sealed partial class TerminalSession
{
    public void SetOverscanEnabled(bool enabled) => MutateView(() => _terminal.SetOverscanEnabled(enabled));
    public void BeginKeyboardSelection() => MutateView(_terminal.BeginKeyboardSelection);
    public void AdjustSelection(TerminalSelectionAdjustment adjustment) => MutateView(() => _terminal.AdjustSelection(adjustment));

    public bool TrySelectFrameCommandOutput(TerminalFrame frame, int column, int row)
    {
        lock (_renderGate) lock (_gate)
        {
            ThrowIfClosed();
            if ((uint)column >= frame.Columns || (uint)row >= frame.Rows ||
                _terminal.ContentVersion != frame.ContentVersion || _terminal.ReadPresentedViewport() != frame.Viewport)
                return false;
            var selected = _terminal.SelectCommandOutput(frame.Viewport.Offset + row, column);
            if (selected) ScheduleHistoryCompression();
            return selected;
        }
    }

    /// <summary>
    /// Selects cells of <paramref name="frame"/> after previously dispatched operations. The queue and lock wait use
    /// <paramref name="wait"/>; a native mutation that has started must finish. Stale means output or the viewport changed. Busy means
    /// the selection was not applied and never will be.
    /// </summary>
    public TerminalSelectionOutcome SelectFrameRange(TerminalFrame frame, int startColumn, int startRow, int endColumn, int endRow, TimeSpan wait) =>
        ChangeSelectionInOrder(wait, () =>
        {
            if (_terminal.ContentVersion != frame.ContentVersion || _terminal.ReadPresentedViewport() != frame.Viewport)
                return false;
            _terminal.BeginSelection(startColumn, startRow);
            _terminal.ExtendSelection(endColumn, endRow);
            _terminal.EndSelection();
            return true;
        });

    /// <summary>Clears selection in session order with a bounded queue and lock wait. A started native mutation must finish.</summary>
    public TerminalSelectionOutcome ClearSelection(TimeSpan wait) => ChangeSelectionInOrder(wait, () =>
    {
        _terminal.ClearSelection();
        return true;
    });

    private TerminalSelectionOutcome ChangeSelectionInOrder(TimeSpan wait, Func<bool> change)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(wait, TimeSpan.Zero);
        const int Queued = 0, Running = 1, Abandoned = 2;
        var started = Stopwatch.GetTimestamp();
        TimeSpan Remaining() => TimeSpan.FromTicks(Math.Max(0, (wait - Stopwatch.GetElapsedTime(started)).Ticks));
        var claim = Queued;
        var operation = DispatchAsync(_ =>
        {
            if (Volatile.Read(ref claim) == Abandoned) return TerminalSelectionOutcome.Busy;
            if (!Monitor.TryEnter(_renderGate, Remaining())) return TerminalSelectionOutcome.Busy;
            try
            {
                if (!Monitor.TryEnter(_gate, Remaining())) return TerminalSelectionOutcome.Busy;
                try
                {
                    if (Remaining() == TimeSpan.Zero || Interlocked.CompareExchange(ref claim, Running, Queued) != Queued)
                        return TerminalSelectionOutcome.Busy;
                    ThrowIfClosed();
                    if (!change()) return TerminalSelectionOutcome.Stale;
                    ScheduleHistoryCompression();
                    return TerminalSelectionOutcome.Applied;
                }
                finally { Monitor.Exit(_gate); }
            }
            finally { Monitor.Exit(_renderGate); }
        });
        if (Task.WaitAny([operation], Remaining()) < 0 && Interlocked.CompareExchange(ref claim, Abandoned, Queued) == Queued)
            return TerminalSelectionOutcome.Busy;
        return operation.GetAwaiter().GetResult();
    }
}
