namespace AgentTerm.Terminal;

/// <summary>
/// A primary-screen row that moves with scrolling, output and reflow. It expires when history trims or
/// clears that row, or when its terminal is disposed. Read it only through its owning terminal.
/// </summary>
public sealed class TerminalAnchor
{
    internal GhosttyTerminal.TrackedHandle? Handle;
    public bool Expired => Handle is null;
}

public sealed partial class GhosttyTerminal
{
    public const int AnchorLimit = 64;
    private readonly List<TerminalAnchor> _anchors = [];

    /// <summary>Anchors a row of the active area. The oldest anchor expires past <see cref="AnchorLimit"/>.</summary>
    public TerminalAnchor AnchorActiveRow(int row)
    {
        ThrowIfDisposed();
        if ((uint)row >= _rows) throw new ArgumentOutOfRangeException(nameof(row));
        var point = new Native.Point { Tag = 0, Value = new Native.PointValue { X = 0, Y = (uint)row } };
        ThrowIfFailed(ScrollNative.ghostty_terminal_grid_ref_track(_handle, point, out var tracked), "Could not anchor terminal row.");
        var anchor = new TerminalAnchor { Handle = new TrackedHandle(tracked) };
        _anchors.Add(anchor);
        while (_anchors.Count > AnchorLimit) ReleaseAnchor(_anchors[0]);
        return anchor;
    }

    /// <summary>The anchor's row in screen coordinates, the space of <see cref="TerminalViewport.Offset"/>. Null once expired.</summary>
    public long? ReadAnchor(TerminalAnchor anchor)
    {
        ThrowIfDisposed();
        if (anchor.Handle is null) return null;
        var result = ScrollNative.ghostty_tracked_grid_ref_point(anchor.Handle, 2, out var point);
        if (result == -4) { ReleaseAnchor(anchor); return null; }
        ThrowIfFailed(result, "Could not read terminal anchor.");
        // History rows always sit above the shell cursor. An anchor below it survived an in-place clear.
        var frame = ReadFrame();
        if (!frame.Viewport.IsAlternateScreen && frame.Viewport.IsAtBottom && point.Y > frame.Viewport.Offset + frame.Cursor.Row)
        {
            ReleaseAnchor(anchor);
            return null;
        }
        return point.Y;
    }

    public void ReleaseAnchor(TerminalAnchor anchor)
    {
        anchor.Handle?.Dispose();
        anchor.Handle = null;
        _anchors.Remove(anchor);
    }

    private void DisposeAnchors()
    {
        foreach (var anchor in _anchors.ToArray()) ReleaseAnchor(anchor);
    }
}
