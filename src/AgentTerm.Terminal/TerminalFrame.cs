namespace AgentTerm.Terminal;

public readonly record struct TerminalColor(byte R, byte G, byte B);

public enum TerminalUnderlineStyle { None, Single, Double, Curly, Dotted, Dashed }

public sealed record TerminalCell(string Text, byte Width, TerminalColor Foreground,
    TerminalColor Background, bool Bold, bool Italic, bool Underline,
    bool Strikethrough, bool Dim, bool Hidden)
{
    public bool Selected { get; init; }
    public TerminalUnderlineStyle UnderlineStyle { get; init; }
    public TerminalColor? UnderlineColor { get; init; }
    public bool Overline { get; init; }
    public bool Blink { get; init; }
}

public enum TerminalCursorShape { Block, Bar, Underline, Hollow }
public sealed record TerminalCursor(int Column, int Row, bool Visible, bool Blink,
    TerminalCursorShape Shape, TerminalColor Color)
{
    public bool WideTail { get; init; }
    public bool PasswordInput { get; init; }
}

public sealed record TerminalOverscanRow(int ViewportY, TerminalCell[] Cells);

public sealed record TerminalFrame(int Columns, int Rows, TerminalCell[] Cells,
    TerminalCursor Cursor, TerminalColor Foreground, TerminalColor Background)
{
    public TerminalOverscanRow[] OverscanAbove { get; init; } = [];
    public TerminalOverscanRow[] OverscanBelow { get; init; } = [];
    public TerminalViewport Viewport { get; init; }
    public bool HasSelection { get; init; }
    public TerminalColor SelectionBackground { get; init; } = new(45, 92, 150);
    public TerminalColor SelectionForeground { get; init; } = new(255, 255, 255);
    public TerminalColor? CursorText { get; init; }
}

public readonly record struct TerminalViewport(long TotalRows, long Offset, int Rows, bool IsAlternateScreen)
{
    public int TopInset => (int)Math.Max(0, Rows - TotalRows);
    public long MaximumOffset => Math.Max(0, TotalRows - Rows);
    public bool IsAtBottom => Offset >= MaximumOffset;
}

[Flags]
public enum TerminalModifiers { None = 0, Shift = 1, Control = 2, Alt = 4, Super = 8 }
public enum TerminalKeyAction { Press, Repeat, Release }
