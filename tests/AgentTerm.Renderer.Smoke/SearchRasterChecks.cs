using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.Graphics.Canvas;

internal static class SearchRasterChecks
{
    internal static void Run(CanvasDevice device)
    {
        using var terminal = new GhosttyTerminal(8, 2);
        terminal.Write("界 界"u8);
        terminal.Find("界", true);
        var frame = terminal.ReadFrame();
        var span = frame.SearchHighlights!.Spans.Single(match => !match.Selected);
        using var image = new CanvasRenderTarget(device, 80, 40, 96);
        using (var drawing = image.CreateDrawingSession())
            TerminalSurface.DrawFrame(drawing, frame, 10, 20, false, false, transparentBackground: true, hideCursor: true);
        // Top-left pixels avoid glyph ink. Both head and continuation must show the match tint.
        var head = image.GetPixelColors(span.StartColumn * 10, 0, 1, 1)[0];
        var tail = image.GetPixelColors((span.StartColumn + 1) * 10, 0, 1, 1)[0];
        if (head.A == 0 || tail != head) throw new Exception("Search highlight did not cover a wide glyph's continuation.");
        using (var drawing = image.CreateDrawingSession())
            TerminalSurface.DrawFrame(drawing, frame with { SearchHighlights = null }, 10, 20, false, false, transparentBackground: true, hideCursor: true);
        if (image.GetPixelColors(span.StartColumn * 10, 0, 1, 1)[0].A != 0)
            throw new Exception("Clearing search must remove the highlight background.");
    }
}
