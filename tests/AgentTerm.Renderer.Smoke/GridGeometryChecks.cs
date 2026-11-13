using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;

internal static class GridGeometryChecks
{
    private const float CellWidth = 10, CellHeight = 20;
    private static readonly TerminalColor Background = new(12, 12, 12);
    private static readonly TerminalColor Red = new(240, 30, 40), Green = new(30, 220, 80), Blue = new(40, 80, 240), Magenta = new(200, 40, 200);
    private static readonly TerminalColor CursorColor = new(171, 205, 239);

    internal static void Run(CanvasDevice device)
    {
        InsetDocument(device);
        ScrollbackDocument(device);
        TopAligned();
        CardRows();
        LiveTerminalLaunch(device);
        CursorOriginInsideRoundedBounds(device);
        Console.WriteLine("Grid geometry checks passed.");
    }

    private static void InsetDocument(CanvasDevice device)
    {
        var frame = Frame(rows: 6, columns: 4, new TerminalViewport(TotalRows: 2, Offset: 0, Rows: 6, IsAlternateScreen: false),
            row => row switch { 0 => Red, 1 => Green, _ => Blue }, new TerminalCursor(1, 1, true, false, TerminalCursorShape.Block, CursorColor));
        Expect(frame.Viewport.TopInset == 4, "Fixture must have a four-row inset.");
        var geometry = TerminalGridGeometry.For(frame, 127, CellWidth, CellHeight, bottomAligned: true);
        Expect(geometry.Top == 87, $"Grid top must be remainder 7 plus inset 80, got {geometry.Top}.");
        Expect(geometry.Rows == 2, $"Only presented rows are drawn, got {geometry.Rows}.");

        using var image = new CanvasRenderTarget(device, 40, 127, 96);
        using (var drawing = image.CreateDrawingSession())
            TerminalSurface.DrawFrame(drawing, frame, CellWidth, CellHeight, focused: true, cursorPhase: true, gridTop: (float)geometry.Top);
        AssertPixel(image, 35, 3, Background, "sub-cell remainder above the inset");
        AssertPixel(image, 35, 50, Background, "inset row");
        AssertPixel(image, 35, 86, Background, "pixel row directly above the first drawn row");
        AssertPixel(image, 35, 87, Red, "first frame row at the grid top");
        AssertPixel(image, 35, 106, Red, "first frame row bottom");
        AssertPixel(image, 35, 107, Green, "second frame row");
        AssertPixel(image, 35, 126, Green, "last pixel row is the last presented row");
        AssertPixel(image, 15, 117, CursorColor, "block cursor on the presented prompt row");
        AssertPixel(image, 5, 117, Green, "cell beside the cursor");
        if (image.GetPixelColors().Any(c => c.R == Blue.R && c.G == Blue.G && c.B == Blue.B))
            throw new Exception("Frame rows past the presented document must be clipped, never drawn.");

        Expect(!geometry.TryCellAt(new Point(5, 3), out _, out _), "The remainder is not a cell.");
        Expect(!geometry.TryCellAt(new Point(5, 50), out _, out _), "The inset has no selectable cells.");
        Expect(!geometry.TryCellAt(new Point(5, 86.9), out _, out _), "A point just above the grid top is not a cell.");
        ExpectCell(geometry, new Point(5, 87), 0, 0, "grid top");
        ExpectCell(geometry, new Point(35, 126.5), 3, 1, "last pixel row");
        Expect(!geometry.TryCellAt(new Point(5, 127), out _, out _), "One past the last pixel row is not a cell.");
        Expect(!geometry.TryCellAt(new Point(40, 100), out _, out _), "A point right of the last column is not a cell.");
        Expect(geometry.ClampedCellAt(new Point(5, 10)) == (0, 0), "A drag into the inset extends from the first presented row.");
        Expect(geometry.ClampedCellAt(new Point(55, 500)) == (3, 1), "A drag below the grid extends from the last cell.");
        Expect(geometry.Caret(frame.Cursor) == new Point(10, 107), $"IME caret must sit on the drawn cursor, got {geometry.Caret(frame.Cursor)}.");
        Expect(geometry.ToFrame(new Point(5, 90)) == new Point(5, 3), "Mouse Y is relative to frame row 0.");
        Expect(geometry.ToFrameClamped(new Point(5, 0)) == new Point(5, 0), "Held-button motion above the grid clamps to row 0.");
        Expect(geometry.ToFrameClamped(new Point(99, 500)) == new Point(39, 39), "Held-button motion below the grid clamps to the last presented row.");
        Expect(geometry.RowTop(-1) == 67, "Card rows above the frame keep the same mapping.");
    }

