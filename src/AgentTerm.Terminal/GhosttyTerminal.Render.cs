using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentTerm.Terminal;

public sealed partial class GhosttyTerminal
{
    private static readonly string[] AsciiText = Enumerable.Range(0, 128).Select(value => ((char)value).ToString()).ToArray();
    private RenderHandle? _render;
    private RenderRowsHandle? _renderRows;
    private RenderCellsHandle? _renderCells;
    // Borrowed from the handles above, which own their lifetime.
    private IntPtr _renderState, _rowIterator, _cellIterator;
    private TerminalFrame? _frame;
    private long? _holdStarted;
    private long? _frameRefreshAt;
    private bool _frameReverse;

    /// <summary>Remaining delay before a synchronized-output hold must be recaptured, or null when no refresh is pending.</summary>
    public TimeSpan? PendingFrameRefreshDelay => _frameRefreshAt is { } deadline
        ? TimeSpan.FromMilliseconds(Math.Max(0, deadline - Environment.TickCount64)) : null;

    /// <summary>Captures an owned snapshot of the visible terminal grid.</summary>
    public TerminalFrame ReadFrame() => CompleteFrame(BeginFrame());

    /// <summary>
    /// First phase of <see cref="ReadFrame"/>. Copies terminal state into the render state, so it needs the same
    /// exclusion as any terminal call. Pass the result to <see cref="CompleteFrame"/>, and keep other render-state
    /// users (<see cref="ReadFrame"/>, scrolling, anchors, presented viewport reads, disposal) out until it returns.
    /// </summary>
    internal FrameCapture BeginFrame()
    {
        ThrowIfDisposed();
        _frameRefreshAt = null;
        EnsureRender();
        if (ReadMode(2026))
        {
            _holdStarted ??= Environment.TickCount64;
            if (_frame is not null && _viewRevision == _capturedViewRevision && Environment.TickCount64 - _holdStarted.Value < 1000)
            {
                _frameRefreshAt = _holdStarted.Value + 1000;
                return new FrameCapture(_frame, default, false, false, 0, null, false);
            }
        }
        else _holdStarted = null;
        EnsurePresentation();
        ConfigureOverscan(_overscanEnabled);
        var capture = new FrameCapture(null, ReadViewport(), HasSelection(), ReadMode(5), _viewRevision, _theme, _overscanEnabled) { ContentVersion = ContentVersion };
        ThrowIfFailed(RenderNative.ghostty_render_state_begin_update(_renderState, _raw), "Could not update render state.");
        return capture;
    }

