namespace AgentTerm.Terminal;

public sealed partial class GhosttyTerminal
{
    private bool _bottomAnchored;
    private bool _presentationDirty = true;
    private bool _preparingPresentation;
    private bool _following = true;
    private long? _presentedTotal;

    public void SetBottomAnchored(bool anchored)
    {
        ThrowIfDisposed();
        if (_bottomAnchored == anchored) return;
        _bottomAnchored = anchored;
        _presentationDirty = true;
        _viewRevision++;
    }

    public unsafe int ReadActiveCursorRow()
    {
        ThrowIfDisposed();
        ushort row = 0;
        ThrowIfFailed(RenderNative.ghostty_terminal_get(_handle, (int)TerminalData.CursorY, &row), "Could not read active cursor row.");
        return row;
    }

    public long ActiveTop { get { ThrowIfDisposed(); return ReadNativeViewport().TotalRows - _rows; } }

    private void EnsurePresentation()
    {
        if (_preparingPresentation || !_presentationDirty) return;
        // During a render hold the presented view stays at the boundary. Preparing it would update the render state
        // over a pending boundary snapshot, or move the view and force a half-drawn capture.
        if (_holdActive && !_presentingBoundary) return;
        if (!_bottomAnchored && _presentedTotal is null)
        {
            _presentationDirty = false;
            return;
        }
        _preparingPresentation = true;
        try
        {
            var original = ReadNativeViewport();
            var before = (_presentedTotal, original.Offset);
            _presentedTotal = null;
            if (_bottomAnchored && !original.IsAlternateScreen)
            {
                NativeScroll(original.MaximumOffset);
                _presentedTotal = original.TotalRows - original.Rows + LastContentRow() + 1;
            }
            var maximum = Math.Max(0, (_presentedTotal ?? original.TotalRows) - original.Rows);
            var target = _following && !HasSelection() ? maximum : Math.Min(original.Offset, maximum);
            NativeScroll(target);
            // Only a changed presentation is a view change. A render-hold boundary settles presentation too, and an
            // unchanged view must not invalidate the held frame.
            if (before != (_presentedTotal, target)) _viewRevision++;
            _presentationDirty = false;
        }
        finally { _preparingPresentation = false; }
    }

    private void NativeScroll(long offset)
    {
        var behavior = offset >= ReadNativeViewport().MaximumOffset
            ? new ScrollBehavior { Tag = 1 }
            : new ScrollBehavior { Tag = 3, Value = (nint)offset };
        ThrowIfFailed(ScrollNative.ghostty_terminal_scroll_viewport(_handle, behavior), "Could not position presentation viewport.");
    }
}
