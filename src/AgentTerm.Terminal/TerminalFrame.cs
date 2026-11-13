namespace AgentTerm.Terminal;

public readonly record struct TerminalColor(byte R, byte G, byte B);

public enum TerminalUnderlineStyle { None, Single, Double, Curly, Dotted, Dashed }

public sealed record TerminalCell(string Text, byte Width, TerminalColor Foreground,
    TerminalColor Background, bool Bold, bool Italic, bool Underline,
    bool Strikethrough, bool Dim, bool Hidden)
{
    // Keep presentation flags together so adding background presence does not enlarge every cell object.
    private byte _presentationFlags;
    /// <summary>Whether the effective background must cover the default background and below-background images.</summary>
    public bool HasBackground { get => (_presentationFlags & 1) != 0; init => SetFlag(1, value); }
    public bool Selected { get => (_presentationFlags & 2) != 0; init => SetFlag(2, value); }
    public TerminalUnderlineStyle UnderlineStyle { get; init; }
    public TerminalColor? UnderlineColor { get; init; }
    public bool Overline { get => (_presentationFlags & 4) != 0; init => SetFlag(4, value); }
    public bool Blink { get => (_presentationFlags & 8) != 0; init => SetFlag(8, value); }
    private void SetFlag(byte flag, bool value) =>
        _presentationFlags = value ? (byte)(_presentationFlags | flag) : (byte)(_presentationFlags & ~flag);
}

public enum TerminalCursorShape { Block, Bar, Underline, Hollow }
public sealed record TerminalCursor(int Column, int Row, bool Visible, bool Blink,
    TerminalCursorShape Shape, TerminalColor Color)
{
    public bool WideTail { get; init; }
    public bool PasswordInput { get; init; }
}

public sealed record TerminalOverscanRow(int ViewportY, TerminalCell[] Cells);

/// <summary>Owned rows shared between snapshots. Published rows are never mutated.</summary>
public sealed class TerminalCellGrid : IReadOnlyList<TerminalCell>
{
    private readonly TerminalCell[][] _rows;
    private readonly int _columns;
    internal TerminalCellGrid(TerminalCell[][] rows, int columns) => (_rows, _columns) = (rows, columns);
    public int Length => _rows.Length * _columns;
    public int Count => Length;
    public TerminalCell this[int index] => _rows[index / _columns][index % _columns];
    public TerminalCell this[Index index] => this[index.GetOffset(Length)];
    internal TerminalCell[][] CopyRows() => (TerminalCell[][])_rows.Clone();
    internal ReadOnlySpan<TerminalCell> Row(int row) => _rows[row];
    public bool SharesRowWith(TerminalCellGrid other, int row) => ReferenceEquals(_rows[row], other._rows[row]);
    public IEnumerator<TerminalCell> GetEnumerator() => _rows.SelectMany(row => row).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed record TerminalFrame(int Columns, int Rows, TerminalCellGrid Cells,
    TerminalCursor Cursor, TerminalColor Foreground, TerminalColor Background)
{
    public TerminalFrame(int columns, int rows, TerminalCell[] cells, TerminalCursor cursor, TerminalColor foreground, TerminalColor background)
        : this(columns, rows, CopyCells(columns, rows, cells), cursor, foreground, background) { }

    public TerminalFrame WithCells(TerminalCell[] cells) => this with { Cells = CopyCells(Columns, Rows, cells) };

    private static TerminalCellGrid CopyCells(int columns, int rows, TerminalCell[] cells)
    {
        if (columns <= 0 || rows < 0 || cells.Length != columns * rows) throw new ArgumentException("Cell grid dimensions do not match its contents.", nameof(cells));
        var result = new TerminalCell[rows][];
        for (var row = 0; row < rows; row++) result[row] = cells.AsSpan(row * columns, columns).ToArray();
        return new TerminalCellGrid(result, columns);
    }
    public TerminalOverscanRow[] OverscanAbove { get; init; } = [];
    public TerminalOverscanRow[] OverscanBelow { get; init; } = [];
    public TerminalViewport Viewport { get; init; }
    public bool HasSelection { get; init; }
    public TerminalSearchHighlights? SearchHighlights { get; init; }
    public long ContentVersion { get; init; }
    /// <summary>Absolute history row of the active screen at capture time, for displayed-frame mouse mapping.</summary>
    public long ActiveScreenOffset { get; init; }
    public TerminalColor SelectionBackground { get; init; } = new(45, 92, 150);
    public TerminalColor SelectionForeground { get; init; } = new(255, 255, 255);
    public TerminalColor? CursorText { get; init; }
    public TerminalImages Images { get; init; } = TerminalImages.Empty;
}

public readonly record struct TerminalViewport(long TotalRows, long Offset, int Rows, bool IsAlternateScreen)
{
    public int TopInset => (int)Math.Max(0, Rows - TotalRows);
    public long MaximumOffset => Math.Max(0, TotalRows - Rows);
    public bool IsAtBottom => Offset >= MaximumOffset;
}

[Flags]
public enum TerminalModifiers
{
    None = 0, Shift = 1, Control = 2, Alt = 4, Super = 8,
    CapsLock = 16, NumLock = 32,
    ShiftRight = 64, ControlRight = 128, AltRight = 256, SuperRight = 512
}
public enum TerminalKeyAction { Press, Repeat, Release }
