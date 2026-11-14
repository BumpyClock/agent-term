using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.Graphics.Canvas;

internal static class DecorationRunChecks
{
    private const int Columns = 8;
    private const float CellWidth = 9, CellHeight = 19;

    internal static void Run(CanvasDevice device)
    {
        foreach (var style in new[] { TerminalUnderlineStyle.Curly, TerminalUnderlineStyle.Dotted, TerminalUnderlineStyle.Dashed })
        {
            var whole = Render(device, 96, style, first: 0);
            var partial = Render(device, 96, style, first: 3);
            var zone = (Top: (int)CellHeight - 5, Bottom: (int)CellHeight);
            for (var column = 4; column < Columns - 1; column++)
            {
                if (!Block(whole, column, zone).SequenceEqual(Block(whole, 5, zone)))
                    throw new Exception($"{style} underline must repeat the same pattern in every column, column {column} differs.");
                if (!Block(partial, column, zone).SequenceEqual(Block(whole, column, zone)))
                    throw new Exception($"{style} underline phase must stay anchored to column {column} wherever the run starts.");
            }
            var wide = Render(device, 96, style, first: 0, wide: true);
            var period = style switch { TerminalUnderlineStyle.Curly => 4, TerminalUnderlineStyle.Dotted => 2, _ => 6 };
            foreach (var column in WideColumns)
            {
                if (!Block(wide, column, zone).SequenceEqual(Block(whole, 5, zone)))
                    throw new Exception($"{style} underline on the wide cell at column {column} must start like a narrow cell.");
                for (var y = zone.Top; y < zone.Bottom; y++)
                    for (var x = 0; x + period < 2 * (int)CellWidth; x++)
                        if (Pixel(wide, column * (int)CellWidth + x, y) != Pixel(wide, column * (int)CellWidth + x + period, y))
                            throw new Exception($"{style} underline must run one pattern across the wide cell at column {column}.");
            }
        }
        foreach (var dpi in new[] { 96f, 144f })
        {
            var pixels = Render(device, dpi, TerminalUnderlineStyle.Single, first: 0);
            var scale = dpi / 96;
            var width = (int)(Columns * CellWidth * scale);
            foreach (var y in new[] { 1f, CellHeight / 2, CellHeight - 2 })
            {
                var row = (int)(y * scale);
                var line = pixels.AsSpan(row * width + (int)(CellWidth * scale), (int)((Columns - 2) * CellWidth * scale));
                foreach (var color in line)
                    if (color != line[0])
                        throw new Exception($"Straight decoration at y={y} must be continuous across cells at {dpi} DPI.");
                if (line[0].R < 80) throw new Exception($"Missing straight decoration at y={y} at {dpi} DPI.");
            }
        }
    }

    private static readonly int[] WideColumns = [0, 3, 5];

    private static Windows.UI.Color[] Render(CanvasDevice device, float dpi, TerminalUnderlineStyle style, int first, bool wide = false)
    {
        var white = new TerminalColor(255, 255, 255);
        var black = new TerminalColor(0, 0, 0);
        var red = new TerminalColor(255, 0, 0);
        var cells = new TerminalCell[Columns];
        for (var column = 0; column < Columns; column++)
            cells[column] = new TerminalCell(" ", 1, white, black, false, false, column >= first, true, false, false)
                { UnderlineStyle = style, UnderlineColor = red, Overline = true };
        if (wide)
            foreach (var column in WideColumns)
                (cells[column], cells[column + 1]) = (cells[column] with { Width = 2 }, cells[column + 1] with { Text = "", Width = 0 });
        var frame = new TerminalFrame(Columns, 1, cells, new TerminalCursor(0, 0, false, false, TerminalCursorShape.Block, white), white, black);
        using var image = new CanvasRenderTarget(device, Columns * CellWidth, CellHeight, dpi);
        using (var drawing = image.CreateDrawingSession()) TerminalSurface.DrawFrame(drawing, frame, CellWidth, CellHeight, true, true);
        return image.GetPixelColors();
    }

    private static Windows.UI.Color Pixel(Windows.UI.Color[] pixels, int x, int y) => pixels[y * (int)(Columns * CellWidth) + x];

    private static Windows.UI.Color[] Block(Windows.UI.Color[] pixels, int column, (int Top, int Bottom) zone)
    {
        var width = (int)(Columns * CellWidth);
        var block = new List<Windows.UI.Color>();
        for (var y = zone.Top; y < zone.Bottom; y++)
            block.AddRange(pixels.AsSpan(y * width + column * (int)CellWidth, (int)CellWidth).ToArray());
        return [.. block];
    }
}
