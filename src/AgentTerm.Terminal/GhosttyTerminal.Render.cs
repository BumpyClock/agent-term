using System.Text;

namespace AgentTerm.Terminal;

// Render ownership contract. The render state, its iterators, the retained frame and capture metadata are one
// lifetime unit. Render exclusion (TerminalSession._renderGate) serializes every operation that updates or traverses
// it. BeginFrame also needs terminal exclusion (_gate): it settles holds and presentation, records viewport, selection,
// reverse mode, theme and view revision, and copies the terminal with begin_update, all at one moment. CompleteFrame
// needs only render exclusion: end_update and every getter read render-owned memory, so output may keep parsing.
// _captureInFlight marks that window for the render-hold callback. Frames are owned managed data; no native pointer or
// borrowed span outlives a capture.
public sealed partial class GhosttyTerminal
{
    private static readonly string[] AsciiText = Enumerable.Range(0, 128).Select(value => ((char)value).ToString()).ToArray();
    private const int ScratchTextBytes = 64;
    private RenderHandle? _render;
    private RenderRowsHandle? _renderRows;
    private RenderCellsHandle? _renderCells;
    // Borrowed from the handles above. Use only inside a HandleLease on the owning handle.
    private IntPtr _renderState, _rowIterator, _cellIterator;
    private TerminalFrame? _frame;
    private long _frameRefreshAt;
    private bool _frameReverse;
    private bool _frameOverscanEnabled;
    private byte[]? _framePalette;
    private bool _captureInFlight;
    private int _captureFailures;

    /// <summary>Allocator for new render states. Zero selects libghostty's default. Tests inject allocation failure here.</summary>
    internal IntPtr RenderAllocator { get; set; }

    /// <summary>Captures that failed and were recovered by keeping the previous frame and rebuilding the render state.</summary>
    internal int CaptureFailures => Volatile.Read(ref _captureFailures);

    /// <summary>Remaining delay before a held or failed frame must be recaptured, or null when no refresh is pending.</summary>
    public TimeSpan? PendingFrameRefreshDelay
    {
        get
        {
            var frameAt = Volatile.Read(ref _frameRefreshAt);
            var imageAt = _holdActive ? null : ImageRefreshAt;
            var deadline = frameAt == 0 ? imageAt : imageAt is { } image ? Math.Min(frameAt, image) : frameAt;
            return deadline is { } due ? TimeSpan.FromMilliseconds(Math.Max(0, due - Environment.TickCount64)) : null;
        }
    }

    /// <summary>Captures an owned snapshot of the visible terminal grid.</summary>
    public TerminalFrame ReadFrame() => CompleteFrame(BeginFrame());

    /// <summary>
    /// First phase of <see cref="ReadFrame"/>. Copies terminal state into the render state, so it needs the same
    /// exclusion as any terminal call. Pass the result to <see cref="CompleteFrame"/>, and keep other render-state
    /// users (<see cref="ReadFrame"/>, scrolling, anchors, presented viewport reads, disposal) out until it returns.
    /// </summary>
    internal FrameCapture BeginFrame() => BeginFrameCore(prepareSearch: true);

    internal FrameCapture BeginFrameWithPreparedSearch() => BeginFrameCore(prepareSearch: false);

    private FrameCapture BeginFrameCore(bool prepareSearch)
    {
        ThrowIfDisposed();
        Volatile.Write(ref _frameRefreshAt, 0);
        try
        {
            AdoptHandoff();
            if (HeldCapture() is { } held)
            {
                if (held.Held is null)
                {
                    EnsureRender();
                    Volatile.Write(ref _captureInFlight, true);
                }
                return held;
            }
            EnsureRender();
            TickImageAnimations();
            EnsurePresentation();
            var capture = CaptureMetadata() with { SearchHighlights = prepareSearch ? PrepareSearchHighlightsSynchronously() : ReadPreparedSearchHighlights() };
            using (var lease = new HandleLease(_render))
            {
                ConfigureOverscan(lease.Raw, capture.OverscanEnabled);
                ThrowIfFailed(RenderNative.ghostty_render_state_begin_update(lease.Raw, _handle), "Could not update render state.");
            }
            capture = capture with { ImageCapture = CaptureImagePixels() };
            Volatile.Write(ref _captureInFlight, true);
            return capture;
        }
        catch (InvalidOperationException error) when (!_disposed) { return RecoverFailedCapture(error); }
    }

