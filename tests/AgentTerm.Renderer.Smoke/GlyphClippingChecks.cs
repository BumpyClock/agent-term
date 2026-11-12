using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;

internal static class GlyphClippingChecks
{
    internal static void Run(CanvasDevice device)
    {
        var background = new TerminalColor(12, 12, 12);
        var foreground = new TerminalColor(240, 240, 240);
        var cursorColor = new TerminalColor(30, 80, 140);
        foreach (var text in new[] { "W", "e\u0301", "界", "🙂" })
        foreach (var bold in new[] { false, true })
        foreach (var italic in new[] { false, true })
        foreach (var cursor in new[] { false, true })
        foreach (var cellWidth in new[] { 4f, 8.4f, 10f })
        foreach (var dpi in new[] { 96f, 120f, 144f })
        {
            var wide = text is "界" or "🙂";
            var cells = Enumerable.Repeat(new TerminalCell(" ", 1, foreground, background,
                false, false, false, false, false, false), 8).ToArray();
            cells[1] = cells[1] with { Text = text, Width = (byte)(wide ? 2 : 1), Bold = bold, Italic = italic };
            if (wide) cells[2] = cells[2] with { Width = 0 };
            var frame = new TerminalFrame(4, 2, cells,
                new TerminalCursor(1, 0, cursor, false, TerminalCursorShape.Block, cursorColor), foreground, background);
            using var actual = new CanvasRenderTarget(device, 4 * cellWidth, 40, dpi);
            using (var drawing = actual.CreateDrawingSession())
                TerminalSurface.DrawFrame(drawing, frame, cellWidth, 20, true, true);

            using var expected = new CanvasRenderTarget(device, 4 * cellWidth, 40, dpi);
            using (var drawing = expected.CreateDrawingSession())
            using (var format = new CanvasTextFormat
            {
                FontFamily = "Consolas", FontSize = 14,
                FontWeight = new FontWeight { Weight = (ushort)(bold ? 700 : 400) },
                FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
                WordWrapping = CanvasWordWrapping.NoWrap
            })
            {
                drawing.Clear(Color.FromArgb(255, 12, 12, 12));
                var bounds = new Rect(cellWidth, 0, cellWidth * (wide ? 2 : 1), 20);
                using (drawing.CreateLayer(1, bounds))
                    drawing.DrawText(text, cellWidth, 0, Color.FromArgb(255, 240, 240, 240), format);
                if (cursor)
                {
                    drawing.FillRectangle(bounds, Color.FromArgb(255, 30, 80, 140));
                    using (drawing.CreateLayer(1, bounds))
                        drawing.DrawText(text, cellWidth, 0, Color.FromArgb(255, 12, 12, 12), format);
                }
            }
            var pixels = actual.GetPixelColors();
            var plain = cursor ? Color.FromArgb(255, 30, 80, 140) : Color.FromArgb(255, 12, 12, 12);
            var scale = dpi / 96;
            var left = (int)Math.Ceiling(cellWidth * scale);
            var right = (int)Math.Floor(cellWidth * (wide ? 3 : 2) * scale);
            if (!actual.GetPixelColors(left, 0, right - left, (int)(20 * scale)).Any(pixel => pixel != plain))
                throw new Exception($"The {text} fixture must draw visible glyph pixels, cursor={cursor}.");
            if (!pixels.SequenceEqual(expected.GetPixelColors()))
                throw new Exception($"Glyph origin or clipping changed for {text}, bold={bold}, italic={italic}, cursor={cursor}, cellWidth={cellWidth}, dpi={dpi}.");
        }
        Console.WriteLine("Glyph and cursor clipping checks passed.");
    }
}