    /// <summary>Second phase of <see cref="ReadFrame"/>. Reads only the render state, so terminal writes may run meanwhile.</summary>
    internal unsafe TerminalFrame CompleteFrame(FrameCapture capture)
    {
        if (capture.Held is { } held) return held;
        ThrowIfFailed(RenderNative.ghostty_render_state_end_update(_renderState), "Could not update render state.");
        var (viewport, hasSelection, reverse, current) = (capture.Viewport, capture.HasSelection, capture.Reverse, capture.ViewRevision == _capturedViewRevision);
        int dirty = 0;
        StateGet(3, &dirty);

        ushort columns = 0, rowsCount = 0;
        Rgb foreground = default, background = default, cursorColor = default;
        StateGet(1, &columns); StateGet(2, &rowsCount);
        StateGet(6, &foreground); StateGet(5, &background);
        var defaultFg = foreground.ToColor();
        var defaultBg = background.ToColor();
        byte visible = 0, blink = 0, hasPosition = 0, wideTail = 0, password = 0;
        Rgb* palette = stackalloc Rgb[256];
        StateGet(9, palette);
        StateGet(13, &password);
        ushort x = 0, y = 0; int cursorStyle = 0;
        StateGet(11, &visible); StateGet(12, &blink); StateGet(14, &hasPosition); StateGet(10, &cursorStyle);
        if (hasPosition != 0) { StateGet(15, &x); StateGet(16, &y); StateGet(17, &wideTail); }
        var cursorResult = RenderNative.ghostty_render_state_get(_renderState, 7, &cursorColor);
        if (cursorResult != 0 && cursorResult != -4) ThrowIfFailed(cursorResult, "Could not read cursor color.");
        var cursor = new TerminalCursor(x, y, visible != 0 && hasPosition != 0, blink != 0,
            cursorStyle == 0 ? TerminalCursorShape.Bar : cursorStyle == 2 ? TerminalCursorShape.Underline : cursorStyle == 3 ? TerminalCursorShape.Hollow : TerminalCursorShape.Block,
            cursorResult == 0 ? cursorColor.ToColor() : reverse ? defaultBg : defaultFg) { WideTail = wideTail != 0, PasswordInput = password != 0 };
        if (dirty == 0 && _frame is not null && current && viewport == _frame.Viewport && hasSelection == _frame.HasSelection && reverse == _frameReverse && cursor == _frame.Cursor &&
            (reverse ? defaultBg : defaultFg) == _frame.Foreground && (reverse ? defaultFg : defaultBg) == _frame.Background)
            return _frame.ContentVersion == capture.ContentVersion ? _frame : _frame = _frame with { ContentVersion = capture.ContentVersion };
        var previous = _frame;
        var captureAll = capture.OverscanEnabled || dirty == 2 || previous is null || previous.Columns != columns || previous.Rows != rowsCount ||
            previous.Viewport != viewport || previous.HasSelection != hasSelection || reverse != _frameReverse ||
            (reverse ? defaultBg : defaultFg) != previous.Foreground || (reverse ? defaultFg : defaultBg) != previous.Background ||
            (!current && hasSelection);
        var sameShape = previous is not null && previous.Columns == columns && previous.Rows == rowsCount;
        var cellsArray = captureAll ? new TerminalCell[columns * rowsCount] : previous!.Cells;
        var cellsChanged = captureAll;
        var rowPtr = _rowIterator;
        StateGet(4, &rowPtr);
        byte* scratch = stackalloc byte[64];
        var capturedRows = 0;
        var visibleRows = 0;
        var above = new List<TerminalOverscanRow>();
        var below = new List<TerminalOverscanRow>();
        while (true)
        {
            int row;
            if (captureAll)
            {
                if (RenderNative.ghostty_render_state_row_iterator_next(_rowIterator) == 0) break;
                row = 0;
                if (capture.OverscanEnabled) RowGet(6, &row); else row = capturedRows;
                capturedRows++;
            }
            else
            {
                ushort dirtyRow = 0;
                if (RenderNative.ghostty_render_state_row_iterator_next_dirty(_rowIterator, &dirtyRow) == 0) break;
                row = dirtyRow;
            }
            if (row < -1 || row > rowsCount) throw new InvalidOperationException("Render state returned an unexpected overscan row.");
            var extra = row < 0 || row >= rowsCount;
            if (!extra) visibleRows++;
            if (!cellsChanged)
            {
                cellsArray = (TerminalCell[])cellsArray.Clone();
                cellsChanged = true;
            }
            var rowCells = extra ? new TerminalCell[columns] : cellsArray;
            var cellPtr = _cellIterator;
            RowGet(3, &cellPtr);
            var index = extra ? 0 : row * columns;
            var end = index + columns;
            while (RenderNative.ghostty_render_state_row_cells_next(_cellIterator) != 0)
            {
                if (index == end) throw new InvalidOperationException("Render state returned an incomplete grid.");
                ulong raw = 0;
                Style style = default;
                int wide = 0;
                CellGet(1, &raw); CellGet(2, &style);
                ThrowIfFailed(RenderNative.ghostty_cell_get(raw, 3, &wide), "Could not read cell width.");
                var (fg, bg) = CellColors(defaultFg, defaultBg, ((style.Flags & 16) != 0) ^ reverse);
                var old = !extra && sameShape ? previous!.Cells[index] : null;
                var buffer = new ByteBuffer { Pointer = scratch, Capacity = 64 };
                var result = RenderNative.ghostty_render_state_row_cells_get(_cellIterator, 9, &buffer);
                string text;
                if (result == 0) text = CellText(new ReadOnlySpan<byte>(scratch, checked((int)buffer.Length)), old?.Text);
                else
                {
                    if (result != -3) ThrowIfFailed(result, "Could not read cell text.");
                    var bytes = new byte[checked((int)buffer.Length)];
                    fixed (byte* ptr = bytes)
                    {
                        buffer = new ByteBuffer { Pointer = ptr, Capacity = (nuint)bytes.Length };
                        CellGet(9, &buffer);
                    }
                    text = CellText(bytes, old?.Text);
                }
                byte selected = 0;
                if (hasSelection) CellGet(7, &selected);
                var width = (byte)(wide == 1 ? 2 : wide >= 2 ? 0 : 1);
                bool bold = (style.Flags & 1) != 0, italic = (style.Flags & 2) != 0, underline = (style.Flags & 0x700) != 0,
                    strikethrough = (style.Flags & 64) != 0, dim = (style.Flags & 4) != 0, hidden = (style.Flags & 32) != 0;
                var underlineStyle = (TerminalUnderlineStyle)((style.Flags >> 8) & 7);
                TerminalColor? underlineColor = style.UnderlineTag == 1 ? palette[style.UnderlineR].ToColor() : style.UnderlineTag == 2 ? new TerminalColor(style.UnderlineR, style.UnderlineG, style.UnderlineB) : null;
                bool overline = (style.Flags & 128) != 0, textBlink = (style.Flags & 8) != 0;
                rowCells[index++] = old is not null && old.Text == text && old.Width == width && old.Foreground == fg && old.Background == bg &&
                    old.Bold == bold && old.Italic == italic && old.Underline == underline && old.Strikethrough == strikethrough &&
                    old.Dim == dim && old.Hidden == hidden && old.Selected == (selected != 0) && old.UnderlineStyle == underlineStyle &&
                    old.UnderlineColor == underlineColor && old.Overline == overline && old.Blink == textBlink
                    ? old
                    : new TerminalCell(text, width, fg, bg, bold, italic, underline, strikethrough, dim, hidden) { Selected = selected != 0, UnderlineStyle = underlineStyle, UnderlineColor = underlineColor, Overline = overline, Blink = textBlink };
            }
            if (index != end) throw new InvalidOperationException("Render state returned an incomplete grid.");
            if (extra)
            {
                for (var i = 0; i + 1 < rowCells.Length; i++)
                    if (rowCells[i].Width == 2 && rowCells[i].Selected && rowCells[i + 1].Width == 0)
                        rowCells[i + 1] = rowCells[i + 1] with { Selected = true };
                (row < 0 ? above : below).Add(new TerminalOverscanRow(row, rowCells));
            }
        }
        if (captureAll && visibleRows != rowsCount) throw new InvalidOperationException("Render state returned an incomplete grid.");
        for (var i = 0; cellsChanged && i + 1 < cellsArray.Length; i++)
            if (cellsArray[i].Width == 2 && cellsArray[i].Selected && cellsArray[i + 1].Width == 0)
                cellsArray[i + 1] = cellsArray[i + 1] with { Selected = true };
        if (reverse) (defaultFg, defaultBg) = (defaultBg, defaultFg);
        _frameReverse = reverse;
        _frame = new TerminalFrame(columns, rowsCount, cellsArray, cursor, defaultFg, defaultBg) {
            Viewport = viewport, HasSelection = hasSelection, ContentVersion = capture.ContentVersion,
            OverscanAbove = above.ToArray(), OverscanBelow = below.ToArray(),
            SelectionBackground = (capture.Theme ?? TerminalThemes.All[0]).SelectionBackground,
            SelectionForeground = (capture.Theme ?? TerminalThemes.All[0]).SelectionForeground,
            CursorText = capture.Theme is null ? null : capture.Theme.CursorText ?? background.ToColor(),
        };
        _capturedViewRevision = capture.ViewRevision;
        ThrowIfFailed(RenderNative.ghostty_render_state_clean(_renderState), "Could not clear render dirty state.");
        return _frame;
    }

