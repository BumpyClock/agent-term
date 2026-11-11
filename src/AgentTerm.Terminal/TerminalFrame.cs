namespace AgentTerm.Terminal;

public readonly record struct TerminalColor(byte R, byte G, byte B);

// Width is zero for the continuation of a wide grapheme. Colors are resolved RGB,
// including palette lookup and inverse video, so the renderer needs no VT logic.
public sealed record TerminalCell(string Text, byte Width, TerminalColor Foreground,
    TerminalColor Background, bool Bold, bool Italic, bool Underline,
    bool Strikethrough, bool Dim, bool Hidden)
{
    public bool Selected { get; init; }
}

public enum TerminalCursorShape { Block, Bar, Underline }
public sealed record TerminalCursor(int Column, int Row, bool Visible, bool Blink,
    TerminalCursorShape Shape, TerminalColor Color);

// Each capture owns its cell array. Consumers must not modify it.
public sealed record TerminalFrame(int Columns, int Rows, TerminalCell[] Cells,
    TerminalCursor Cursor, TerminalColor Foreground, TerminalColor Background)
{
    public TerminalViewport Viewport { get; init; }
    public bool HasSelection { get; init; }
}

public readonly record struct TerminalViewport(long TotalRows, long Offset, int Rows, bool IsAlternateScreen)
{
    public long MaximumOffset => Math.Max(0, TotalRows - Rows);
    public bool IsAtBottom => Offset >= MaximumOffset;
}

[Flags]
public enum TerminalModifiers { None = 0, Shift = 1, Control = 2, Alt = 4, Super = 8 }
public enum TerminalKeyAction { Press, Repeat, Release }
