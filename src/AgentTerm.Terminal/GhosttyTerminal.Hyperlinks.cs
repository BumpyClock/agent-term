using System.Runtime.InteropServices;

namespace AgentTerm.Terminal;

public sealed partial class GhosttyTerminal
{
    private const int NativeOutOfSpace = -3;
    private const int MaxNativeUriBytes = 1 << 20;

    /// <summary>
    /// The OSC 8 hyperlink at a viewport cell, with every cell it covers on screen, or null when the cell has none.
    /// Resolves fresh native grid references on each call, copies the URI bytes, and keeps no reference afterwards.
    /// A cell on the tail half of a wide character resolves to the character's own cell.
    /// </summary>
    public TerminalLink? ReadLink(int column, int row)
    {
        ThrowIfDisposed();
        if ((uint)column >= _columns || (uint)row >= _rows) return null;
        if (!TryReadLinkCell(column, row, out var cell)) return null;
        if (!cell.HasLink && cell.Wide == 2 && column > 0)
        {
            column--;
            if (!TryReadLinkCell(column, row, out cell)) return null;
        }
        if (!cell.HasLink) return null;
        var uri = ReadUri(cell.Reference);
        if (uri is null) return null;

        var scratch = new byte[uri.Length];
        var spans = new List<TerminalLinkSpan>(1);
        var runStart = column;
        var runEnd = column + 1;
        while (runStart > 0 && BelongsToLink(runStart - 1, row, uri, scratch)) runStart--;
        while (runEnd < _columns && BelongsToLink(runEnd, row, uri, scratch)) runEnd++;
        spans.Add(new TerminalLinkSpan(row, runStart, runEnd));

        // A link continues over a soft wrap only. A hard line break starts a new run.
        var above = row;
        var aboveStart = runStart;
        while (aboveStart == 0 && above > 0 && RowWraps(above - 1) && BelongsToLink(_columns - 1, above - 1, uri, scratch))
        {
            above--;
            aboveStart = _columns - 1;
            while (aboveStart > 0 && BelongsToLink(aboveStart - 1, above, uri, scratch)) aboveStart--;
            spans.Insert(0, new TerminalLinkSpan(above, aboveStart, _columns));
        }
        var below = row;
        var belowEnd = runEnd;
        while (belowEnd == _columns && below + 1 < _rows && RowWraps(below) && BelongsToLink(0, below + 1, uri, scratch))
        {
            below++;
            belowEnd = 1;
            while (belowEnd < _columns && BelongsToLink(belowEnd, below, uri, scratch)) belowEnd++;
            spans.Add(new TerminalLinkSpan(below, 0, belowEnd));
        }
        return new TerminalLink(uri, spans);
    }

    /// <summary>The pointer shape the program asked for with OSC 22. A terminal that never heard one reports text.</summary>
    public unsafe TerminalMouseShape ReadMouseShape()
    {
        ThrowIfDisposed();
        int shape = 0;
        var result = RenderNative.ghostty_terminal_get(_raw, 41, &shape);
        return result == 0 ? TerminalMouseShapes.FromNative(shape) : TerminalMouseShapes.Initial;
    }

    private readonly record struct LinkCell(Native.GridRef Reference, int Wide, bool HasLink);

    private unsafe bool TryReadLinkCell(int column, int row, out LinkCell cell)
    {
        cell = default;
        var point = new Native.Point { Tag = 1, Value = new Native.PointValue { X = (ushort)column, Y = (uint)row } };
        if (Native.ghostty_terminal_grid_ref(_handle, point, out var reference) != 0) return false;
        ulong raw = 0;
        if (LinkNative.ghostty_grid_ref_cell(&reference, &raw) != 0) return false;
        int wide = 0;
        byte hasLink = 0;
        if (RenderNative.ghostty_cell_get(raw, 3, &wide) != 0 || RenderNative.ghostty_cell_get(raw, 7, &hasLink) != 0) return false;
        cell = new LinkCell(reference, wide, hasLink != 0);
        return true;
    }

    private unsafe byte[]? ReadUri(Native.GridRef reference)
    {
        nuint needed = 0;
        var result = LinkNative.ghostty_grid_ref_hyperlink_uri(&reference, null, 0, &needed);
        if (result != NativeOutOfSpace || needed == 0 || needed > MaxNativeUriBytes) return null;
        var uri = new byte[(int)needed];
        nuint written = 0;
        fixed (byte* pointer = uri)
            result = LinkNative.ghostty_grid_ref_hyperlink_uri(&reference, pointer, needed, &written);
        return result == 0 && written == needed ? uri : null;
    }

    private unsafe bool UriEquals(Native.GridRef reference, byte[] expected, byte[] scratch)
    {
        nuint written = 0;
        int result;
        fixed (byte* pointer = scratch)
            result = LinkNative.ghostty_grid_ref_hyperlink_uri(&reference, pointer, (nuint)scratch.Length, &written);
        return result == 0 && written == (nuint)expected.Length && scratch.AsSpan(0, expected.Length).SequenceEqual(expected);
    }

    /// <summary>
    /// Whether a cell is part of the link with this URI. The tail half of a wide character joins when its first half
    /// does. The spacer left at a soft wrapped row end joins when the character that wrapped does.
    /// </summary>
    private bool BelongsToLink(int column, int row, byte[] uri, byte[] scratch)
    {
        if (!TryReadLinkCell(column, row, out var cell)) return false;
        if (cell.HasLink) return UriEquals(cell.Reference, uri, scratch);
        if (cell.Wide == 2 && column > 0)
            return TryReadLinkCell(column - 1, row, out var head) && head.HasLink && UriEquals(head.Reference, uri, scratch);
        if (cell.Wide == 3 && row + 1 < _rows)
            return TryReadLinkCell(0, row + 1, out var wrapped) && wrapped.HasLink && UriEquals(wrapped.Reference, uri, scratch);
        return false;
    }

    private unsafe bool RowWraps(int row)
    {
        var point = new Native.Point { Tag = 1, Value = new Native.PointValue { X = 0, Y = (uint)row } };
        if (Native.ghostty_terminal_grid_ref(_handle, point, out var reference) != 0) return false;
        ulong rowValue = 0;
        byte wraps = 0;
        return LinkNative.ghostty_grid_ref_row(&reference, &rowValue) == 0 &&
            LinkNative.ghostty_row_get(rowValue, 1, &wraps) == 0 && wraps != 0;
    }

    private static unsafe partial class LinkNative
    {
        private const string Lib = "ghostty-vt";
        [LibraryImport(Lib)] internal static partial int ghostty_grid_ref_cell(Native.GridRef* reference, ulong* cell);
        [LibraryImport(Lib)] internal static partial int ghostty_grid_ref_row(Native.GridRef* reference, ulong* row);
        [LibraryImport(Lib)] internal static partial int ghostty_row_get(ulong row, int key, void* value);
        [LibraryImport(Lib)] internal static partial int ghostty_grid_ref_hyperlink_uri(Native.GridRef* reference, byte* buffer, nuint length, nuint* written);
    }
}