    private void EnsureRender()
    {
        if (_render is null)
        {
            ThrowIfFailed(RenderNative.ghostty_render_state_new(IntPtr.Zero, out var state), "Could not create render state.");
            _render = new RenderHandle(state);
            _renderState = state;
        }
        if (_renderRows is null)
        {
            ThrowIfFailed(RenderNative.ghostty_render_state_row_iterator_new(IntPtr.Zero, out var rows), "Could not create render row iterator.");
            _renderRows = new RenderRowsHandle(rows);
            _rowIterator = rows;
        }
        if (_renderCells is null)
        {
            ThrowIfFailed(RenderNative.ghostty_render_state_row_cells_new(IntPtr.Zero, out var cells), "Could not create render cell iterator.");
            _renderCells = new RenderCellsHandle(cells);
            _cellIterator = cells;
        }
    }

    /// <summary>
    /// The lowest active row at or below the cursor that a frame would draw. Updates the render state without
    /// cleaning it, so the next frame still sees every change.
    /// </summary>
    private unsafe int LastContentRow()
    {
        EnsureRender();
        ConfigureOverscan(false);
        var last = ReadActiveCursorRow();
        var reverse = ReadMode(5);
        ThrowIfFailed(RenderNative.ghostty_render_state_update(_renderState, _raw), "Could not update render state.");
        Rgb foreground = default, background = default;
        StateGet(6, &foreground); StateGet(5, &background);
        TerminalColor defaultFg = foreground.ToColor(), defaultBg = background.ToColor(), frameBg = reverse ? defaultFg : defaultBg;
        var rowPtr = _rowIterator;
        StateGet(4, &rowPtr);
        byte* scratch = stackalloc byte[64];
        for (var row = 0; RenderNative.ghostty_render_state_row_iterator_next(_rowIterator) != 0; row++)
        {
            if (row <= last) continue;
            var cellPtr = _cellIterator;
            RowGet(3, &cellPtr);
            while (RenderNative.ghostty_render_state_row_cells_next(_cellIterator) != 0)
            {
                ulong raw = 0; int tag = 0; ushort styleId = 0; uint codepoint = 0;
                CellGet(1, &raw);
                ThrowIfFailed(RenderNative.ghostty_cell_get(raw, 2, &tag), "Could not read cell content.");
                ThrowIfFailed(RenderNative.ghostty_cell_get(raw, 6, &styleId), "Could not read cell style.");
                ThrowIfFailed(RenderNative.ghostty_cell_get(raw, 1, &codepoint), "Could not read cell codepoint.");
                if (tag == 0 && styleId == 0 && (codepoint == 0 || codepoint <= char.MaxValue && char.IsWhiteSpace((char)codepoint))) continue;
                Style style = default;
                CellGet(2, &style);
                if ((style.Flags & (0x700 | 64 | 128)) != 0 || CellColors(defaultFg, defaultBg, ((style.Flags & 16) != 0) ^ reverse).Bg != frameBg) { last = row; break; }
                var buffer = new ByteBuffer { Pointer = scratch, Capacity = 64 };
                var result = RenderNative.ghostty_render_state_row_cells_get(_cellIterator, 9, &buffer);
                if (result == -3) { last = row; break; }
                ThrowIfFailed(result, "Could not read cell text.");
                if (!string.IsNullOrWhiteSpace(CellText(new ReadOnlySpan<byte>(scratch, checked((int)buffer.Length)), null))) { last = row; break; }
            }
        }
        return last;
    }

