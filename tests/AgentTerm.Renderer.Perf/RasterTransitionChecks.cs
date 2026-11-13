using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

internal static partial class Program
{
    private static string _onScreenOutput = "";

    private static async Task VerifyTransitionsAsync(Window window, TerminalSurface surface, string output)
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        if (!SurfaceProbe.WorkerOwned) throw new Exception("This renderer has no worker-owned raster to verify.");
        _onScreenOutput = output;
        var probe = new SurfaceProbe(surface);
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
        terminal.Find("row", true); await Check("search-highlight");
        terminal.Find("glyphs", true); await Check("search-highlight-moved");
        terminal.Find("", true); terminal.ClearSelection(); await Check("search-highlight-cleared");
        Write("\x1b[1;8HX"); await Check("sparse-edit");
        await VerifyOnScreenAsync(window, surface, probe, "the pane");
        Write("\x1b[1;1Hdrop");
        terminal.ReadFrame();
        Write("\x1b[2;1Hnext"); await Check("dropped-intermediate-frame");
        Write("\x1b[2;1H\x1b[K"); await Check("erase-row");
        Write("\x1b[3;2H界"); await Check("wide-glyph");
        await VerifyOnScreenAsync(window, surface, probe, "the pane after an offset partial edit");
        Write("\x1b[3;2Hx "); await Check("wide-to-narrow");
        Write("\x1b[5;1Hcombining é ö å"); await Check("combining-written");
        Write("\x1b[5;11Hũ"); await Check("combining-partial-edit");
        Write("\x1b[8;1Hx => y != z <!-- --> === ---------->"); await Check("ligatures-written");
        Write("\x1b[8;36H-"); await Check("ligature-arrow-head-edit");
        Write("\x1b[8;36H>"); await Check("ligature-context-restored");
        Write("\x1b[8;3H\x1b[31m=\x1b[0m"); await Check("ligature-style-split");
        Write("\x1b[8;4H "); await Check("ligature-partial-erase");
        terminal.BeginSelection(22, 7); terminal.ExtendSelection(23, 7); terminal.EndSelection(); await Check("ligature-selection-edge");
        terminal.ClearSelection(); await Check("ligature-selection-cleared");
        Write("\x1b[8;1H\x1b[K===abcdefghijk"); await Check("ligature-before-run-rejection");
        Write("\x1b[8;14H\u0633"); await Check("ligature-run-rejected-by-distant-rtl");
        Write("\x1b[8;14Hk"); await Check("ligature-run-accepted-after-distant-edit");
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
        await VerifyOnScreenAsync(window, surface, probe, "the opaque pane");
        surface.SetAcrylicBackground(true); transparent = true; await Check("transparent-background");

