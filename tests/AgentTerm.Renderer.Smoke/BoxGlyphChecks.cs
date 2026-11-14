using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;

internal static class BoxGlyphChecks
{
    private static readonly TerminalColor Black = new(0, 0, 0);
    private static readonly TerminalColor White = new(230, 230, 230);

    private static readonly string[] Box =
    [
        "       ",
        " ┌─┬─┐ ",
        " │ │ │ ",
        " ├─┼─┤ ",
        " └─┴─┘ ",
        " ╭─╮ █ ",
        " ╰─╯ ▀ ",
        "       ",
    ];

    internal static void Run(CanvasDevice device)
    {
        var installed = CanvasTextFormat.GetSystemFontFamilies().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dpi in new[] { 96f, 120f, 144f, 168f })
        foreach (var (cellWidth, cellHeight) in new[] { (8.4f, 19f), (10f, 20f), (7.7f, 17.5f) })
        foreach (var gridTop in new[] { 0f, 3.25f })
        {
            var setup = new Setup(device, dpi, cellWidth, cellHeight, gridTop);
            var raster = setup.Render(Box, "Consolas");
            AssertHorizontal(setup, raster, 1, 2, 5);
            AssertHorizontal(setup, raster, 3, 2, 5);
            AssertHorizontal(setup, raster, 4, 2, 5);
            foreach (var column in new[] { 1, 3, 5 }) AssertVertical(setup, raster, column, 2, 4);
            AssertArcReachesEdges(setup, raster);
            AssertBlocks(setup, raster);
            foreach (var family in new[] { "Cascadia Mono", "MS Gothic" }.Where(installed.Contains))
                if (!setup.Render(Box, family).SequenceEqual(raster))
                    throw new Exception($"Box glyphs must not depend on the font: {family} differs from Consolas at {setup}.");
        }
        Console.WriteLine("Box-drawing and block glyphs join across cells and cover full cell edges.");
    }

    private static void AssertHorizontal(Setup setup, bool[,] lit, int row, int firstColumn, int endColumn)
    {
        var (left, right) = (setup.X(firstColumn), setup.X(endColumn));
        var (top, bottom) = (setup.Y(row), setup.Y(row + 1));
        var rows = Enumerable.Range(top, bottom - top).Where(y => Enumerable.Range(left, right - left).All(x => lit[x, y])).ToArray();
        if (rows.Length == 0 || rows[^1] - rows[0] + 1 != rows.Length)
            throw new Exception($"Row {row} must have one continuous horizontal stroke over columns {firstColumn}..{endColumn - 1} at {setup}.");
        if (Enumerable.Range(top, bottom - top).Count(y => lit[left, y]) != rows.Length)
            throw new Exception($"Row {row} stroke must keep one thickness across cell boundaries at {setup}.");
    }

    private static void AssertVertical(Setup setup, bool[,] lit, int column, int firstRow, int endRow)
    {
        var (top, bottom) = (setup.Y(firstRow) - 1, setup.Y(endRow) + 1);
        var (left, right) = (setup.X(column), setup.X(column + 1));
        if (!Enumerable.Range(left, right - left).Any(x => Enumerable.Range(top, bottom - top).All(y => lit[x, y])))
            throw new Exception($"Column {column} must have one continuous vertical stroke across rows {firstRow - 1}..{endRow} at {setup}.");
    }

    private static void AssertArcReachesEdges(Setup setup, bool[,] lit)
    {
        var lineRows = Enumerable.Range(setup.Y(5), setup.Y(6) - setup.Y(5)).Where(y => lit[setup.X(2), y]).ToArray();
        var lineColumns = Enumerable.Range(setup.X(1), setup.X(2) - setup.X(1)).Where(x => lit[x, setup.Y(6)]).ToArray();
        if (lineRows.Length == 0 || lineColumns.Length == 0) throw new Exception($"Rounded corners must join their neighbours at {setup}.");
        foreach (var y in lineRows)
            if (!lit[setup.X(2) - 1, y] || !lit[setup.X(3), y])
                throw new Exception($"Rounded corners must meet the horizontal stroke at the cell edge at {setup}.");
        foreach (var x in lineColumns)
            if (!lit[x, setup.Y(6) - 1] || !lit[x, setup.Y(6)])
                throw new Exception($"Rounded corners must meet each other at the shared cell edge at {setup}.");
    }

    private static void AssertBlocks(Setup setup, bool[,] lit)
    {
        var (left, right) = (setup.X(5), setup.X(6));
        for (var x = left - 1; x <= right; x++)
        for (var y = setup.Y(5) - 1; y < setup.Y(6); y++)
        {
            var inside = x >= left && x < right && y >= setup.Y(5) && y < setup.Y(6);
            if (lit[x, y] != inside) throw new Exception($"A full block must cover exactly its cell at {setup} (pixel {x},{y}).");
        }
        var upper = Enumerable.Range(setup.Y(6), setup.Y(7) - setup.Y(6)).Count(y => lit[left, y]);
        if (upper == 0 || !lit[left, setup.Y(6)] || !lit[right - 1, setup.Y(6)] || lit[left, setup.Y(7) - 1])
            throw new Exception($"An upper half block must start at the top cell edge and span its width at {setup}.");
    }

    private sealed record Setup(CanvasDevice Device, float Dpi, float CellWidth, float CellHeight, float GridTop)
    {
        private float Scale => Dpi / 96;
        internal int X(int column) => (int)MathF.Round(column * CellWidth * Scale);
        internal int Y(int row) => (int)MathF.Round((GridTop + row * CellHeight) * Scale);

        internal bool[,] Render(string[] screen, string family)
        {
            var columns = screen[0].Length;
            var cells = screen.SelectMany(line => line.Select(text =>
                new TerminalCell(text.ToString(), 1, White, Black, false, false, false, false, false, false))).ToArray();
            var frame = new TerminalFrame(columns, screen.Length, cells,
                new TerminalCursor(0, 0, false, false, TerminalCursorShape.Block, White), White, Black);
            using var target = new CanvasRenderTarget(Device, columns * CellWidth, GridTop + screen.Length * CellHeight, Dpi);
            using (var drawing = target.CreateDrawingSession())
                TerminalSurface.DrawFrame(drawing, frame, CellWidth, CellHeight, false, true, 14, fontFamily: family, hideCursor: true, gridTop: GridTop);
            var (width, height) = ((int)target.SizeInPixels.Width, (int)target.SizeInPixels.Height);
            var pixels = target.GetPixelColors();
            var lit = new bool[width, height];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                lit[x, y] = pixels[y * width + x].R > 128;
            return lit;
        }

        public override string ToString() => $"{Dpi} DPI, {CellWidth}x{CellHeight} DIP cells, grid top {GridTop}";
    }

    private static bool SequenceEqual(this bool[,] left, bool[,] right) =>
        left.GetLength(0) == right.GetLength(0) && left.GetLength(1) == right.GetLength(1) && left.Cast<bool>().SequenceEqual(right.Cast<bool>());
}