    private unsafe (TerminalColor Fg, TerminalColor Bg) CellColors(TerminalColor fg, TerminalColor bg, bool inverse)
    {
        Rgb color = default;
        var result = RenderNative.ghostty_render_state_row_cells_get(_cellIterator, 6, &color);
        if (result == 0) fg = color.ToColor(); else if (result != -4) ThrowIfFailed(result, "Could not read cell foreground.");
        result = RenderNative.ghostty_render_state_row_cells_get(_cellIterator, 5, &color);
        if (result == 0) bg = color.ToColor(); else if (result != -4) ThrowIfFailed(result, "Could not read cell background.");
        return inverse ? (bg, fg) : (fg, bg);
    }

    /// <summary>Decodes cell text, returning <paramref name="previous"/> when it already holds the same characters.</summary>
    private static string CellText(ReadOnlySpan<byte> utf8, string? previous)
    {
        if (utf8.Length == 1 && utf8[0] < AsciiText.Length) return AsciiText[utf8[0]];
        if (utf8.IsEmpty) return string.Empty;
        Span<char> chars = utf8.Length <= 256 ? stackalloc char[utf8.Length] : new char[utf8.Length];
        chars = chars[..Encoding.UTF8.GetChars(utf8, chars)];
        return previous is not null && chars.SequenceEqual(previous) ? previous : new string(chars);
    }

    private unsafe bool ReadMode(ushort mode)
    {
        var value = new Mode { Value = mode };
        ThrowIfFailed(RenderNative.ghostty_terminal_get(_raw, 37, &value), "Could not read terminal mode.");
        return value.Enabled != 0;
    }
    private unsafe void StateGet(int key, void* value) => ThrowIfFailed(RenderNative.ghostty_render_state_get(_renderState, key, value), "Could not read render state.");
    private unsafe void RowGet(int key, void* value) => ThrowIfFailed(RenderNative.ghostty_render_state_row_get(_rowIterator, key, value), "Could not read render row.");
    private unsafe void CellGet(int key, void* value) => ThrowIfFailed(RenderNative.ghostty_render_state_row_cells_get(_cellIterator, key, value), "Could not read render cell.");
    private void DisposeRender()
    {
        _renderState = _rowIterator = _cellIterator = IntPtr.Zero;
        _renderCells?.Dispose(); _renderRows?.Dispose(); _render?.Dispose();
    }

