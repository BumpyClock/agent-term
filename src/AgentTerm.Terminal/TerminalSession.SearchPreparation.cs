namespace AgentTerm.Terminal;

public sealed partial class TerminalSession
{
    private bool _highlightWorkerRunning;
    private System.Runtime.ExceptionServices.ExceptionDispatchInfo? _highlightFailure;

    private void ScheduleSearchHighlights()
    {
        _highlightFailure?.Throw();
        if (!_terminal.NeedsSearchHighlights() || _highlightWorkerRunning) return;
        _highlightWorkerRunning = true;
        _ = Task.Run(PrepareSearchHighlightsAsync);
    }

    private async Task PrepareSearchHighlightsAsync()
    {
        try
        {
            while (true)
            {
                bool completed;
                using (_gate.Enter())
                {
                    if (_disposeTask is not null) return;
                    completed = _terminal.PrepareSearchHighlightsSlice(SearchSlice);
                }
                if (completed) break;
                await Task.Yield();
            }
        }
        catch (Exception error)
        {
            using (_gate.Enter()) _highlightFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
        }
        finally
        {
            using (_gate.Enter()) _highlightWorkerRunning = false;
        }
        InvalidatePendingFrameCapture();
        OutputAvailable?.Invoke();
    }
}
