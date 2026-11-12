using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

internal static partial class Program
{
    private static async Task VerifyTransitionsAsync(Window window, TerminalSurface surface, string output)
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        var canvasField = typeof(TerminalSurface).GetField("_canvas", Private)!;
        var retainedField = typeof(TerminalSurface).GetField("_renderTarget", Private)
            ?? throw new Exception("This renderer has no retained raster to verify.");
        var suspend = typeof(TerminalSurface).GetMethod("SuspendRendering", Private)
            ?? throw new Exception("This renderer has no suspend path to verify.");
        var resume = typeof(TerminalSurface).GetMethod("ResumeRendering", Private)!;
        var glyphCache = GlyphCacheProbe.For(surface) ?? throw new Exception("This renderer has no glyph cache to verify.");
        using var terminal = new GhosttyTerminal(
            (ushort)Math.Floor(surface.ActualWidth / surface.CellWidth), (ushort)Math.Floor(surface.ActualHeight / surface.CellHeight));
        terminal.SetBottomAnchored(true);
        var cases = new List<string>();
        var transparent = true;
        var fontSize = 14f;
        terminal.SetTheme(TerminalThemes.Resolve("agent-dark", false));
        Write("\x1b[?1049h\x1b[2J\x1b[?25lSparse edits and cached glyphs\r\n\x1b[1;31mBold red\x1b[0m \x1b[3mItalic\x1b[0m é 界 🙂\r\nLast fixture row");
        await Check("initial");
        Write("\x1b[1;8HX"); await Check("sparse-edit");
        Write("\x1b[2;1H\x1b[K"); await Check("erase-row");
        Write("\x1b[3;2H界"); await Check("wide-glyph");
        Write("\x1b[3;2Hx "); await Check("wide-to-narrow");
        Write("\x1b[5;1Hcombining é ö å"); await Check("combining-written");
        Write("\x1b[5;11Hũ"); await Check("combining-partial-edit");
        terminal.BeginSelection(0, 0); terminal.ExtendSelection(9, 0); terminal.EndSelection(); await Check("selection-first");
        terminal.ClearSelection(); terminal.BeginSelection(2, 2); terminal.ExtendSelection(8, 2); terminal.EndSelection(); await Check("selection-moved");
        terminal.BeginSelection(0, 1); terminal.ExtendSelection(4, 1);
        Write("\x1b[6;1Houtput while selecting");
        terminal.ExtendSelection(12, 1); await Check("selection-extended-during-output");
        terminal.EndSelection(); terminal.ClearSelection(); await Check("selection-cleared");
        var before = terminal.ReadFrame();
        Write("\x1b[4;20H");
        var moved = terminal.ReadFrame();
        if (moved.Cursor == before.Cursor || !moved.Cells.SequenceEqual(before.Cells))
            throw new Exception("Cursor-only movement did not isolate a cursor change.");
        await Check("cursor-only");
        terminal.SetTheme(TerminalThemes.Resolve("agent-light", false)); await Check("theme-and-default-background");
        Write("\x1b[4;1H\x1b[48;2;20;80;140mColor band\x1b[0m"); await Check("colored-background");
        surface.SetAcrylicBackground(false); transparent = false; await Check("solid-background");
        surface.SetAcrylicBackground(true); transparent = true; await Check("transparent-background");

        suspend.Invoke(surface, null);
        if (retainedField.GetValue(surface) is not null || glyphCache.Count != 0)
            throw new Exception("SuspendRendering kept the retained raster or glyph layouts.");
        Write("\x1b[7;1Hwritten while suspended");
        resume.Invoke(surface, null);
        await Check("suspend-resume");

        Write("\x1b[?1049l\x1b[2J\x1b[H");
        for (var line = 0; line < 200; line++) Write($"history line {line:000} \x1b[3{line % 7 + 1}mcolor\x1b[0m\r\n");
        await Check("history-bottom");
        var bottomOffset = terminal.ReadFrame().Viewport.Offset;
        terminal.ScrollLines(-37);
        if (terminal.ReadFrame().Viewport.Offset != bottomOffset - 37) throw new Exception("History did not scroll by 37 rows.");
        await Check("history-scrolled-up");
        Write("live output while scrolled\r\n"); await Check("history-output-while-scrolled");
        terminal.ScrollLines(11); await Check("history-scrolled-down");
        terminal.ScrollToBottom(); await Check("history-returned");