    private static void ScrollbackDocument(CanvasDevice device)
    {
        var frame = Frame(rows: 6, columns: 4, new TerminalViewport(TotalRows: 50, Offset: 44, Rows: 6, IsAlternateScreen: false),
            row => row == 5 ? Magenta : row == 0 ? Red : Green, new TerminalCursor(0, 5, false, false, TerminalCursorShape.Block, CursorColor));
        var geometry = TerminalGridGeometry.For(frame, 127, CellWidth, CellHeight, bottomAligned: true);
        Expect(geometry.Top == 7 && geometry.Rows == 6, $"Full view must start at the remainder, got top {geometry.Top}, rows {geometry.Rows}.");
        using var image = new CanvasRenderTarget(device, 40, 127, 96);
        using (var drawing = image.CreateDrawingSession())
            TerminalSurface.DrawFrame(drawing, frame, CellWidth, CellHeight, true, true, gridTop: (float)geometry.Top);
        AssertPixel(image, 5, 6, Background, "remainder above scrollback");
        AssertPixel(image, 5, 7, Red, "first scrollback row at the remainder");
        AssertPixel(image, 5, 126, Magenta, "live row bottom edge on the last pixel row");
        ExpectCell(geometry, new Point(5, 7), 0, 0, "first row below the remainder");
        ExpectCell(geometry, new Point(5, 126), 0, 5, "bottom row");
    }

    private static void TopAligned()
    {
        var frame = Frame(rows: 6, columns: 4, new TerminalViewport(50, 44, 6, false), _ => Green,
            new TerminalCursor(2, 3, true, false, TerminalCursorShape.Block, CursorColor));
        var geometry = TerminalGridGeometry.For(frame, 127, CellWidth, CellHeight, bottomAligned: false);
        Expect(geometry.Top == 0 && geometry.Rows == 6, "Legacy draws from the top with every row.");
        ExpectCell(geometry, new Point(0, 0), 0, 0, "Legacy origin");
        Expect(!geometry.TryCellAt(new Point(0, 121), out _, out _), "Legacy remainder below the grid is not a cell.");
        Expect(geometry.Caret(frame.Cursor) == new Point(20, 60), "Legacy caret.");
    }

    private static void CardRows()
    {
        var frame = Frame(rows: 6, columns: 4, new TerminalViewport(50, 44, 6, false), _ => Background,
            new TerminalCursor(0, 5, true, false, TerminalCursorShape.Block, CursorColor));
        var cells = frame.Cells;
        cells[2 * 4 + 1] = cells[2 * 4 + 1] with { Text = ">" };
        cells[4 * 4 + 3] = cells[4 * 4 + 3] with { Background = Red };
        cells[5 * 4 + 0] = cells[5 * 4 + 0] with { Underline = true };
        var geometry = TerminalGridGeometry.For(frame, 120, CellWidth, CellHeight, bottomAligned: true);
        Expect(geometry.RowsBlank(frame, 0, 2), "Two empty rows are blank.");
        Expect(!geometry.RowsBlank(frame, 1, 2), "Prompt text on the second reserved row must hide the card.");
        Expect(!geometry.RowsBlank(frame, 3, 2), "A coloured background is not blank.");
        Expect(!geometry.RowsBlank(frame, 5, 2), "An underlined blank is not blank, even when the second row is off the frame.");
        Expect(geometry.RowsBlank(frame, -1, 2), "Only rows inside the frame are judged.");
        var inset = Frame(rows: 6, columns: 4, new TerminalViewport(2, 0, 6, false), row => row >= 2 ? Red : Background,
            new TerminalCursor(0, 1, true, false, TerminalCursorShape.Block, CursorColor));
        Expect(TerminalGridGeometry.For(inset, 120, CellWidth, CellHeight, true).RowsBlank(inset, 0, 2),
            "Clipped rows beyond the presented document are never judged as card content.");
    }

