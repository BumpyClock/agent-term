using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.Graphics.Canvas;

internal static class FidelityRasterChecks
{
    internal static void Run(CanvasDevice device)
    {
        var black = new TerminalColor(0, 0, 0);
        var white = new TerminalColor(255, 255, 255);
        var red = new TerminalColor(255, 0, 0);
        using var image = new CanvasRenderTarget(device, 60, 20, 96);
        var decorations = new Dictionary<TerminalUnderlineStyle, Windows.UI.Color[]>();
        foreach (var style in Enum.GetValues<TerminalUnderlineStyle>().Where(x => x != TerminalUnderlineStyle.None))
        {
            var cell = new TerminalCell(" ", 1, white, black, false, false, true, false, false, false) { UnderlineStyle = style, UnderlineColor = red, Overline = true };
            var frame = new TerminalFrame(1, 1, [cell], new TerminalCursor(0, 0, false, false, TerminalCursorShape.Block, white), white, black);
            using (var drawing = image.CreateDrawingSession()) TerminalSurface.DrawFrame(drawing, frame, 60, 20, true, true);
            decorations[style] = image.GetPixelColors();
            if (!image.GetPixelColors(0, 16, 60, 4).Any(c => c.R > 80 && c.G < 50)) throw new Exception($"Missing {style} underline color.");
            if (!image.GetPixelColors(0, 0, 60, 3).Any(c => c.R > 80 && c.G > 80)) throw new Exception("Missing overline.");
            var hidden = frame.WithCells([cell with { Hidden = true, Selected = true }]);
            using (var drawing = image.CreateDrawingSession()) TerminalSurface.DrawFrame(drawing, hidden, 60, 20, true, true);
            if (image.GetPixelColors().Any(c => c.R > 80 && c.G < 50)) throw new Exception("Hidden selected text drew an underline.");
        }
        foreach (var first in decorations.Keys)
        foreach (var second in decorations.Keys.Where(style => style > first))
            if (decorations[first].SequenceEqual(decorations[second]))
                throw new Exception($"{first} and {second} must render distinct decorations.");

        foreach (var text in new[] { "e\u0301", "界", "👩‍💻" })
        foreach (var selected in new[] { false, true })
        {
            var cell = new TerminalCell(text, 1, white, black, false, false, true, true, false, false)
            {
                Blink = true, Selected = selected, Overline = true,
                UnderlineStyle = TerminalUnderlineStyle.Curly, UnderlineColor = red
            };
            var frame = new TerminalFrame(1, 1, [cell], new TerminalCursor(0, 0, false, false, TerminalCursorShape.Block, white), white, black)
            {
                SelectionForeground = new TerminalColor(0, 255, 0),
                SelectionBackground = new TerminalColor(0, 0, 80)
            };
            using (var drawing = image.CreateDrawingSession()) TerminalSurface.DrawFrame(drawing, frame, 60, 20, true, true, textPhase: true);
            var on = image.GetPixelColors();
            using (var drawing = image.CreateDrawingSession()) TerminalSurface.DrawFrame(drawing, frame, 60, 20, true, true, textPhase: false);
            var off = image.GetPixelColors();
            var expectedBackground = selected ? frame.SelectionBackground : black;
            if (on.SequenceEqual(off) || off.Any(c => c.R != expectedBackground.R || c.G != expectedBackground.G || c.B != expectedBackground.B))
                throw new Exception($"Blink must hide all glyphs and decorations, preserving background for {text}, selected={selected}.");
            if (selected && on.Any(c => c.R > 80 && c.G < 50))
                throw new Exception("Selection must override the explicit underline color.");
            var hidden = frame.WithCells([cell with { Hidden = true }]);
            using (var drawing = image.CreateDrawingSession()) TerminalSurface.DrawFrame(drawing, hidden, 60, 20, true, true);
            if (!image.GetPixelColors().SequenceEqual(off))
                throw new Exception("Hidden and blink-off text must preserve the same background.");
        }
        var decorated = new TerminalCell(" ", 1, white, black, false, false, true, true, false, false)
            { Overline = true, UnderlineStyle = TerminalUnderlineStyle.Double, UnderlineColor = red };
        var block = new TerminalFrame(1, 1, [decorated], new TerminalCursor(0, 0, true, false, TerminalCursorShape.Block, white), white, black)
            { CursorText = new TerminalColor(0, 0, 255) };
        using (var drawing = image.CreateDrawingSession()) TerminalSurface.DrawFrame(drawing, block, 60, 20, true, true);
        foreach (var y in new[] { 0, 9, 15, 17 })
            if (!image.GetPixelColors(2, y, 55, 2).Any(c => c.B > c.R + 60 && c.B > c.G + 60))
                throw new Exception($"Block cursor lost its cell decoration at y={y}.");
        var wide = new TerminalCell("界", 2, white, black, false, false, false, false, false, false);
        var tail = wide with { Text = "", Width = 0 };
        var cursorFrame = new TerminalFrame(2, 1, [wide, tail], new TerminalCursor(1, 0, true, false, TerminalCursorShape.Hollow, red) { WideTail = true }, white, black);
        using (var drawing = image.CreateDrawingSession()) TerminalSurface.DrawFrame(drawing, cursorFrame, 30, 20, true, true);
        var pixels = image.GetPixelColors();
        if (!pixels.Any(c => c.R > 200 && c.G < 50)) throw new Exception("Missing hollow wide cursor.");
        if (pixels[10 * 60 + 50].R > 200 && pixels[10 * 60 + 50].G < 50) throw new Exception("Hollow cursor filled its center.");
    }
}