    /// <summary>Second phase of <see cref="ReadFrame"/>. Reads only the render state, so terminal writes may run meanwhile.</summary>
    internal TerminalFrame CompleteFrame(FrameCapture capture)
    {
        if (capture.Held is { } held) return held;
        try
        {
            using var state = new HandleLease(_render);
            using var rows = new HandleLease(_renderRows);
            using var cells = new HandleLease(_renderCells);
            ThrowIfFailed(RenderNative.ghostty_render_state_end_update(state.Raw), "Could not update render state.");
            var frame = Materialize(capture);
            _frameHoldGeneration = capture.HoldGeneration;
            ThrowIfFailed(RenderNative.ghostty_render_state_clean(state.Raw), "Could not clear render dirty state.");
            return frame;
        }
        catch (Exception error) when (error is InvalidOperationException && !_disposed)
        {
            return RecoverFailedCapture(error).Held!;
        }
        finally { Volatile.Write(ref _captureInFlight, false); }
    }

    /// <summary>
    /// A failed capture never publishes partial state. The render state is retired so the next capture rebuilds every
    /// row from the terminal, the previous complete frame stays on screen, and a retry is scheduled with backoff.
    /// </summary>
    private FrameCapture RecoverFailedCapture(Exception error)
    {
        RetireRender();
        var failures = Interlocked.Increment(ref _captureFailures);
        if (_frame is null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        Volatile.Write(ref _frameRefreshAt, Environment.TickCount64 + Math.Min(1000, 16L << Math.Min(6, failures - 1)));
        return new FrameCapture(_frame, default, false, false, 0, null);
    }

    private FrameCapture CaptureMetadata() =>
        new(null, ReadViewport(), HasSelection(), ReadMode(ReverseColorsMode), _viewRevision, _theme)
        { HoldGeneration = _holdActive ? _holdGeneration : -1, ContentVersion = ContentVersion, ActiveScreenOffset = ActiveTop, OverscanEnabled = _overscanEnabled };

    private unsafe TerminalFrame Materialize(FrameCapture capture)
    {
        var (viewport, hasSelection, reverse, current) = (capture.Viewport, capture.HasSelection, capture.Reverse, capture.ViewRevision == _capturedViewRevision);
        ushort columns = 0, rowsCount = 0;
        RenderDirty dirty = 0;
        Rgb foreground = default, background = default, cursorColor = default;
        var nativeCursor = RenderCursor.Sized();
        byte cursorHasColor = 0;
        var stateKeys = stackalloc RenderStateData[] {
            RenderStateData.Columns, RenderStateData.Rows, RenderStateData.Dirty, RenderStateData.ColorForeground,
            RenderStateData.ColorBackground, RenderStateData.Cursor, RenderStateData.ColorCursorHasValue };
        var stateValues = stackalloc void*[] { &columns, &rowsCount, &dirty, &foreground, &background, &nativeCursor, &cursorHasColor };
        nuint written = 0;
        ThrowIfFailed(RenderNative.ghostty_render_state_get_multi(_renderState, 7, stateKeys, stateValues, &written), "Could not read render state.");
        if (cursorHasColor != 0) StateGet(RenderStateData.ColorCursor, &cursorColor);
        var defaultFg = foreground.ToColor();
        var defaultBg = background.ToColor();
        Rgb* palette = stackalloc Rgb[256];
        StateGet(RenderStateData.Palette, palette);
        var paletteBytes = new ReadOnlySpan<byte>(palette, 256 * sizeof(Rgb));
        var paletteMatches = _framePalette is not null && paletteBytes.SequenceEqual(_framePalette);
        OverscanRequest coverage = default;
        StateGet(RenderStateData.Overscan, &coverage);
        var coverageMatches = _frame is not null && _frameOverscanEnabled == capture.OverscanEnabled &&
            _frame.OverscanAbove.Length == coverage.Above && _frame.OverscanBelow.Length == coverage.Below;
        var hasPosition = nativeCursor.ViewportHasValue != 0;
        var cursor = new TerminalCursor(hasPosition ? nativeCursor.ViewportX : 0, hasPosition ? nativeCursor.ViewportY : 0,
            nativeCursor.Visible != 0 && hasPosition, nativeCursor.Blinking != 0,
            nativeCursor.VisualStyle switch { CursorVisualStyle.Bar => TerminalCursorShape.Bar, CursorVisualStyle.Underline => TerminalCursorShape.Underline, CursorVisualStyle.BlockHollow => TerminalCursorShape.Hollow, _ => TerminalCursorShape.Block },
            cursorHasColor != 0 ? cursorColor.ToColor() : reverse ? defaultBg : defaultFg)
            { WideTail = hasPosition && nativeCursor.WideTail != 0, PasswordInput = nativeCursor.PasswordInput != 0 };
        var images = CompleteImages(capture.ImageCapture);
        if (dirty == RenderDirty.Clean && _frame is not null && current && coverageMatches && paletteMatches && _frame.Columns == columns && _frame.Rows == rowsCount && ReferenceEquals(capture.SearchHighlights, _frame.SearchHighlights) && capture.ActiveScreenOffset == _frame.ActiveScreenOffset && ReferenceEquals(images, _frame.Images) && viewport == _frame.Viewport && hasSelection == _frame.HasSelection && reverse == _frameReverse && cursor == _frame.Cursor &&
            (reverse ? defaultBg : defaultFg) == _frame.Foreground && (reverse ? defaultFg : defaultBg) == _frame.Background)
            return capture.Boundary || _frame.ContentVersion == capture.ContentVersion ? _frame : _frame = _frame with { ContentVersion = capture.ContentVersion };
        var previous = _frame;
        var captureAll = !coverageMatches || !paletteMatches || !current || dirty == RenderDirty.Full || previous is null || previous.Columns != columns || previous.Rows != rowsCount ||
            previous.Viewport != viewport || previous.HasSelection != hasSelection || reverse != _frameReverse ||
            (reverse ? defaultBg : defaultFg) != previous.Foreground || (reverse ? defaultFg : defaultBg) != previous.Background ||
            (!current && hasSelection);
        var grid = new TerminalCellGrid.Builder(previous?.Cells, columns, rowsCount);
        var rowPtr = _rowIterator;
        StateGet(RenderStateData.RowIterator, &rowPtr);
        byte* scratch = stackalloc byte[ScratchTextBytes];
        var cellKeys = stackalloc RenderCellData[] { RenderCellData.Raw, RenderCellData.Style, RenderCellData.GraphemesUtf8 };
        ulong raw = 0;
        Style style = default;
        int wide = 0;
        var buffer = default(ByteBuffer);
        var cellValues = stackalloc void*[] { &raw, &style, &buffer };
        var capturedRows = 0;
        var visibleRows = 0;
        var above = captureAll ? [] : previous!.OverscanAbove;
        var below = captureAll ? [] : previous!.OverscanBelow;
        while (true)
        {
            int row;
            if (captureAll)
            {
                if (RenderNative.ghostty_render_state_row_iterator_next(_rowIterator) == 0) break;
                row = capturedRows++;
                if (capture.OverscanEnabled) RowGet(RenderRowData.ViewportY, &row);
            }
            else
            {
                ushort dirtyRow = 0;
                if (RenderNative.ghostty_render_state_row_iterator_next_dirty(_rowIterator, &dirtyRow) == 0) break;
                row = dirtyRow;
                if (capture.OverscanEnabled) RowGet(RenderRowData.ViewportY, &row);
            }
            if (row < -1 || row > rowsCount) throw new InvalidOperationException("Render state returned an unexpected overscan row.");
            var extra = row < 0 || row >= rowsCount;
            if (!extra) visibleRows++;
            int selectionStart = -1, selectionEnd = -1;
            if (hasSelection)
            {
                var range = RowSelection.Sized();
                var selectionResult = RenderNative.ghostty_render_state_row_get(_rowIterator, RenderRowData.Selection, &range);
                if (selectionResult == NativeResult.Success) (selectionStart, selectionEnd) = (range.StartX, range.EndX);
                else if (selectionResult != NativeResult.NoValue) ThrowIfFailed(selectionResult, "Could not read row selection.");
            }
            var cellPtr = _cellIterator;
            RowGet(RenderRowData.Cells, &cellPtr);
            var oldRow = extra ? default : grid.Previous(row);
            var rowCells = oldRow.IsEmpty ? new TerminalCell[columns] : null;
            var index = 0;
            var start = 0;
            var end = columns;
            while (RenderNative.ghostty_render_state_row_cells_next(_cellIterator) != 0)
            {
                if (index == end) throw new InvalidOperationException("Render state returned an incomplete grid.");
                buffer = new ByteBuffer { Pointer = scratch, Capacity = ScratchTextBytes };
                var cellResult = RenderNative.ghostty_render_state_row_cells_get_multi(_cellIterator, 3, cellKeys, cellValues, &written);
                var old = oldRow.IsEmpty ? null : oldRow[index];
                string text;
                if (cellResult == NativeResult.Success) text = CellText(new ReadOnlySpan<byte>(scratch, checked((int)buffer.Length)), old?.Text);
                else
                {
                    // get_multi stops at the first failure. Only a grapheme cluster longer than the scratch buffer may fail here.
                    if (cellResult != NativeResult.OutOfSpace || written != 2) ThrowIfFailed(cellResult, "Could not read render cell.");
                    var bytes = new byte[checked((int)buffer.Length)];
                    fixed (byte* ptr = bytes)
                    {
                        buffer = new ByteBuffer { Pointer = ptr, Capacity = (nuint)bytes.Length };
                        CellGet(RenderCellData.GraphemesUtf8, &buffer);
                    }
                    text = CellText(bytes.AsSpan(0, checked((int)buffer.Length)), old?.Text);
                }
                ThrowIfFailed(RenderNative.ghostty_cell_get(raw, CellData.Wide, &wide), "Could not read cell width.");
                var inverse = style.Has(StyleFlags.Inverse);
                var (fg, bg, hasBackground) = CellColors(defaultFg, defaultBg, inverse ^ reverse);
                hasBackground |= inverse;
                var column = index - start;
                var selected = column >= selectionStart && column <= selectionEnd;
                var width = (byte)((CellWide)wide == CellWide.Wide ? 2 : (CellWide)wide >= CellWide.SpacerTail ? 0 : 1);
                bool bold = style.Has(StyleFlags.Bold), italic = style.Has(StyleFlags.Italic), underline = style.Has(StyleFlags.UnderlineMask),
                    strikethrough = style.Has(StyleFlags.Strikethrough), dim = style.Has(StyleFlags.Faint), hidden = style.Has(StyleFlags.Invisible) || text.StartsWith("\U0010EEEE", StringComparison.Ordinal);
                var underlineStyle = (TerminalUnderlineStyle)((style.Flags >> 8) & 7);
                TerminalColor? underlineColor = style.UnderlineTag == 1 ? palette[style.UnderlineR].ToColor() : style.UnderlineTag == 2 ? new TerminalColor(style.UnderlineR, style.UnderlineG, style.UnderlineB) : null;
                bool overline = style.Has(StyleFlags.Overline), textBlink = style.Has(StyleFlags.Blink);
                var left = index == 0 ? null : rowCells is null ? oldRow[index - 1] : rowCells[index - 1];
                var cell = Same(old) ? old! : Same(left) ? left!
                    : new TerminalCell(text, width, fg, bg, bold, italic, underline, strikethrough, dim, hidden)
                    { HasBackground = hasBackground, Selected = selected, UnderlineStyle = underlineStyle, UnderlineColor = underlineColor, Overline = overline, Blink = textBlink };
                if (rowCells is null && !ReferenceEquals(cell, old))
                {
                    rowCells = new TerminalCell[columns];
                    oldRow[..index].CopyTo(rowCells);
                }
                if (rowCells is not null) rowCells[index] = cell;
                index++;

                bool Same(TerminalCell? cell) => cell is not null && cell.Text == text && cell.Width == width && cell.Foreground == fg && cell.Background == bg &&
                    cell.HasBackground == hasBackground && cell.Bold == bold && cell.Italic == italic && cell.Underline == underline &&
                    cell.Strikethrough == strikethrough && cell.Dim == dim && cell.Hidden == hidden && cell.Selected == selected &&
                    cell.UnderlineStyle == underlineStyle && cell.UnderlineColor == underlineColor && cell.Overline == overline && cell.Blink == textBlink;
            }
            if (index != end) throw new InvalidOperationException("Render state returned an incomplete grid.");
            if (rowCells is null) continue;
            for (var i = 0; i + 1 < rowCells.Length; i++)
                if (rowCells[i].Width == 2 && rowCells[i].Selected && rowCells[i + 1].Width == 0)
                    rowCells[i + 1] = rowCells[i + 1] with { Selected = true };
            if (extra)
            {
                var overscanRow = new TerminalOverscanRow(row, rowCells);
                if (row < 0) above = captureAll ? [.. above, overscanRow] : [overscanRow];
                else below = captureAll ? [.. below, overscanRow] : [overscanRow];
                continue;
            }
            grid.Replace(row, rowCells);
        }
        if (captureAll && visibleRows != rowsCount) throw new InvalidOperationException("Render state returned an incomplete grid.");
        if (reverse) (defaultFg, defaultBg) = (defaultBg, defaultFg);
        _frameReverse = reverse;
        _frameOverscanEnabled = capture.OverscanEnabled;
        if (!paletteMatches) _framePalette = paletteBytes.ToArray();
        _frame = new TerminalFrame(columns, rowsCount, grid.Build(), cursor, defaultFg, defaultBg) {
            Images = images, ActiveScreenOffset = capture.ActiveScreenOffset, Viewport = viewport, HasSelection = hasSelection, ContentVersion = capture.ContentVersion,
            SearchHighlights = capture.SearchHighlights,
            OverscanAbove = above, OverscanBelow = below,
            SelectionBackground = (capture.Theme ?? TerminalThemes.All[0]).SelectionBackground,
            SelectionForeground = (capture.Theme ?? TerminalThemes.All[0]).SelectionForeground,
            CursorText = capture.Theme is null ? null : capture.Theme.CursorText ?? background.ToColor(),
        };
        _capturedViewRevision = capture.ViewRevision;
        return _frame;
    }

    private RenderHandle NewRenderState()
    {
        ThrowIfFailed(RenderNative.ghostty_render_state_new(RenderAllocator, out var state), "Could not create render state.");
        return new RenderHandle(state);
    }

    /// <summary>Adopts a pending boundary handoff and creates any missing render handle. Requires render and terminal exclusion.</summary>
    private void EnsureRender()
    {
        AdoptHandoff();
        if (_render is null)
        {
            _render = NewRenderState();
            _renderState = _render.DangerousGetHandle();
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

    /// <summary>Frees the render state with the iterators that borrow from it. The next capture rebuilds every row.</summary>
    private void RetireRender()
    {
        _placementsGeneration = _retriedPlacements = null;
        _renderState = _rowIterator = _cellIterator = IntPtr.Zero;
        _renderCells?.Dispose(); _renderRows?.Dispose(); _render?.Dispose();
        _renderCells = null; _renderRows = null; _render = null;
    }

    /// <summary>
    /// The lowest active row at or below the cursor that a frame would draw. Updates the render state without
    /// cleaning it, so the next frame still sees every change.
    /// </summary>
    private unsafe int LastContentRow()
    {
        var inFlight = Volatile.Read(ref _captureInFlight);
        if (!inFlight) EnsureRender();
        // A parser callback must never borrow the iterators being materialized by CompleteFrame.
        using var localState = inFlight ? NewRenderState() : null;
        RenderRowsHandle? localRows = null;
        RenderCellsHandle? localCells = null;
        if (inFlight)
        {
            ThrowIfFailed(RenderNative.ghostty_render_state_row_iterator_new(IntPtr.Zero, out var rows), "Could not create presentation rows.");
            localRows = new RenderRowsHandle(rows);
            var cellResult = RenderNative.ghostty_render_state_row_cells_new(IntPtr.Zero, out var cells);
            if (cellResult != NativeResult.Success) localRows.Dispose();
            ThrowIfFailed(cellResult, "Could not create presentation cells.");
            localCells = new RenderCellsHandle(cells);
            _renderStale = true; // The local update consumes terminal dirtiness; adoption must replace the old state.
            _handoff?.Dispose();
            _handoff = null;
        }
        using var ownedRows = localRows;
        using var ownedCells = localCells;
        var rowIterator = (localRows ?? _renderRows)!.DangerousGetHandle();
        var cellIterator = (localCells ?? _renderCells)!.DangerousGetHandle();
        var last = ReadActiveCursorRow();
        var reverse = ReadMode(ReverseColorsMode);
        using var state = new HandleLease(localState ?? _render);
        using var rowsLease = new HandleLease(localRows ?? _renderRows);
        using var cellsLease = new HandleLease(localCells ?? _renderCells);
        var presentationState = state.Raw;
        ConfigureOverscan(state.Raw, false);
        ThrowIfFailed(RenderNative.ghostty_render_state_update(state.Raw, _handle), "Could not update render state.");
        Rgb foreground = default, background = default;
        LocalStateGet(RenderStateData.ColorForeground, &foreground); LocalStateGet(RenderStateData.ColorBackground, &background);
        TerminalColor defaultFg = foreground.ToColor(), defaultBg = background.ToColor(), frameBg = reverse ? defaultFg : defaultBg;
        var rowPtr = rowIterator;
        LocalStateGet(RenderStateData.RowIterator, &rowPtr);
        byte* scratch = stackalloc byte[ScratchTextBytes];
        for (var row = 0; RenderNative.ghostty_render_state_row_iterator_next(rowIterator) != 0; row++)
        {
            if (row <= last) continue;
            var cellPtr = cellIterator;
            LocalRowGet(RenderRowData.Cells, &cellPtr);
            while (RenderNative.ghostty_render_state_row_cells_next(cellIterator) != 0)
            {
                ulong raw = 0; int tag = 0; ushort styleId = 0; uint codepoint = 0;
                LocalCellGet(RenderCellData.Raw, &raw);
                ThrowIfFailed(RenderNative.ghostty_cell_get(raw, CellData.ContentTag, &tag), "Could not read cell content.");
                ThrowIfFailed(RenderNative.ghostty_cell_get(raw, CellData.StyleId, &styleId), "Could not read cell style.");
                ThrowIfFailed(RenderNative.ghostty_cell_get(raw, CellData.Codepoint, &codepoint), "Could not read cell codepoint.");
                if (tag == 0 && styleId == 0 && (codepoint == 0 || codepoint <= char.MaxValue && char.IsWhiteSpace((char)codepoint))) continue;
                Style style = default;
                LocalCellGet(RenderCellData.Style, &style);
                var inverse = style.Has(StyleFlags.Inverse);
                var colors = CellColors(defaultFg, defaultBg, inverse ^ reverse, cellIterator);
                if (style.Has(StyleFlags.UnderlineMask | StyleFlags.Strikethrough | StyleFlags.Overline) || inverse || colors.HasBackground || colors.Bg != frameBg) { last = row; break; }
                var buffer = new ByteBuffer { Pointer = scratch, Capacity = ScratchTextBytes };
                var result = RenderNative.ghostty_render_state_row_cells_get(cellIterator, RenderCellData.GraphemesUtf8, &buffer);
                if (result == NativeResult.OutOfSpace) { last = row; break; }
                ThrowIfFailed(result, "Could not read cell text.");
                if (!string.IsNullOrWhiteSpace(CellText(new ReadOnlySpan<byte>(scratch, checked((int)buffer.Length)), null))) { last = row; break; }
            }
        }
        void LocalStateGet(RenderStateData key, void* value) => ThrowIfFailed(RenderNative.ghostty_render_state_get(presentationState, key, value), "Could not read presentation state.");
        void LocalRowGet(RenderRowData key, void* value) => ThrowIfFailed(RenderNative.ghostty_render_state_row_get(rowIterator, key, value), "Could not read presentation row.");
        void LocalCellGet(RenderCellData key, void* value) => ThrowIfFailed(RenderNative.ghostty_render_state_row_cells_get(cellIterator, key, value), "Could not read presentation cell.");
        return Math.Max(last, LastImageRow(state.Raw));
    }

    private unsafe (TerminalColor Fg, TerminalColor Bg, bool HasBackground) CellColors(TerminalColor fg, TerminalColor bg, bool inverse, IntPtr iterator = default)
    {
        if (iterator == IntPtr.Zero) iterator = _cellIterator;
        Rgb color = default;
        var result = RenderNative.ghostty_render_state_row_cells_get(iterator, RenderCellData.ForegroundColor, &color);
        var hasForeground = result == NativeResult.Success;
        if (result == NativeResult.Success) fg = color.ToColor(); else if (result != NativeResult.NoValue) ThrowIfFailed(result, "Could not read cell foreground.");
        result = RenderNative.ghostty_render_state_row_cells_get(iterator, RenderCellData.BackgroundColor, &color);
        var hasBackground = result == NativeResult.Success;
        if (result == NativeResult.Success) bg = color.ToColor(); else if (result != NativeResult.NoValue) ThrowIfFailed(result, "Could not read cell background.");
        return inverse ? (bg, fg, hasForeground) : (fg, bg, hasBackground);
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
        ThrowIfFailed(RenderNative.ghostty_terminal_get(_handle, (int)TerminalData.Mode, &value), "Could not read terminal mode.");
        return value.Enabled != 0;
    }
    private unsafe void StateGet(RenderStateData key, void* value) => ThrowIfFailed(RenderNative.ghostty_render_state_get(_renderState, key, value), "Could not read render state.");
    private unsafe void RowGet(RenderRowData key, void* value) => ThrowIfFailed(RenderNative.ghostty_render_state_row_get(_rowIterator, key, value), "Could not read render row.");
    private unsafe void CellGet(RenderCellData key, void* value) => ThrowIfFailed(RenderNative.ghostty_render_state_row_cells_get(_cellIterator, key, value), "Could not read render cell.");
    private void DisposeRender()
    {
        RetireRender();
        _handoff?.Dispose();
        _handoff = null;
    }

    /// <summary>
    /// Metadata recorded with begin_update at one moment under terminal exclusion. <see cref="Held"/> short-circuits
    /// completion with a retained frame. <see cref="Boundary"/> marks a render-hold boundary already in the render state.
    /// </summary>
    internal readonly record struct FrameCapture(TerminalFrame? Held, TerminalViewport Viewport, bool HasSelection, bool Reverse, long ViewRevision, TerminalTheme? Theme)
    {
        public bool Boundary { get; init; }
        public long HoldGeneration { get; init; } = -1;
        public long ContentVersion { get; init; }
        public long ActiveScreenOffset { get; init; }
        public bool OverscanEnabled { get; init; }
        public ImageCapture? ImageCapture { get; init; }
        public TerminalSearchHighlights? SearchHighlights { get; init; }
    }
}
