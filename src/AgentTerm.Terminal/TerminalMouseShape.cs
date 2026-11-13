namespace AgentTerm.Terminal;

/// <summary>The W3C cursor names libghostty-vt reports for OSC 22. Values match GhosttyMouseShape.</summary>
public enum TerminalMouseShape
{
    Default = 0, ContextMenu = 1, Help = 2, Pointer = 3, Progress = 4, Wait = 5, Cell = 6, Crosshair = 7, Text = 8,
    VerticalText = 9, Alias = 10, Copy = 11, Move = 12, NoDrop = 13, NotAllowed = 14, Grab = 15, Grabbing = 16,
    AllScroll = 17, ColResize = 18, RowResize = 19, NResize = 20, EResize = 21, SResize = 22, WResize = 23,
    NeResize = 24, NwResize = 25, SeResize = 26, SwResize = 27, EwResize = 28, NsResize = 29, NeswResize = 30,
    NwseResize = 31, ZoomIn = 32, ZoomOut = 33
}

/// <summary>Pointer kinds a Windows host can draw. Names match the WinUI system cursor shapes.</summary>
public enum TerminalPointerKind
{
    Arrow, IBeam, Hand, Help, Wait, AppStarting, Cross, SizeAll, SizeWestEast, SizeNorthSouth,
    SizeNortheastSouthwest, SizeNorthwestSoutheast, UniversalNo
}

public static class TerminalMouseShapes
{
    /// <summary>The shape a terminal reports before any program asks for one.</summary>
    public const TerminalMouseShape Initial = TerminalMouseShape.Text;

    /// <summary>Converts a native value. A value this build does not know falls back to the plain arrow.</summary>
    public static TerminalMouseShape FromNative(int value) =>
        Enum.IsDefined((TerminalMouseShape)value) ? (TerminalMouseShape)value : TerminalMouseShape.Default;

    /// <summary>
    /// Maps a requested shape to the nearest pointer Windows can draw. Windows has no vertical text, zoom, copy,
    /// alias or grab pointers. Those use the closest one that still says what the pointer can do.
    /// </summary>
    public static TerminalPointerKind ToPointer(TerminalMouseShape shape) => shape switch
    {
        TerminalMouseShape.Text or TerminalMouseShape.VerticalText => TerminalPointerKind.IBeam,
        TerminalMouseShape.Pointer or TerminalMouseShape.Grab or TerminalMouseShape.Grabbing or TerminalMouseShape.Alias
            or TerminalMouseShape.Copy => TerminalPointerKind.Hand,
        TerminalMouseShape.Help => TerminalPointerKind.Help,
        TerminalMouseShape.Progress => TerminalPointerKind.AppStarting,
        TerminalMouseShape.Wait => TerminalPointerKind.Wait,
        TerminalMouseShape.Cell or TerminalMouseShape.Crosshair => TerminalPointerKind.Cross,
        TerminalMouseShape.Move or TerminalMouseShape.AllScroll => TerminalPointerKind.SizeAll,
        TerminalMouseShape.ColResize or TerminalMouseShape.EResize or TerminalMouseShape.WResize
            or TerminalMouseShape.EwResize => TerminalPointerKind.SizeWestEast,
        TerminalMouseShape.RowResize or TerminalMouseShape.NResize or TerminalMouseShape.SResize
            or TerminalMouseShape.NsResize => TerminalPointerKind.SizeNorthSouth,
        TerminalMouseShape.NeResize or TerminalMouseShape.SwResize or TerminalMouseShape.NeswResize =>
            TerminalPointerKind.SizeNortheastSouthwest,
        TerminalMouseShape.NwResize or TerminalMouseShape.SeResize or TerminalMouseShape.NwseResize =>
            TerminalPointerKind.SizeNorthwestSoutheast,
        TerminalMouseShape.NoDrop or TerminalMouseShape.NotAllowed => TerminalPointerKind.UniversalNo,
        _ => TerminalPointerKind.Arrow
    };
}
