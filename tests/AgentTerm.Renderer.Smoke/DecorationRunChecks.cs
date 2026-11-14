using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.Graphics.Canvas;
using System.Text;

internal static class DecorationRunChecks
{
    private const int Columns = 8;
    private const float CellWidth = 9, CellHeight = 19;

    internal static void Run(CanvasDevice device, string? outputDirectory = null)
    {
        DoubleUnderline(device, outputDirectory);
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

    private static void DoubleUnderline(CanvasDevice device, string? outputDirectory)
    {
        using var terminal = new GhosttyTerminal(Columns, 3);
        terminal.Write(Encoding.UTF8.GetBytes("\x1b[?25l\x1b[48;2;0;0;0m\x1b[2J\x1b[H\x1b[58;2;255;0;0m" +
            "\x1b[4:1m        \x1b[2;1H\x1b[4:2m        \x1b[3;1H        "));
        var frame = terminal.ReadFrame();
        if (frame.Cells[Columns].UnderlineStyle != TerminalUnderlineStyle.Double)
            throw new Exception("SGR 4:2 fixture must reach the renderer as double underline.");
        if (outputDirectory is not null) Directory.CreateDirectory(outputDirectory);
        foreach (var (dpi, strokePixels) in new[] { (96f, 1), (120f, 1), (144f, 2), (192f, 2) })
        foreach (var (cellWidth, cellHeight) in new[] { (8.4f, 19f), (7.7f, 17.5f) })
        foreach (var gridTop in new[] { 0f, 3.25f })
        foreach (var family in new[] { "Consolas", "Cascadia Mono" })
        {
            var context = $"{dpi} DPI, {cellWidth}x{cellHeight} DIP cells, grid top {gridTop}, {family}";
            using var image = new CanvasRenderTarget(device, Columns * cellWidth, gridTop + 3 * cellHeight, dpi);
            using (var drawing = image.CreateDrawingSession())
                TerminalSurface.DrawFrame(drawing, frame, cellWidth, cellHeight, false, true, fontFamily: family, gridTop: gridTop);
            if (outputDirectory is not null)
                image.SaveAsync(Path.GetFullPath(Path.Combine(outputDirectory, $"underline-{dpi}-{cellHeight}-{gridTop}-{family}.png")),
                    CanvasBitmapFileFormat.Png).AsTask().GetAwaiter().GetResult();
            var pixels = image.GetPixelColors();
            var width = (int)image.SizeInPixels.Width;
            var scale = dpi / 96;
            for (var row = 1; row < 3; row++)
            {
                var top = (int)MathF.Ceiling((gridTop + row * cellHeight) * scale);
                var bottom = (int)MathF.Round((gridTop + (row + 1) * cellHeight) * scale);
                int? upper = null, gap = null, lower = null, end = null;
                for (var y = top; y < bottom; y++)
                {
                    var color = pixels[y * width + width / 2];
                    if (color.R != 0)
                    {
                        if (upper is null) upper = y;
                        else if (gap is not null && lower is null) lower = y;
                        else if (end is not null) throw new Exception($"Double underline has more than two strokes, {context}.");
                    }
                    else if (upper is not null && gap is null) gap = y;
                    else if (lower is not null && end is null) end = y;
                }
                end ??= bottom;
                if (upper is null || gap is null || lower is null || gap - upper != strokePixels || gap - upper != lower - gap || end - lower != gap - upper)
                    throw new Exception($"Double underline needs two equal strokes separated by a full background gap of the same thickness. " +
                        $"Got upper={upper}, gap={gap}, lower={lower}, end={end}, row {row}, {context}.");
                for (var y = upper.Value; y < end; y++)
                {
                    var color = pixels[y * width + width / 2];
                    if (color.A != 255 || color.R != (y >= gap && y < lower ? 0 : 255) || color.G != 0 || color.B != 0)
                        throw new Exception($"Double underline must have opaque pixel-aligned strokes and a background-only gap, got {color} at y={y}, {context}.");
                    for (var x = (int)MathF.Ceiling(cellWidth * scale); x < (Columns - 1) * cellWidth * scale; x++)
                        if (pixels[y * width + x] != color)
                            throw new Exception($"Double underline must be continuous across cells at ({x},{y}), {context}.");
                }
                if (outputDirectory is not null)
                    Console.WriteLine($"Double underline pixels passed. Strokes {gap - upper}px, gap {lower - gap}px, row {row}, {context}.");
            }
        }
        Console.WriteLine("Double underlines have two opaque strokes separated by equal background gaps.");
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
