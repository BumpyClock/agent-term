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
        ushort row = 0;
        ThrowIfFailed(RenderNative.ghostty_terminal_get(_handle, 4, &row), "Could not read active cursor row.");
        return row;
    }

    public long ActiveTop => ReadNativeViewport().TotalRows - _rows;

    private void EnsurePresentation()
    {
        if (_preparingPresentation || !_presentationDirty) return;
        if (!_bottomAnchored && _presentedTotal is null)
        {
            _presentationDirty = false;
            return;
        }
        _preparingPresentation = true;
        try
        {
            var original = ReadNativeViewport();
            _presentedTotal = null;
            if (_bottomAnchored && !original.IsAlternateScreen)
            {
                // Render state only exposes viewport cells. Inspect the active grid without
                // publishing the temporary frame or moving any PTY cursor.
                NativeScroll(original.MaximumOffset);
                var active = ReadFrame();
                var last = ReadActiveCursorRow();
                for (var row = active.Rows - 1; row > last; row--)
                {
                    if (active.Cells.Skip(row * active.Columns).Take(active.Columns).Any(c =>
                        !string.IsNullOrWhiteSpace(c.Text) || c.Background != active.Background || c.Underline || c.Strikethrough))
                    { last = row; break; }
                }
                _presentedTotal = original.TotalRows - original.Rows + last + 1;
            }
            var maximum = Math.Max(0, (_presentedTotal ?? original.TotalRows) - original.Rows);
            var target = _following && !HasSelection() ? maximum : Math.Min(original.Offset, maximum);
            NativeScroll(target);
            _viewRevision++;
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