        terminal.SetImageStorageLimit(TerminalImages.DefaultStorageLimit);
        terminal.SetCellPixelSize(10, 20);
        Write("\x1b[?25l\x1b[H\x1b[2JImage damage fixture\x1b[3;1H\x1b[48;2;12;12;12mexplicit background\x1b[0m");
        Image(1, 2, 20, -2147483648, 255, 0, 0, 128);
        Image(2, 5, 30, -1, 0, 255, 0, 128);
        Image(3, 8, 40, 0, 0, 0, 255, 128);
        if (terminal.ReadFrame().Images.Placements.Count != 3) throw new Exception("Image damage fixture requires three visible placements.");
        await Check("images-three-layers");
        var statsProperty = typeof(TerminalSurface).GetProperty("PresentedRasterStats", Private)!;
        long ImageRequests() => statsProperty.GetValue(surface) is { } stats ? (long)stats.GetType().GetProperty("ImageRequests")!.GetValue(stats)! : 0;
        var requests = ImageRequests();
        Write("\x1b[1;1HX\x1b[5;1HY\x1b[9;1HZ"); await Check("images-unrelated-sparse-text");
        var unrelatedRequests = ImageRequests() - requests;
        if (unrelatedRequests != 0) throw new Exception($"Unrelated text damage accessed the image cache {unrelatedRequests} times.");
        requests = ImageRequests();
        Image(2, 5, 30, -1, 255, 255, 0, 128); await Check("images-small-animation");
        var animationRequests = ImageRequests() - requests;
        if (animationRequests != 1) throw new Exception($"One changed image should access only its bitmap, got {animationRequests} accesses.");
        Image(3, 8, 41, 0, 255, 0, 255, 128); await Check("images-moved");
        Image(3, 5, 30, 0, 255, 0, 255, 128); await Check("images-overlapping-alpha");
        await VerifyOnScreenAsync(window, surface, probe, "the pane with translucent images");
        Image(2, 5, 30, -1, 0, 128, 255, 128); await Check("images-overlapping-animation");
        var imageFrame = terminal.ReadFrame();
        var duplicate = imageFrame.Images.Placements[^1];
        await Check("images-duplicate-alpha", imageFrame with { Images = imageFrame.Images with
            { Placements = [.. imageFrame.Images.Placements, duplicate] } });
        await Check("images-duplicate-removed");
        await Check("images-scale-changed", imageFrame with { Images = imageFrame.Images with
            { CellPixelWidth = 20, CellPixelHeight = 40 } });
        await Check("images-scale-restored");
        surface.SetAcrylicBackground(false); transparent = false; await Check("images-solid");
        Image(3, 6, 31, 0, 128, 0, 255, 128); await Check("images-solid-moved");
        surface.SetAcrylicBackground(true); transparent = true; await Check("images-acrylic-restored");
        Write("\x1b_Ga=d,d=I,i=1,q=2\x1b\\"); await Check("images-deleted-below-background");
        terminal.BeginSelection(29, 4); terminal.ExtendSelection(32, 5); terminal.EndSelection(); await Check("images-selection");
        Image(2, 5, 30, -1, 0, 255, 255, 192); await Check("images-animation-with-selection");
        terminal.ClearSelection();
        Write("\x1b_Ga=d,d=A,q=2\x1b\\"); await Check("images-all-deleted");
        Console.WriteLine($"Image damage cache accesses: unrelated text {unrelatedRequests}, one animation {animationRequests}.");

        await VerifyHeldPaneAsync(window, surface, probe, Write, () => terminal.ReadFrame());
        await Check("after-held-pane");
        await VerifyOnScreenAsync(window, surface, probe, "pane A after its held device was released");
        await VerifyClosedWhileHeldAsync(window, surface, probe, suspend, resume, Write, () => terminal.ReadFrame());
        await Check("reopened-after-close-while-held");
        await VerifyOnScreenAsync(window, surface, probe, "pane A reopened after closing while held");

        var presenter = (OverlappedPresenter)window.AppWindow.Presenter;
        var restoreMaximized = presenter.State == OverlappedPresenterState.Maximized;
        presenter.Minimize();
        await WaitForAsync(() => presenter.State == OverlappedPresenterState.Minimized, "the fixture window to minimize");
        for (var frame = 0; frame < 6; frame++)
        {
            Write($"[8;1Hminimized present {frame}");
            var minimized = terminal.ReadFrame();
            surface.SetFrame(minimized);
            await probe.WhenPresentedAsync(minimized, $"minimized present {frame}");
        }
        if (restoreMaximized) presenter.Maximize(); else presenter.Restore();
        await WaitForAsync(() => presenter.State != OverlappedPresenterState.Minimized, "the fixture window to restore");
        Console.WriteLine("Six presentations completed while the fixture window was minimized.");
        await VerifyOnScreenAsync(window, surface, probe, "the pane restored after minimized presents");

        var visible = terminal.ReadFrame();
        var edgeCells = visible.Cells.Take(visible.Columns).Select(cell => cell with { Text = "A", Width = 1 }).ToArray();
        var edge = visible with { OverscanAbove = [new(-1, edgeCells)] };
        surface.SetScrollFraction(0.5);
        await Check("overscan-shared-grid-initial", edge);
        var changedEdge = edge with { OverscanAbove = [new(-1, edgeCells.Select(cell => cell with { Text = "Z" }).ToArray())] };
        await Check("overscan-shared-grid-edited", changedEdge);
        await Check("overscan-shared-grid-removed", visible);
        surface.SetScrollFraction(-0.5);
        var lowerEdge = visible with { OverscanBelow = [new(visible.Rows, edgeCells)] };
        await Check("lower-overscan-shared-grid-initial", lowerEdge);
        await Check("lower-overscan-shared-grid-edited", lowerEdge with
        {
            OverscanBelow = [new(visible.Rows, edgeCells.Select(cell => cell with { Text = "Z" }).ToArray())]
        });
        await Check("lower-overscan-shared-grid-removed", visible);
        surface.SetScrollFraction(0);
        await Check("overscan-fraction-restored", visible);

