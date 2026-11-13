using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

internal static class SmoothScrollRasterChecks
{
    internal static void Run(CanvasDevice device)
    {
        using var terminal = new GhosttyTerminal(2, 3);
        terminal.Write("\x1b[41m  \r\n\x1b[42m  \r\n\x1b[43m  \r\n\x1b[44m  \r\n\x1b[48;2;255;0;255m  "u8);
        terminal.ScrollTo(1);
        terminal.SetOverscanEnabled(true);
        var frame = terminal.ReadFrame();
        if (frame.OverscanBelow.Length != 1) throw new Exception("Fixture needs a real native overscan row.");
        var geometry = TerminalGridGeometry.For(frame, 60, 10, 20, false, .5);
        if (geometry.Top != -10 || geometry.RowTop(2) != 30 || geometry.Caret(frame.Cursor).Y != geometry.RowTop(Math.Clamp(frame.Cursor.Row, 0, 2)))
            throw new Exception("Caret and anchors must use the fractional grid origin.");
        if (!geometry.TryCellAt(new Point(5, 15), out var column, out var row) || column != 0 || row != 1 || geometry.ToFrame(new Point(5, 15)).Y != 25)
            throw new Exception("Selection and reporting coordinates must use the fractional origin.");
        using var image = new CanvasRenderTarget(device, 20, 60, 96);
        using (var drawing = image.CreateDrawingSession())
            TerminalSurface.DrawFrame(drawing, frame, 10, 20, false, true, hideCursor: true, gridTop: (float)geometry.Top);
        var bottom = image.GetPixelColors(0, 51, 20, 9);
        if (bottom.Any(c => c.R != 255 || c.G != 0 || c.B != 255))
            throw new Exception("The partly visible native overscan row must fill the lower edge without a gap.");
        terminal.BeginSelection(0, 1);
        terminal.ExtendSelection(1, 1);
        if (!terminal.ReadFrame().Cells.Skip(2).Take(2).All(c => c.Selected))
            throw new Exception("Selection must retain native cell ownership during scrolling.");
        Console.WriteLine("Smooth scroll raster and coordinate checks passed.");
    }
}
