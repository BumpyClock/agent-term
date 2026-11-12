using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentTerm.Terminal;

public sealed partial class GhosttyTerminal
{
    private RenderHandle? _render;
    private RenderRowsHandle? _renderRows;
    private RenderCellsHandle? _renderCells;
    private TerminalFrame? _frame;
    private long? _holdStarted;
    private bool _frameReverse;

    /// <summary>Captures an owned snapshot of the visible terminal grid.</summary>
    public unsafe TerminalFrame ReadFrame()
    {
        ThrowIfDisposed();
        if (_render is null)
        {
            ThrowIfFailed(RenderNative.ghostty_render_state_new(IntPtr.Zero, out var state), "Could not create render state.");
            _render = new RenderHandle(state);
        }
        if (_renderRows is null)
        {
            ThrowIfFailed(RenderNative.ghostty_render_state_row_iterator_new(IntPtr.Zero, out var rows), "Could not create render row iterator.");
            _renderRows = new RenderRowsHandle(rows);
        }
        if (_renderCells is null)
        {
            ThrowIfFailed(RenderNative.ghostty_render_state_row_cells_new(IntPtr.Zero, out var cells), "Could not create render cell iterator.");
            _renderCells = new RenderCellsHandle(cells);
        }

        // A held update must not consume terminal dirty state. Resize ends the hold.
        if (ReadMode(2026))
        {
            _holdStarted ??= Environment.TickCount64;
            if (_frame is not null && _viewRevision == _capturedViewRevision && Environment.TickCount64 - _holdStarted.Value < 1000) return _frame;
        }
        else _holdStarted = null;
        var viewport = ReadViewport();
        var hasSelection = HasSelection();
        var reverse = ReadMode(5);
        ThrowIfFailed(RenderNative.ghostty_render_state_update(_render, _handle), "Could not update render state.");
        int dirty = 0;
        StateGet(3, &dirty);

        ushort columns = 0, rowsCount = 0;
        Rgb foreground = default, background = default, cursorColor = default;
        StateGet(1, &columns); StateGet(2, &rowsCount);
        StateGet(6, &foreground); StateGet(5, &background);
        var defaultFg = foreground.ToColor();
        var defaultBg = background.ToColor();
        byte visible = 0, blink = 0, hasPosition = 0;
        ushort x = 0, y = 0; int cursorStyle = 0;
        StateGet(11, &visible); StateGet(12, &blink); StateGet(14, &hasPosition); StateGet(10, &cursorStyle);
        if (hasPosition != 0) { StateGet(15, &x); StateGet(16, &y); }
        var cursorResult = RenderNative.ghostty_render_state_get(_render, 7, &cursorColor);
        if (cursorResult != 0 && cursorResult != -4) ThrowIfFailed(cursorResult, "Could not read cursor color.");
        var cursor = new TerminalCursor(x, y, visible != 0 && hasPosition != 0, blink != 0,
            cursorStyle == 0 ? TerminalCursorShape.Bar : cursorStyle == 2 ? TerminalCursorShape.Underline : TerminalCursorShape.Block,
            cursorResult == 0 ? cursorColor.ToColor() : reverse ? defaultBg : defaultFg);
        if (dirty == 0 && _frame is not null && _viewRevision == _capturedViewRevision && viewport == _frame.Viewport && hasSelection == _frame.HasSelection && reverse == _frameReverse && cursor == _frame.Cursor &&
            (reverse ? defaultBg : defaultFg) == _frame.Foreground && (reverse ? defaultFg : defaultBg) == _frame.Background) return _frame;
        var cellsArray = new TerminalCell[columns * rowsCount];
        var rowHandle = _renderRows!;
        var cellHandle = _renderCells!;
        var rowPtr = rowHandle.DangerousGetHandle();
        StateGet(4, &rowPtr);
        var index = 0;
        byte* scratch = stackalloc byte[64];
        while (RenderNative.ghostty_render_state_row_iterator_next(rowHandle) != 0)
        {
            var cellPtr = cellHandle.DangerousGetHandle();
            ThrowIfFailed(RenderNative.ghostty_render_state_row_get(rowHandle, 3, &cellPtr), "Could not read render row.");
            while (RenderNative.ghostty_render_state_row_cells_next(cellHandle) != 0)
            {
                ulong raw = 0;
                Style style = default;
                int wide = 0;
                CellGet(1, &raw); CellGet(2, &style);
                ThrowIfFailed(RenderNative.ghostty_cell_get(raw, 3, &wide), "Could not read cell width.");
                var fg = defaultFg; var bg = defaultBg;
                Rgb color = default;
                var result = RenderNative.ghostty_render_state_row_cells_get(cellHandle, 6, &color);
                if (result == 0) fg = color.ToColor(); else if (result != -4) ThrowIfFailed(result, "Could not read cell foreground.");
                result = RenderNative.ghostty_render_state_row_cells_get(cellHandle, 5, &color);
                if (result == 0) bg = color.ToColor(); else if (result != -4) ThrowIfFailed(result, "Could not read cell background.");
                if (((style.Flags & 16) != 0) ^ reverse) (fg, bg) = (bg, fg);

                var buffer = new ByteBuffer { Pointer = scratch, Capacity = 64 };
                result = RenderNative.ghostty_render_state_row_cells_get(cellHandle, 9, &buffer);
                string text;
                if (result == 0) text = Encoding.UTF8.GetString(scratch, checked((int)buffer.Length));
                else
                {
                    if (result != -3) ThrowIfFailed(result, "Could not read cell text.");
                    // The native API reports the exact size when the cluster exceeds the stack buffer.
                    var bytes = new byte[checked((int)buffer.Length)];
                    fixed (byte* ptr = bytes)
                    {
                        buffer = new ByteBuffer { Pointer = ptr, Capacity = (nuint)bytes.Length };
                        CellGet(9, &buffer);
                        text = Encoding.UTF8.GetString(bytes);
                    }
                }
                byte selected = 0;
                CellGet(7, &selected);
                cellsArray[index++] = new TerminalCell(text, (byte)(wide == 1 ? 2 : wide >= 2 ? 0 : 1), fg, bg,
                    (style.Flags & 1) != 0, (style.Flags & 2) != 0, (style.Flags & 0x700) != 0,
                    (style.Flags & 64) != 0, (style.Flags & 4) != 0, (style.Flags & 32) != 0) { Selected = selected != 0 };
            }
        }
        if (index != cellsArray.Length) throw new InvalidOperationException("Render state returned an incomplete grid.");
        for (var i = 0; i + 1 < cellsArray.Length; i++)
            if (cellsArray[i].Width == 2 && cellsArray[i].Selected && cellsArray[i + 1].Width == 0)
                cellsArray[i + 1] = cellsArray[i + 1] with { Selected = true };
        if (reverse) (defaultFg, defaultBg) = (defaultBg, defaultFg);
        _frameReverse = reverse;
        _frame = new TerminalFrame(columns, rowsCount, cellsArray, cursor, defaultFg, defaultBg) {
            Viewport = viewport, HasSelection = hasSelection,
            SelectionBackground = (_theme ?? TerminalThemes.All[0]).SelectionBackground,
            SelectionForeground = (_theme ?? TerminalThemes.All[0]).SelectionForeground,
            CursorText = _theme is null ? null : _theme.CursorText ?? background.ToColor(),
        };
        _capturedViewRevision = _viewRevision;
        ThrowIfFailed(RenderNative.ghostty_render_state_clean(_render), "Could not clear render dirty state.");
        return _frame;
    }