        var rasterizer = RasterizerOf(surface);
        suspend.Invoke(surface, null);
        if (probe.HasResources || glyphCache.Count != 0)
            throw new Exception("SuspendRendering kept the raster owner or its presented glyph statistics.");
        await WaitForAsync(() => RasterizerReleased(rasterizer), "the suspended rasterizer to release its device resources");
        Write("\x1b[7;1Hwritten while suspended");
        resume.Invoke(surface, null);
        await Check("suspend-resume");
        await VerifyOnScreenAsync(window, surface, probe, "the resumed pane");

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
        await VerifyOnScreenAsync(window, surface, probe, "the pane after history scrolling");

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
            if (glyphCache.Cells > glyphCache.Capacity) throw new Exception($"Glyph cache grew to {glyphCache.Cells} cells above {glyphCache.Capacity}.");
        }
        var cjkCreated = glyphCache.Created - createdBefore;
        if (cjkCreated <= capacity) throw new Exception($"CJK screens created {cjkCreated} layouts and never overflowed capacity {capacity}.");

        if (glyphCache.Count <= 1024) throw new Exception("CJK shrink fixture did not fill more than the minimum cache capacity.");
        terminal.Resize(16, 8);
        await Check("cjk-cache-shrink");
        if (glyphCache.Capacity != 1024 || glyphCache.Cells > glyphCache.Capacity)
            throw new Exception($"Shrinking the grid retained {glyphCache.Cells} cells above the new capacity {glyphCache.Capacity}.");

        fontSize = 17; surface.SetTerminalFont(fontSize, "FiraCode Nerd Font"); await Check("font-zoom");
        fontSize = 14; surface.SetTerminalFont(fontSize, "FiraCode Nerd Font"); await Check("font-restore");
        (window.AppWindow.Presenter as OverlappedPresenter)?.Restore();
        window.AppWindow.Resize(new SizeInt32(1001, 733));
        await Task.Delay(150);
        terminal.Resize((ushort)Math.Max(2, Math.Floor(surface.ActualWidth / surface.CellWidth)),
            (ushort)Math.Max(1, Math.Floor(surface.ActualHeight / surface.CellHeight)));
        await Check("resize");
        await VerifyOnScreenAsync(window, surface, probe, "the resized pane");
        Write("\x1b[1;1H\x1b[2JAfter clear"); await Check("clear-screen");
        Write("\x1b[?5h"); await Check("reverse-video");
        Write("\x1b[?5l"); await Check("reverse-restored");
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            passed = cases.Count, cases, imageDamage = new { unrelatedRequests, animationRequests },
            glyphCache = new { capacity, cjkScreens, cjkCreated, finalLayouts = glyphCache.Count, finalCells = glyphCache.Cells }
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Retained raster parity passed {cases.Count} transitions.");

        void Write(string value) => terminal.Write(Encoding.UTF8.GetBytes(value));
        void Image(int id, int row, int column, int z, byte r, byte g, byte b, byte alpha) =>
            Write($"\x1b[{row};{column}H\x1b_Ga=T,f=32,s=1,v=1,c=2,r=2,C=1,i={id},z={z},q=2;{Convert.ToBase64String([r, g, b, alpha])}\x1b\\");
        async Task Check(string name, TerminalFrame? snapshot = null)
        {
            var frame = snapshot ?? terminal.ReadFrame();
            surface.SetFrame(frame);
            await probe.WhenPresentedAsync(frame, name);
            var readback = await probe.ReadRasterAsync() ?? throw new Exception($"Retained raster missing after {name}.");
            var gridTop = (float)surface.GeometryFor(frame).Top;
            if (readback.Frame is not { } painted || !ReferenceEquals(painted.Cells, frame.Cells) || !ReferenceEquals(painted.Images, frame.Images) ||
                !ReferenceEquals(painted.SearchHighlights, frame.SearchHighlights) || painted.Background != frame.Background ||
                readback.Transparent != transparent || readback.FontSize != fontSize || readback.Dpi != probe.Dpi || readback.GridTop != gridTop)
                throw new Exception($"The readback after {name} was painted from a different frame or metrics.");
            var actualPixels = readback.Pixels;
            var device = CanvasDevice.GetSharedDevice();
            using var expected = new CanvasRenderTarget(device, (float)surface.ActualWidth, (float)surface.ActualHeight, readback.Dpi);
            using (var drawing = expected.CreateDrawingSession())
                TerminalSurface.DrawFrame(drawing, frame, surface.CellWidth, surface.CellHeight, true, true, fontSize,
                    transparent, surface.TerminalFontFamily, hideCursor: true, gridTop: gridTop, textPhase: readback.TextPhase);
            var expectedPixels = expected.GetPixelBytes();
            if (!actualPixels.SequenceEqual(expectedPixels))
            {
                var size = expected.SizeInPixels;
                if (actualPixels.Length == expectedPixels.Length)
                {
                    using var actual = CanvasBitmap.CreateFromBytes(device, actualPixels, (int)size.Width, (int)size.Height,
                        Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, probe.Dpi, CanvasAlphaMode.Premultiplied);
                    await actual.SaveAsync(output + $".{name}.actual.png", CanvasBitmapFileFormat.Png);
                }
                await expected.SaveAsync(output + $".{name}.expected.png", CanvasBitmapFileFormat.Png);
                var differences = actualPixels.Zip(expectedPixels).Count(pair => pair.First != pair.Second);
                throw new Exception($"Retained raster differs after {name}. Changed bytes {differences}; {actualPixels.Length} vs {expectedPixels.Length} bytes.");
            }
            cases.Add(name);
        }
    }

    private static async Task VerifyHeldPaneAsync(Window window, TerminalSurface surface, SurfaceProbe probe,
        Action<string> write, Func<TerminalFrame> read)
    {
        var device = probe.Device ?? throw new Exception("The pane has no raster device to hold.");
        using var otherTerminal = new GhosttyTerminal(24, 4);
        otherTerminal.Write(Encoding.UTF8.GetBytes("\x1b[?25lindependent pane"));
        var other = new TerminalSurface
        {
            Width = 320, Height = 120, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        other.SetTerminalFont(14, "FiraCode Nerd Font");
        var root = (Microsoft.UI.Xaml.Controls.Panel)window.Content;
        root.Children.Add(other);
        var locked = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        Thread? holder = null;
        try
        {
            var otherProbe = new SurfaceProbe(other);
            await WaitForAsync(() => other.IsLoaded && otherProbe.HasResources && other.ActualWidth > 0, "the second pane to load");
            var warm = otherTerminal.ReadFrame();
            other.SetFrame(warm);
            await otherProbe.WhenPresentedAsync(warm, "second pane warm-up");
            holder = new Thread(() =>
            {
                using (device.Lock())
                {
                    locked.Set();
                    release.Wait();
                }
            }) { IsBackground = true, Name = "Pane A device lock holder" };
            holder.Start();
            if (!locked.Wait(TimeSpan.FromSeconds(5))) throw new Exception("The device lock holder never acquired pane A's device.");
            var worker = RasterizerOf(surface);
            long Preparations() => (long)worker.GetType().GetProperty("Preparations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;
            bool Running() => (bool)worker.GetType().GetProperty("Running", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;
            var before = Preparations();
            write("\x1b[6;1Hheld pane edit");
            var held = read();
            surface.SetFrame(held);
            await WaitForAsync(() => Preparations() > before && Running(), "pane A to start preparation on its held device");
            var blocked = Preparations();
            var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!window.DispatcherQueue.TryEnqueue(() => dispatched.TrySetResult())) throw new Exception("The dispatcher rejected a callback.");
            await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
            otherTerminal.Write(Encoding.UTF8.GetBytes("\r\nprogress while A is held"));
            var progressed = otherTerminal.ReadFrame();
            other.SetFrame(progressed);
            await otherProbe.WhenPresentedAsync(progressed, "second pane while pane A's device is held");
            await VerifyOnScreenAsync(window, other, otherProbe, "pane B while pane A's device is held");
            if (ReferenceEquals(probe.Presented, held) || !Running() || Preparations() != blocked || !ReferenceEquals(worker, RasterizerOf(surface)))
                throw new Exception("Pane A was not held in the same active preparation while pane B progressed.");
            release.Set();
            if (!holder.Join(TimeSpan.FromSeconds(5))) throw new Exception("The device lock holder did not exit.");
            await probe.WhenPresentedAsync(held, "pane A after its device lock was released");
        }
        finally
        {
            release.Set();
            holder?.Join(TimeSpan.FromSeconds(5));
            root.Children.Remove(other);
        }
    }

    private static async Task VerifyClosedWhileHeldAsync(Window window, TerminalSurface surface, SurfaceProbe probe,
        MethodInfo suspend, MethodInfo resume, Action<string> write, Func<TerminalFrame> read)
    {
        var device = probe.Device ?? throw new Exception("The pane has no raster device to hold.");
        var worker = RasterizerOf(surface);
        long Preparations() => (long)worker.GetType().GetProperty("Preparations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;
        bool Running() => (bool)worker.GetType().GetProperty("Running", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;
        var locked = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using (device.Lock())
            {
                locked.Set();
                release.Wait();
            }
        }) { IsBackground = true, Name = "Pane A device lock holder during close" };
        var root = (Microsoft.UI.Xaml.Controls.Panel)window.Content;
        TerminalSurface? reopened = null;
        using var otherTerminal = new GhosttyTerminal(24, 4);
        try
        {
            holder.Start();
            if (!locked.Wait(TimeSpan.FromSeconds(5))) throw new Exception("The device lock holder never acquired pane A's device.");
            var before = Preparations();
            write("\x1b[6;1Hedit before close while held");
            surface.SetFrame(read());
            await WaitForAsync(() => Preparations() > before && Running(), "pane A to block in preparation before close");
            suspend.Invoke(surface, null);
            if (probe.HasResources) throw new Exception("Closing pane A while held kept its raster owner attached.");
            if (RasterizerReleased(worker)) throw new Exception("Pane A released resources still owned by its blocked preparation.");
            var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!window.DispatcherQueue.TryEnqueue(() => dispatched.TrySetResult())) throw new Exception("The dispatcher rejected a callback.");
            await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
            otherTerminal.Write(Encoding.UTF8.GetBytes("\x1b[?25lnew pane while A is held"));
            reopened = new TerminalSurface
            {
                Width = 320, Height = 120, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
            };
            reopened.SetTerminalFont(14, "FiraCode Nerd Font");
            reopened.SetAcrylicBackground(true);
            root.Children.Add(reopened);
            var reopenedProbe = new SurfaceProbe(reopened);
            await WaitForAsync(() => reopened.IsLoaded && reopenedProbe.HasResources && reopened.ActualWidth > 0, "a new pane to load while A is held");
            var shown = otherTerminal.ReadFrame();
            reopened.SetFrame(shown);
            await reopenedProbe.WhenPresentedAsync(shown, "a new pane while closed pane A's device is held");
            await VerifyOnScreenAsync(window, reopened, reopenedProbe, "a new pane while closed pane A's device is held");
            if (!Running() || RasterizerReleased(worker)) throw new Exception("Closed pane A left its blocked preparation before the device was released.");
            release.Set();
            if (!holder.Join(TimeSpan.FromSeconds(5))) throw new Exception("The device lock holder did not exit.");
            await WaitForAsync(() => RasterizerReleased(worker), "closed pane A to release its resources after its device was released");
            Console.WriteLine("Closing pane A while its device was held kept the UI responsive; a new pane presented on screen before release.");
        }
        finally
        {
            release.Set();
            holder.Join(TimeSpan.FromSeconds(5));
            if (reopened is not null) root.Children.Remove(reopened);
        }
        resume.Invoke(surface, null);
        await WaitForAsync(() => probe.HasResources, "pane A to reattach after closing while held");
    }

    private static async Task VerifyOnScreenAsync(Window window, TerminalSurface surface, SurfaceProbe probe, string name)
    {
        var readback = await probe.ReadRasterAsync() ?? throw new Exception("No raster to compare on screen.");
        var frame = readback.Frame ?? throw new Exception("The readback has no frame.");
        var scale = readback.Dpi / 96;
        var width = (int)MathF.Floor(Math.Min(frame.Columns, 40) * surface.CellWidth * scale);
        var top = (int)MathF.Ceiling(Math.Max(0, readback.GridTop) * scale);
        var height = (int)MathF.Floor(3 * surface.CellHeight * scale);
        var stride = (int)Math.Round(surface.ActualWidth * scale);
        if (readback.Pixels.Length < (top + height) * stride * 4) throw new Exception("On-screen fixture region exceeds the raster.");
        var expected = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(readback.Pixels, (top + row) * stride * 4, expected, row * width * 4, width * 4);
        var root = (Microsoft.UI.Xaml.Controls.Panel)window.Content;
        var background = root.Background;
        byte[] backdrop = [168, 96, 24];
        var translucent = 0;
        if (readback.Transparent)
        {
            for (var index = 0; index < expected.Length; index += 4)
            {
                var cover = 255 - expected[index + 3];
                if (cover != 0) translucent++;
                for (var channel = 0; channel < 3; channel++)
                    expected[index + channel] = (byte)Math.Min(255, expected[index + channel] + (cover * backdrop[channel] + 127) / 255);
            }
            if (translucent == 0) throw new Exception($"The transparent raster of {name} has no translucent pixels to composite.");
            root.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, backdrop[2], backdrop[1], backdrop[0]));
        }
        try
        {
            var (matched, differences, sample, actual) = await CompositorCapture.WaitForSurfacePixelsAsync(window, surface, 0, top, width, height, expected,
                TimeSpan.FromSeconds(3), readback.Transparent ? 1 : 0);
            if (!matched)
            {
                var stem = $"{_onScreenOutput}.onscreen-{string.Concat(name.Where(char.IsLetterOrDigit))}";
                await SaveBgraAsync(actual, width, height, stem + ".actual.png");
                await SaveBgraAsync(expected, width, height, stem + ".expected.png");
                throw new Exception($"Composited pixels of {name} differ from the published raster in {differences} of {width * height} pixels; {sample}. Images: {stem}.*.png");
            }
        }
        finally { root.Background = background; }
        Console.WriteLine($"On-screen composition of {name} matched the published raster over {width}x{height} pixels" +
            (readback.Transparent ? $", {translucent} of them blended over a non-black backdrop." : "."));
    }

    private static async Task SaveBgraAsync(byte[] pixels, int width, int height, string path)
    {
        if (pixels.Length != width * height * 4) return;
        var opaque = (byte[])pixels.Clone();
        for (var index = 3; index < opaque.Length; index += 4) opaque[index] = 255;
        using var bitmap = CanvasBitmap.CreateFromBytes(CanvasDevice.GetSharedDevice(), opaque, width, height,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        await bitmap.SaveAsync(path, CanvasBitmapFileFormat.Png);
    }

    private static object RasterizerOf(TerminalSurface surface)
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        var owner = typeof(TerminalSurface).GetField("_raster", Private)!.GetValue(surface) ?? throw new Exception("No raster owner is attached.");
        return owner.GetType().GetProperty("Rasterizer")!.GetValue(owner)!;
    }

    private static bool RasterizerReleased(object rasterizer) =>
        (bool)rasterizer.GetType().GetProperty("Released", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rasterizer)!;

    private static async Task WaitForAsync(Func<bool> condition, string description)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException($"Timed out waiting for {description}.");
            await Task.Delay(10);
        }
    }
}
