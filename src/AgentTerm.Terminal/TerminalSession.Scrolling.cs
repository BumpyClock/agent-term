namespace AgentTerm.Terminal;

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

    public bool TrySelectFrameRange(TerminalFrame frame, int startColumn, int startRow, int endColumn, int endRow)
    {
        lock (_renderGate) lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            // Automation ranges refer to the displayed immutable frame. A stream
            // write or viewport change requires the client to request a new range.
            if (_terminal.ContentVersion != frame.ContentVersion || _terminal.ReadPresentedViewport() != frame.Viewport)
                return false;
            _terminal.BeginSelection(startColumn, startRow);
            _terminal.ExtendSelection(endColumn, endRow);
            _terminal.EndSelection();
            ScheduleHistoryCompression();
            return true;
        }
    }
}
