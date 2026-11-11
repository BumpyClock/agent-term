using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.Graphics.Canvas;
using System.Text;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var terminal = new GhosttyTerminal(40, 12);
        terminal.Write(Encoding.UTF8.GetBytes("\x1b[38;2;230;230;230;48;2;12;12;12m\x1b[2J\x1b[H" +
            "Agent Term renderer\r\n" +
            "\x1b[3;1H\x1b[48;2;240;30;40m    \x1b[48;2;30;220;80m    \x1b[48;2;40;80;240m    \x1b[0m" +
            "\x1b[5;1H\x1b[1mBold\x1b[0m  \x1b[3mItalic\x1b[0m  \x1b[4mUnderline\x1b[0m" +
            "\x1b[7;1HUnicode: café 界 🙂 e\u0301" +
            "\x1b]12;#abcdef\x07\x1b[10;4H\x1b[2 q\x1b[?25h"));
        using var device = CanvasDevice.GetSharedDevice();
        using var image = new CanvasRenderTarget(device, 400, 240, 96);
        Draw(image, terminal.ReadFrame(), true);
        AssertPixel(image, 1, 41, 240, 30, 40, "truecolor red background");
        AssertPixel(image, 41, 41, 30, 220, 80, "truecolor green background");
        AssertPixel(image, 81, 41, 40, 80, 240, "truecolor blue background");
        AssertPixel(image, 35, 190, 171, 205, 239, "block cursor");
        if (!image.GetPixelColors(0, 0, 180, 20).Any(c => c.R > 100 && c.G > 100 && c.B > 100))
            throw new Exception("Renderer did not draw the title glyphs.");

        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/renderer-smoke.png");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        image.SaveAsync(output, CanvasBitmapFileFormat.Png).AsTask().GetAwaiter().GetResult();

        terminal.Write("\x1b[6 q"u8);
        Draw(image, terminal.ReadFrame(), true);
        AssertPixel(image, 30, 190, 171, 205, 239, "bar cursor");
        terminal.Write("\x1b[4 q"u8);
        Draw(image, terminal.ReadFrame(), true);
        AssertPixel(image, 35, 199, 171, 205, 239, "underline cursor");
        terminal.Write("\x1b[?25l"u8);
        var hidden = terminal.ReadFrame();
        Draw(image, hidden, true);
        var background = hidden.Cells[9 * hidden.Columns + 3].Background;
        AssertPixel(image, 35, 199, background.R, background.G, background.B, "hidden cursor");
        terminal.Write("\x1b[?25h\x1b[1 q"u8);
        var blinking = terminal.ReadFrame();
        Draw(image, blinking, false);
        AssertPixel(image, 35, 190, background.R, background.G, background.B, "cursor blink off phase");
        Draw(image, blinking, true);
        AssertPixel(image, 35, 190, 171, 205, 239, "cursor blink on phase");
        using (var drawing = image.CreateDrawingSession())
            TerminalSurface.DrawFrame(drawing, blinking, 10, 20, focused: false, cursorPhase: false);
        AssertPixel(image, 35, 190, background.R, background.G, background.B, "unfocused cursor hollow center");
        if (!image.GetPixelColors(30, 180, 10, 20).Any(c => c.R > 100 && c.G > 100 && c.B > 100))
            throw new Exception("Unfocused cursor outline must remain visible while blink phase is off.");

        terminal.BeginSelection(2, 2);
        terminal.ExtendSelection(5, 2);
        terminal.EndSelection();
        Draw(image, terminal.ReadFrame(), true);
        var selectedRed = image.GetPixelColors(21, 41, 1, 1)[0];
        var selectedGreen = image.GetPixelColors(51, 41, 1, 1)[0];
        if (selectedRed != selectedGreen || (selectedRed.R == 240 && selectedRed.G == 30 && selectedRed.B == 40))
            throw new Exception("Selection must visibly highlight cells across different terminal backgrounds.");
        AssertPixel(image, 1, 41, 240, 30, 40, "unselected cell beside selection");
        var selectionOutput = Path.Combine(Path.GetDirectoryName(output)!, "selection-smoke.png");
        image.SaveAsync(selectionOutput, CanvasBitmapFileFormat.Png).AsTask().GetAwaiter().GetResult();
        terminal.ClearSelection();
        Draw(image, terminal.ReadFrame(), true);
        AssertPixel(image, 21, 41, 240, 30, 40, "original background after clearing selection");
        terminal.Write("\x1b[0m\x1b[12;40H\x1b[K\x1b[?25l"u8);
        var acrylicFrame = terminal.ReadFrame();
        if (acrylicFrame.Cells[^1].Background != acrylicFrame.Background)
            throw new Exception("The acrylic fixture must include a default-background cell.");
        using (var drawing = image.CreateDrawingSession())
            TerminalSurface.DrawFrame(drawing, acrylicFrame, 10, 20, true, true, transparentBackground: true);
        if (image.GetPixelColors(391, 221, 1, 1)[0].A != 0)
            throw new Exception("Acrylic mode must leave the default background transparent.");
        var explicitColor = image.GetPixelColors(1, 41, 1, 1)[0];
        if (explicitColor.A != 255 || explicitColor.R != 240)
            throw new Exception("Acrylic mode must preserve opaque ANSI backgrounds.");
        Draw(image, terminal.ReadFrame(), true);
        if (image.GetPixelColors(391, 221, 1, 1)[0].A != 255)
            throw new Exception("Switching back to solid must restore an opaque background.");
        Console.WriteLine($"Renderer pixel checks passed. Snapshot: {output}");
    }

    private static void Draw(CanvasRenderTarget target, TerminalFrame frame, bool cursorPhase)
    {
        using var drawing = target.CreateDrawingSession();
        TerminalSurface.DrawFrame(drawing, frame, 10, 20, focused: true, cursorPhase);
    }

    private static void AssertPixel(CanvasRenderTarget image, int x, int y, byte r, byte g, byte b, string label)
    {
        var color = image.GetPixelColors(x, y, 1, 1)[0];
        if (color.R != r || color.G != g || color.B != b)
            throw new Exception($"Incorrect {label} at ({x},{y}). Expected ({r},{g},{b}), got {color}.");
    }
}
