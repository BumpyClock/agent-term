using System.Runtime.InteropServices;

namespace AgentTerm.Terminal;

public enum TerminalSelectionAdjustment
{
    Left, Right, Up, Down, Home, End, PageUp, PageDown, BeginningOfLine, EndOfLine
}

public sealed partial class GhosttyTerminal
{
    public unsafe void BeginKeyboardSelection()
    {
        ThrowIfDisposed();
        EnsurePresentation();
        ushort column = 0, row = 0;
        var viewport = ReadViewport();
        if (viewport.IsAtBottom)
        {
            ThrowIfFailed(RenderNative.ghostty_terminal_get(_handle, 3, &column), "Could not read cursor column.");
            ThrowIfFailed(RenderNative.ghostty_terminal_get(_handle, 4, &row), "Could not read cursor row.");
            row = (ushort)Math.Clamp(ActiveTop + row - viewport.Offset, 0, _rows - 1);
        }
        ClearSelection();
        ThrowIfFailed(Native.ghostty_terminal_grid_ref(_handle,
            ViewportPoint(Math.Min(column, _columns - 1), Math.Min(row, _rows - 1)), out var reference),
            "Could not find keyboard selection anchor.");
        var selection = new Native.Selection { Start = reference, End = reference };
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 22, (IntPtr)(&selection)), "Could not start keyboard selection.");
        _selectionScreen = ActiveScreen();
        _selectionRectangle = false;
        _viewRevision++;
        CaptureSelectedViewport();
    }

    public unsafe void AdjustSelection(TerminalSelectionAdjustment adjustment)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(adjustment)) throw new ArgumentOutOfRangeException(nameof(adjustment));
        EnsurePresentation();
        CheckSelectionScreen();
        if (!HasSelection()) BeginKeyboardSelection();

        // Read and consume native snapshot refs within this mutation. Never keep
        // their page pointers across output, resize, scrolling, or selection writes.
        Native.Selection selection = default;
        ThrowIfFailed(RenderNative.ghostty_terminal_get(_handle, (int)TerminalData.Selection, &selection),
            "Could not read keyboard selection.");
        ThrowIfFailed(SelectionNative.ghostty_terminal_selection_adjust(_handle, ref selection, adjustment),
            "Could not adjust keyboard selection.");
        ThrowIfFailed(SelectionNative.ghostty_terminal_point_from_grid_ref(_handle, in selection.End, 2, out var end),
            "Could not locate keyboard selection endpoint.");
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 22, (IntPtr)(&selection)), "Could not set keyboard selection.");
        _selecting = false;
        _selectionScreen = ActiveScreen();
        _selectionRectangle = selection.Rectangle != 0;
        var viewport = ReadViewport();
        long target = end.Y < viewport.Offset ? end.Y :
            end.Y >= viewport.Offset + viewport.Rows ? (long)end.Y - viewport.Rows + 1 : viewport.Offset;
        ScrollTo(target);
    }

    private static partial class SelectionNative
    {
        [LibraryImport("ghostty-vt")]
        internal static partial int ghostty_terminal_selection_adjust(TerminalHandle terminal,
            ref Native.Selection selection, TerminalSelectionAdjustment adjustment);
        [LibraryImport("ghostty-vt")]
        internal static partial int ghostty_terminal_point_from_grid_ref(TerminalHandle terminal,
            in Native.GridRef reference, int tag, out Native.PointValue point);
    }
}
