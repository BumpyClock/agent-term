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
        foreach (var style in Enum.GetValues<TerminalUnderlineStyle>().Where(x => x != TerminalUnderlineStyle.None))
        {
            var cell = new TerminalCell(" ", 1, white, black, false, false, true, false, false, false) { UnderlineStyle = style, UnderlineColor = red, Overline = true };
            var frame = new TerminalFrame(1, 1, [cell], new TerminalCursor(0, 0, false, false, TerminalCursorShape.Block, white), white, black);
            using (var drawing = image.CreateDrawingSession()) TerminalSurface.DrawFrame(drawing, frame, 60, 20, true, true);
            if (!image.GetPixelColors(0, 16, 60, 4).Any(c => c.R > 200 && c.G < 50)) throw new Exception($"Missing {style} underline color.");
            if (!image.GetPixelColors(0, 0, 60, 3).Any(c => c.R > 200 && c.G > 200)) throw new Exception("Missing overline.");
            var hidden = frame with { Cells = [cell with { Hidden = true, Selected = true }] };
            using (var drawing = image.CreateDrawingSession()) TerminalSurface.DrawFrame(drawing, hidden, 60, 20, true, true);
            if (image.GetPixelColors().Any(c => c.R > 200 && c.G < 50)) throw new Exception("Hidden selected text drew an underline.");
        }
        var wide = new TerminalCell("界", 2, white, black, false, false, false, false, false, false);
        var tail = wide with { Text = "", Width = 0 };
        var cursorFrame = new TerminalFrame(2, 1, [wide, tail], new TerminalCursor(1, 0, true, false, TerminalCursorShape.Hollow, red) { WideTail = true }, white, black);
        using (var drawing = image.CreateDrawingSession()) TerminalSurface.DrawFrame(drawing, cursorFrame, 30, 20, true, true);
        var pixels = image.GetPixelColors();
        if (!pixels.Any(c => c.R > 200 && c.G < 50)) throw new Exception("Missing hollow wide cursor.");
        if (pixels[10 * 60 + 50].R > 200 && pixels[10 * 60 + 50].G < 50) throw new Exception("Hollow cursor filled its center.");
    }
}