        var glyphsPerScreen = Math.Min(terminal.ReadFrame().Cells.Length / 2 - 1, 20000);
        var capacity = glyphCache.Capacity;
        var createdBefore = glyphCache.Created;
        string[] styles = ["\x1b[0m", "\x1b[0;1m", "\x1b[0;3m", "\x1b[0;1;3m"];
        var cjkScreens = 0;
        for (; glyphCache.Created - createdBefore <= capacity + glyphsPerScreen && cjkScreens < 64; cjkScreens++)
        {
            var text = new StringBuilder("\x1b[H").Append(styles[cjkScreens % 4]);
            var first = cjkScreens / 4 * glyphsPerScreen;
            for (var index = 0; index < glyphsPerScreen; index++) text.Append((char)(0x4E00 + (first + index) % 20000));
            Write(text.Append("\x1b[0m").ToString());
            await Check($"cjk-cache-{cjkScreens}");
            if (glyphCache.Count > glyphCache.Capacity) throw new Exception($"Glyph cache grew to {glyphCache.Count} above {glyphCache.Capacity}.");
        }
        var cjkCreated = glyphCache.Created - createdBefore;
        if (cjkCreated <= capacity) throw new Exception($"CJK screens created {cjkCreated} layouts and never overflowed capacity {capacity}.");

        fontSize = 17; surface.SetTerminalFont(fontSize, "FiraCode Nerd Font"); await Check("font-zoom");
        fontSize = 14; surface.SetTerminalFont(fontSize, "FiraCode Nerd Font"); await Check("font-restore");
        (window.AppWindow.Presenter as OverlappedPresenter)?.Restore();
        window.AppWindow.Resize(new SizeInt32(1001, 733));
        await Task.Delay(150);
        terminal.Resize((ushort)Math.Max(2, Math.Floor(surface.ActualWidth / surface.CellWidth)),
            (ushort)Math.Max(1, Math.Floor(surface.ActualHeight / surface.CellHeight)));
        await Check("resize");
        Write("\x1b[1;1H\x1b[2JAfter clear"); await Check("clear-screen");
        Write("\x1b[?5h"); await Check("reverse-video");
        Write("\x1b[?5l"); await Check("reverse-restored");
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            passed = cases.Count, cases, glyphCache = new { capacity, cjkScreens, cjkCreated, finalLayouts = glyphCache.Count }
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Retained raster parity passed {cases.Count} transitions.");

        void Write(string value) => terminal.Write(Encoding.UTF8.GetBytes(value));
        async Task Check(string name)
        {
            var canvas = (CanvasControl)canvasField.GetValue(surface)!;
            var frame = terminal.ReadFrame();
            var drawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Completed(CanvasControl _, CanvasDrawEventArgs __) => drawn.TrySetResult();
            canvas.Draw += Completed;
            try
            {
                surface.SetFrame(frame);
                canvas.Invalidate();
                await drawn.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { canvas.Draw -= Completed; }
            var actual = retainedField.GetValue(surface) as CanvasRenderTarget ?? throw new Exception($"Retained target missing after {name}.");
            using var expected = new CanvasRenderTarget(canvas, (float)surface.ActualWidth, (float)surface.ActualHeight, canvas.Dpi);
            using (var drawing = expected.CreateDrawingSession())
                TerminalSurface.DrawFrame(drawing, frame, surface.CellWidth, surface.CellHeight, true, true, fontSize,
                    transparent, surface.TerminalFontFamily, hideCursor: true,
                    gridTop: (float)TerminalGridGeometry.For(frame, surface.ActualHeight, surface.CellWidth, surface.CellHeight, true).Top);
            var expectedPixels = expected.GetPixelBytes();
            var actualPixels = actual.GetPixelBytes();
            if (!actualPixels.SequenceEqual(expectedPixels))
            {
                await actual.SaveAsync(output + $".{name}.actual.png", CanvasBitmapFileFormat.Png);
                await expected.SaveAsync(output + $".{name}.expected.png", CanvasBitmapFileFormat.Png);
                var differences = actualPixels.Zip(expectedPixels).Count(pair => pair.First != pair.Second);
                throw new Exception($"Retained raster differs after {name}. Changed bytes {differences}; size {actual.SizeInPixels} vs {expected.SizeInPixels}.");
            }
            cases.Add(name);
        }
    }
}