    internal readonly record struct FrameCapture(TerminalFrame? Held, TerminalViewport Viewport, bool HasSelection, bool Reverse, long ViewRevision, TerminalTheme? Theme, bool OverscanEnabled)
    {
        public long ContentVersion { get; init; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Mode { public ushort Value; public byte Enabled; }
    [StructLayout(LayoutKind.Sequential)] private struct Rgb { public byte R, G, B; public readonly TerminalColor ToColor() => new(R, G, B); }
    [StructLayout(LayoutKind.Explicit, Size = 16)] private struct Style { [FieldOffset(8)] public byte UnderlineTag; [FieldOffset(9)] public byte UnderlineR; [FieldOffset(10)] public byte UnderlineG; [FieldOffset(11)] public byte UnderlineB; [FieldOffset(12)] public ushort Flags; }
    [StructLayout(LayoutKind.Sequential)] private unsafe struct ByteBuffer { public byte* Pointer; public nuint Capacity, Length; }
    private sealed class RenderHandle : SafeHandleZeroOrMinusOneIsInvalid { public RenderHandle(IntPtr value) : base(true) => SetHandle(value); protected override bool ReleaseHandle() { RenderNative.ghostty_render_state_free(handle); return true; } }
    private sealed class RenderRowsHandle : SafeHandleZeroOrMinusOneIsInvalid { public RenderRowsHandle(IntPtr value) : base(true) => SetHandle(value); protected override bool ReleaseHandle() { RenderNative.ghostty_render_state_row_iterator_free(handle); return true; } }
    private sealed class RenderCellsHandle : SafeHandleZeroOrMinusOneIsInvalid { public RenderCellsHandle(IntPtr value) : base(true) => SetHandle(value); protected override bool ReleaseHandle() { RenderNative.ghostty_render_state_row_cells_free(handle); return true; } }

    // SuppressGCTransition marks getters that only copy render or terminal state. They never block or call back.
    private static unsafe partial class RenderNative
    {
        private const string Lib = "ghostty-vt";
        [LibraryImport(Lib), SuppressGCTransition] internal static partial int ghostty_terminal_get(IntPtr terminal, int key, void* value);
        [LibraryImport(Lib)] internal static partial int ghostty_render_state_new(IntPtr allocator, out IntPtr state);
        [LibraryImport(Lib)] internal static partial void ghostty_render_state_free(IntPtr state);
        [LibraryImport(Lib)] internal static partial int ghostty_render_state_update(IntPtr state, IntPtr terminal);
        [LibraryImport(Lib)] internal static partial int ghostty_render_state_begin_update(IntPtr state, IntPtr terminal);
        [LibraryImport(Lib)] internal static partial int ghostty_render_state_end_update(IntPtr state);
        [LibraryImport(Lib), SuppressGCTransition] internal static partial int ghostty_render_state_get(IntPtr state, int key, void* value);
        [LibraryImport(Lib), SuppressGCTransition] internal static partial int ghostty_render_state_clean(IntPtr state);
        [LibraryImport(Lib)] internal static partial int ghostty_render_state_row_iterator_new(IntPtr allocator, out IntPtr rows);
        [LibraryImport(Lib)] internal static partial void ghostty_render_state_row_iterator_free(IntPtr rows);
        [LibraryImport(Lib), SuppressGCTransition] internal static partial byte ghostty_render_state_row_iterator_next(IntPtr rows);
        [LibraryImport(Lib), SuppressGCTransition] internal static partial byte ghostty_render_state_row_iterator_next_dirty(IntPtr rows, ushort* y);
        [LibraryImport(Lib), SuppressGCTransition] internal static partial int ghostty_render_state_row_get(IntPtr rows, int key, void* value);
        [LibraryImport(Lib)] internal static partial int ghostty_render_state_row_cells_new(IntPtr allocator, out IntPtr cells);
        [LibraryImport(Lib)] internal static partial void ghostty_render_state_row_cells_free(IntPtr cells);
        [LibraryImport(Lib), SuppressGCTransition] internal static partial byte ghostty_render_state_row_cells_next(IntPtr cells);
        [LibraryImport(Lib), SuppressGCTransition] internal static partial int ghostty_render_state_row_cells_get(IntPtr cells, int key, void* value);
        [LibraryImport(Lib), SuppressGCTransition] internal static partial int ghostty_cell_get(ulong cell, int key, void* value);
    }
}
