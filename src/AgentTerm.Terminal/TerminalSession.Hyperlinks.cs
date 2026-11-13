namespace AgentTerm.Terminal;

/// <summary>What a program asked the pointer to do over its terminal.</summary>
public readonly record struct TerminalPointerMode(bool MouseTracking, TerminalMouseShape Shape);

public sealed partial class TerminalSession
{
    /// <summary>
    /// The OSC 8 link at a cell of the frame the caller is showing, or null. Returns null when the terminal has moved
    /// on from that frame (scrolled, grown, switched screen), so a stale frame never resolves another cell's link.
    /// The result is owned data and holds no native reference.
    /// </summary>
    public TerminalLink? ReadLinkAt(int column, int row, TerminalFrame displayed)
    {
        lock (_renderGate) lock (_gate)
        {
            if (_disposeTask is not null) return null;
            return _terminal.ContentVersion == displayed.ContentVersion && _terminal.ReadPresentedViewport() == displayed.Viewport
                ? _terminal.ReadLink(column, row) : null;
        }
    }

    /// <summary>The pointer shape the program requested with OSC 22. Reading it does not wait for a frame.</summary>
    public TerminalMouseShape ReadMouseShape()
    {
        lock (_gate) return _disposeTask is not null ? TerminalMouseShapes.Initial : _terminal.ReadMouseShape();
    }

    /// <summary>Mouse tracking and requested pointer shape under one lock, for hosts that read both on every pointer move.</summary>
    public TerminalPointerMode ReadPointerMode()
    {
        lock (_gate)
            return _disposeTask is not null ? new(false, TerminalMouseShapes.Initial)
                : new(_terminal.IsMouseTrackingEnabled, _terminal.ReadMouseShape());
    }

}