    private unsafe bool ReadMode(ushort mode)
    {
        var value = new Mode { Value = mode };
        ThrowIfFailed(RenderNative.ghostty_terminal_get(_handle, 37, &value), "Could not read terminal mode.");
        return value.Enabled != 0;
    }
    private unsafe void StateGet(int key, void* value) => ThrowIfFailed(RenderNative.ghostty_render_state_get(_render!, key, value), "Could not read render state.");
    private unsafe void CellGet(int key, void* value) => ThrowIfFailed(RenderNative.ghostty_render_state_row_cells_get(_renderCells!, key, value), "Could not read render cell.");
    private void DisposeRender() { _renderCells?.Dispose(); _renderRows?.Dispose(); _render?.Dispose(); }

    [StructLayout(LayoutKind.Sequential)] private struct Mode { public ushort Value; public byte Enabled; }
    [StructLayout(LayoutKind.Sequential)] private struct Rgb { public byte R, G, B; public readonly TerminalColor ToColor() => new(R, G, B); }
    [StructLayout(LayoutKind.Explicit, Size = 16)] private struct Style { [FieldOffset(12)] public ushort Flags; }
    [StructLayout(LayoutKind.Sequential)] private unsafe struct ByteBuffer { public byte* Pointer; public nuint Capacity, Length; }
    private sealed class RenderHandle : SafeHandleZeroOrMinusOneIsInvalid { public RenderHandle(IntPtr value) : base(true) => SetHandle(value); protected override bool ReleaseHandle() { RenderNative.ghostty_render_state_free(handle); return true; } }
    private sealed class RenderRowsHandle : SafeHandleZeroOrMinusOneIsInvalid { public RenderRowsHandle(IntPtr value) : base(true) => SetHandle(value); protected override bool ReleaseHandle() { RenderNative.ghostty_render_state_row_iterator_free(handle); return true; } }
    private sealed class RenderCellsHandle : SafeHandleZeroOrMinusOneIsInvalid { public RenderCellsHandle(IntPtr value) : base(true) => SetHandle(value); protected override bool ReleaseHandle() { RenderNative.ghostty_render_state_row_cells_free(handle); return true; } }
    private static unsafe class RenderNative
    {
        private const string Lib = "ghostty-vt";
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_terminal_get(TerminalHandle terminal, int key, void* value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_render_state_new(IntPtr allocator, out IntPtr state);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void ghostty_render_state_free(IntPtr state);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_render_state_update(RenderHandle state, TerminalHandle terminal);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_render_state_get(RenderHandle state, int key, void* value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_render_state_clean(RenderHandle state);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_render_state_row_iterator_new(IntPtr allocator, out IntPtr rows);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void ghostty_render_state_row_iterator_free(IntPtr rows);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern byte ghostty_render_state_row_iterator_next(RenderRowsHandle rows);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_render_state_row_get(RenderRowsHandle rows, int key, void* value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_render_state_row_cells_new(IntPtr allocator, out IntPtr cells);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void ghostty_render_state_row_cells_free(IntPtr cells);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern byte ghostty_render_state_row_cells_next(RenderCellsHandle cells);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_render_state_row_cells_get(RenderCellsHandle cells, int key, void* value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_cell_get(ulong cell, int key, void* value);
    }
}