    private static void LiveTerminalLaunch(CanvasDevice device)
    {
        using var terminal = new GhosttyTerminal(20, 6);
        terminal.SetBottomAnchored(true);
        terminal.Write("\x1b[48;2;240;30;40m   \x1b[0mPS> "u8);
        var frame = terminal.ReadFrame();
        Expect(frame.Viewport.TopInset == 5, $"A one-line document in six rows needs a five-row inset, got {frame.Viewport.TopInset}.");
        var geometry = TerminalGridGeometry.For(frame, 6 * CellHeight, CellWidth, CellHeight, bottomAligned: true);
        Expect(geometry.Top == 100, $"The prompt row must be the bottom row, got top {geometry.Top}.");
        using var image = new CanvasRenderTarget(device, 200, 120, 96);
        using (var drawing = image.CreateDrawingSession())
            TerminalSurface.DrawFrame(drawing, frame, CellWidth, CellHeight, true, true, gridTop: (float)geometry.Top);
        AssertPixel(image, 1, 101, Red, "prompt background on the bottom row");
        if (image.GetPixelColors(0, 0, 200, 100).Any(c => c.R == Red.R && c.G == Red.G && c.B == Red.B))
            throw new Exception("No prompt cells may be drawn above the bottom row at launch.");
        terminal.SetBottomAnchored(false);
        Expect(terminal.ReadFrame().Viewport.TopInset == 0, "Legacy presentation has no inset.");
    }

    private static void CursorOriginInsideRoundedBounds(CanvasDevice device)
    {
        var cursor = new TerminalCursor(1, 1, true, false, TerminalCursorShape.Block, CursorColor);
        var frame = Frame(6, 4, new TerminalViewport(2, 0, 6, false), _ => Background, cursor);
        foreach (var resize in new[] { true })
        {
            var first = TerminalGridGeometry.For(frame, resize ? 120.25 : 120, CellWidth, CellHeight,
                bottomAligned: resize);
            var next = TerminalGridGeometry.For(frame, resize ? 121.25 : 120, CellWidth, CellHeight,
                bottomAligned: resize);
            var firstKey = TerminalSurface.CursorOverlayGeometry(first, cursor.Column, cursor.Row, 1);
            var nextKey = TerminalSurface.CursorOverlayGeometry(next, cursor.Column, cursor.Row, 1);
            Expect(firstKey.Bounds == nextKey.Bounds, "Fixture must move inside one rounded overlay bounds bucket.");
            Expect(firstKey != nextKey && nextKey.GridTop == next.Top,
                "Resize and fractional scrolling must change the overlay geometry key at the exact grid origin.");
            using var oldRaster = new CanvasRenderTarget(device, 40, 140, 96);
            using var newRaster = new CanvasRenderTarget(device, 40, 140, 96);
            using (var drawing = oldRaster.CreateDrawingSession())
                TerminalSurface.DrawFrame(drawing, frame, CellWidth, CellHeight, true, true, gridTop: (float)firstKey.GridTop);
            using (var drawing = newRaster.CreateDrawingSession())
                TerminalSurface.DrawFrame(drawing, frame, CellWidth, CellHeight, true, true, gridTop: (float)nextKey.GridTop);
            Expect(!oldRaster.GetPixelBytes().AsSpan().SequenceEqual(newRaster.GetPixelBytes()),
                "A stationary cursor must move its raster when only the exact grid origin changes.");
        }
    }

    private static TerminalFrame Frame(int rows, int columns, TerminalViewport viewport, Func<int, TerminalColor> background, TerminalCursor cursor)
    {
        var cells = Enumerable.Range(0, rows * columns).Select(index =>
            new TerminalCell(" ", 1, new TerminalColor(230, 230, 230), background(index / columns), false, false, false, false, false, false)).ToArray();
        return new TerminalFrame(columns, rows, cells, cursor, new TerminalColor(230, 230, 230), Background) { Viewport = viewport };
    }

    private static void ExpectCell(TerminalGridGeometry geometry, Point point, int column, int row, string label)
    {
        if (!geometry.TryCellAt(point, out var actualColumn, out var actualRow) || actualColumn != column || actualRow != row)
            throw new Exception($"Hit test at {label} {point}: expected ({column},{row}), got ({actualColumn},{actualRow}).");
    }

    private static void AssertPixel(CanvasRenderTarget image, int x, int y, TerminalColor expected, string label)
    {
        var color = image.GetPixelColors(x, y, 1, 1)[0];
        if (color.R != expected.R || color.G != expected.G || color.B != expected.B)
            throw new Exception($"Incorrect {label} at ({x},{y}). Expected {expected}, got {color}.");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
