using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.Graphics.Canvas;
using System.Text;

/// <summary>Pixel checks for Kitty image composition: layers, alpha, crop, scale, offsets, clipping and caching.</summary>
internal static class KittyImageRenderChecks
{
    private static readonly TerminalColor Black = new(0, 0, 0);
    private static readonly TerminalColor White = new(255, 255, 255);
    private static readonly TerminalColor Blue = new(0, 0, 200);
    private static ulong _generation = 1_000_000;

    internal static void Run(CanvasDevice device)
    {
        CheckLayers(device);
        CheckDefaultColoredBackground(device);
        CheckCropScaleOffsetAndClip(device);
        CheckTerminalFrame(device);
        CheckBitmapCache(device);
        CheckManyCurrentCrops(device);
        CheckOverscanLayers(device);
        Console.WriteLine("Kitty image render checks passed layers, alpha, crop, scale, offsets, clipping and bitmap cache.");
    }

    private static void CheckDefaultColoredBackground(CanvasDevice device)
    {
        using var terminal = new GhosttyTerminal(4, 1);
        var bg = terminal.ReadFrame().Background;
        terminal.Write(Encoding.ASCII.GetBytes($"\x1b[?25l\x1b[0m \x1b[48;2;{bg.R};{bg.G};{bg.B}m "));
        var native = terminal.ReadFrame();
        var frame = native with { Images = new TerminalImages([Place(Solid(1, 1, 255, 0, 0), TerminalImageLayer.BelowBackground, 0, 0, 20, 20)], 10, 20) };
        using var target = Draw(device, frame, 40, 20);
        Expect(target, 5, 10, 255, 0, 0, "A default background exposes a below-background image.");
        Expect(target, 15, 10, bg.R, bg.G, bg.B, "An explicit default-colored background covers a below-background image.");
        using var transparent = new CanvasRenderTarget(device, 40, 20, 96);
        using (var drawing = transparent.CreateDrawingSession())
            TerminalSurface.DrawFrame(drawing, native, 10, 20, true, true, transparentBackground: true);
        if (transparent.GetPixelColors(5, 10, 1, 1)[0].A != 0 || transparent.GetPixelColors(15, 10, 1, 1)[0].A != 255)
            throw new Exception("Acrylic must retain explicit default-colored backgrounds as opaque.");
    }

    private static TerminalImageData Solid(int width, int height, byte r, byte g, byte b, byte a = 255) =>
        new(1, ++_generation, width, height, Enumerable.Range(0, width * height)
            .SelectMany(_ => new[] { (byte)(r * a / 255), (byte)(g * a / 255), (byte)(b * a / 255), a }).ToArray());

    private static TerminalImagePlacement Place(TerminalImageData image, TerminalImageLayer layer, int column, int row,
        int pixelWidth, int pixelHeight, int sourceX = 0, int sourceWidth = -1, int offsetX = 0, int offsetY = 0) =>
        new(image, layer, layer switch { TerminalImageLayer.BelowBackground => int.MinValue / 2 - 1, TerminalImageLayer.BelowText => -1, _ => 0 },
            column, row, 1, 1, offsetX, offsetY, pixelWidth, pixelHeight, sourceX, 0, sourceWidth < 0 ? image.Width - sourceX : sourceWidth, image.Height);

    private static TerminalFrame Frame(int columns, int rows, Func<int, TerminalCell?> cell, params TerminalImagePlacement[] placements)
    {
        var blank = new TerminalCell(" ", 1, White, Black, false, false, false, false, false, false);
        var cells = Enumerable.Range(0, columns * rows).Select(i => cell(i) ?? blank).ToArray();
        return new TerminalFrame(columns, rows, cells, new TerminalCursor(0, 0, false, false, TerminalCursorShape.Block, White), White, Black)
        {
            Images = new TerminalImages(placements.OrderBy(p => p.Layer).ThenBy(p => p.Z).ToArray(), 10, 20),
        };
    }

    private static void CheckLayers(CanvasDevice device)
    {
        var text = new TerminalCell("M", 1, White, Black, false, false, false, false, false, false);
        var frame = Frame(6, 1, i => i switch
            {
                1 or 2 => text with { Text = " ", Background = Blue },
                3 or 4 => text,
                _ => null,
            },
            Place(Solid(1, 1, 255, 0, 0), TerminalImageLayer.BelowBackground, 0, 0, 20, 20),
            Place(Solid(1, 1, 255, 255, 0), TerminalImageLayer.BelowText, 2, 0, 10, 20),
            Place(Solid(1, 1, 0, 255, 0), TerminalImageLayer.AboveText, 3, 0, 10, 20),
            Place(Solid(1, 1, 0, 255, 255), TerminalImageLayer.BelowText, 4, 0, 10, 20),
            Place(Solid(1, 1, 255, 0, 0, 128), TerminalImageLayer.AboveText, 5, 0, 10, 20));
        using var target = Draw(device, frame, 60, 20);
        Expect(target, 5, 10, 255, 0, 0, "A below-background image shows through the default background.");
        Expect(target, 15, 10, 0, 0, 200, "A non-default cell background hides a below-background image.");
        Expect(target, 25, 10, 255, 255, 0, "A below-text image draws over cell backgrounds.");
        var above = target.GetPixelColors(30, 0, 10, 20);
        if (above.Any(c => c.R != 0 || c.G != 255 || c.B != 0)) throw new Exception("An above-text image must cover its cell's text.");
        var below = target.GetPixelColors(40, 0, 10, 20);
        if (!below.Any(c => c is { R: > 200, G: > 200, B: > 200 })) throw new Exception("Text must draw over a below-text image.");
        if (!below.Any(c => c is { R: 0, G: 255, B: 255 })) throw new Exception("A below-text image must show around its cell's glyph.");
        Expect(target, 55, 10, 128, 0, 0, "Premultiplied alpha must blend over the background.");
    }

    private static void CheckCropScaleOffsetAndClip(CanvasDevice device)
    {
        var halves = new TerminalImageData(2, ++_generation, 2, 1, [255, 0, 0, 255, 0, 255, 0, 255]);
        var frame = Frame(6, 2, _ => null,
            Place(halves, TerminalImageLayer.AboveText, 0, 0, 20, 20, sourceX: 1, sourceWidth: 1),
            Place(Solid(1, 1, 0, 0, 255), TerminalImageLayer.AboveText, 2, -1, 10, 40),
            Place(Solid(1, 1, 255, 0, 255), TerminalImageLayer.AboveText, 5, 0, 30, 20),
            Place(Solid(1, 1, 255, 255, 255), TerminalImageLayer.AboveText, 3, 1, 5, 6, offsetX: 3, offsetY: 4));
        const float top = 20;
        using var target = Draw(device, frame, 100, 80, top);
        Expect(target, 2, 30, 0, 255, 0, "A source rectangle must crop the image before scaling.");
        Expect(target, 18, 30, 0, 255, 0, "A cropped image must scale to its pixel size.");
        Expect(target, 25, 10, 0, 0, 0, "An image above the grid must be clipped at the top row.");
        Expect(target, 25, 30, 0, 0, 255, "The visible part of a partly scrolled image must draw.");
        Expect(target, 55, 30, 255, 0, 255, "An image must draw inside the grid.");
        Expect(target, 65, 30, 0, 0, 0, "An image must be clipped at the right edge of the grid.");
        Expect(target, 31, 41, 0, 0, 0, "A cell offset must move the image right and down.");
        Expect(target, 35, 46, 255, 255, 255, "A cell offset must keep the image size.");
        Expect(target, 39, 51, 0, 0, 0, "An offset image must end at its pixel size.");

        var scaled = frame with { Images = frame.Images with { CellPixelWidth = 20, CellPixelHeight = 40 } };
        using var hiDpi = Draw(device, scaled, 100, 80, top);
        Expect(hiDpi, 8, 24, 0, 255, 0, "Native pixels must map to drawn cells by the cell pixel size.");
        Expect(hiDpi, 12, 24, 0, 0, 0, "A placement of 20 native pixels must cover one 20-pixel cell.");
        Expect(hiDpi, 8, 34, 0, 0, 0, "A placement of 20 native pixels must cover half of a 40-pixel cell height.");
    }

    private static void CheckTerminalFrame(CanvasDevice device)
    {
        using var terminal = new GhosttyTerminal(8, 3);
        terminal.SetImageStorageLimit(TerminalSession.DefaultImageStorageLimit);
        terminal.SetCellPixelSize(10, 20);
        var pixels = Convert.ToBase64String(Enumerable.Range(0, 20 * 20).SelectMany(_ => new byte[] { 250, 100, 0, 255 }).ToArray());
        terminal.Write(Encoding.ASCII.GetBytes($"\x1b[48;2;0;0;200m  \x1b[0m\r\x1b[2C\x1b_Ga=T,f=32,s=20,v=20,i=1,q=2;{pixels}\x1b\\"));
        var frame = terminal.ReadFrame();
        using var target = Draw(device, frame, 80, 60);
        Expect(target, 25, 10, 250, 100, 0, "A transmitted image must draw at its cursor cell.");
        Expect(target, 5, 10, 0, 0, 200, "Cells beside the image keep their background.");
        terminal.Write("\x1b_Ga=d,d=A,q=2\x1b\\"u8);
        using var deleted = Draw(device, terminal.ReadFrame(), 80, 60);
        Expect(deleted, 25, 10, frame.Background.R, frame.Background.G, frame.Background.B, "A deleted image must stop drawing.");
    }

    private static void CheckBitmapCache(CanvasDevice device)
    {
        using var cache = new TerminalSurface.ImageBitmaps();
        var image = Place(Solid(4, 4, 1, 2, 3), TerminalImageLayer.AboveText, 0, 0, 10, 20);
        var first = cache.Get(device, image);
        if (!ReferenceEquals(first, cache.Get(device, image with { Column = 3 })) || cache.Count != 1 || cache.Bytes != 64)
            throw new Exception("An image generation must upload once for every placement that shows all of it.");
        var big = Enumerable.Range(0, 5).Select(_ => Place(Solid(2048, 2048, 9, 9, 9), TerminalImageLayer.AboveText, 0, 0, 10, 20)).ToArray();
        foreach (var data in big) cache.Get(device, data);
        var current = new TerminalImages([big[0]], 10, 20);
        cache.Trim(current);
        if (cache.Bytes > TerminalSurface.ImageBitmaps.Budget || !ReferenceEquals(cache.Get(device, big[0]), cache.Get(device, big[0])))
            throw new Exception($"Trimming must fit the budget and keep drawn bitmaps, held {cache.Bytes} bytes.");
        using (var other = new CanvasDevice())
        {
            cache.Get(other, image);
            if (cache.Count != 1) throw new Exception("A new device must drop bitmaps of the old device.");
        }
        cache.Clear();
        var wide = Place(new TerminalImageData(3, ++_generation, device.MaximumBitmapSizeInPixels + 1, 1,
            new byte[(device.MaximumBitmapSizeInPixels + 1) * 4]), TerminalImageLayer.AboveText, 0, 0, 10, 20);
        var fitted = cache.Get(device, wide)!;
        if (fitted.Bitmap.SizeInPixels.Width > device.MaximumBitmapSizeInPixels || fitted.Scale >= 1)
            throw new Exception("Images beyond the device limit must be resampled to fit.");
        if (cache.Count != 1 || cache.Bytes == 0) throw new Exception("The cache must track resampled bitmaps.");
        var halves = new TerminalImageData(4, ++_generation, 2, 1, [255, 0, 0, 255, 0, 255, 0, 255]);
        var whole = cache.Get(device, Place(halves, TerminalImageLayer.AboveText, 0, 0, 10, 20))!;
        var crop = cache.Get(device, Place(halves, TerminalImageLayer.AboveText, 0, 0, 10, 20, sourceX: 1, sourceWidth: 1))!;
        if (ReferenceEquals(whole, crop) || crop.Bitmap.SizeInPixels.Width != 1 || crop.Bitmap.GetPixelBytes()[1] != 255)
            throw new Exception("A cropped placement must get a bitmap of just its source rectangle.");
        var outside = Place(halves, TerminalImageLayer.AboveText, 0, 0, 10, 20, sourceX: 1, sourceWidth: 2);
        if (cache.Get(device, outside) is not null) throw new Exception("A source rectangle outside the image must not draw.");
        cache.Clear();
        if (cache.Count != 0 || cache.Bytes != 0) throw new Exception("Clearing must release every bitmap.");
    }

    private static void CheckManyCurrentCrops(CanvasDevice device)
    {
        using var cache = new TerminalSurface.ImageBitmaps();
        var image = Solid(2048, 2048, 255, 0, 0);
        var placements = Enumerable.Range(0, 100).Select(i => Place(image,
            TerminalImageLayer.AboveText, i, 0, 1, 20, sourceX: i, sourceWidth: 2048 - i)).ToArray();
        using var target = new CanvasRenderTarget(device, 100, 20, 96);
        using (var drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));
            foreach (var placement in placements)
            {
                var entry = cache.Get(device, placement, drawing)!;
                drawing.DrawImage(entry.Bitmap, new Windows.Foundation.Rect(placement.Column, 0, 1, 20),
                    new Windows.Foundation.Rect(0, 0, entry.Bitmap.SizeInPixels.Width, entry.Bitmap.SizeInPixels.Height));
                if (cache.Bytes > TerminalSurface.ImageBitmaps.Budget)
                    throw new Exception("Current-frame crops must fit the bitmap budget at every admission.");
            }
        }
        cache.Trim(new TerminalImages(placements, 10, 20));
        for (var x = 0; x < 100; x++) Expect(target, x, 10, 255, 0, 0,
            "Every placement must survive eviction and pending drawing-resource release.");
    }

    private static void CheckOverscanLayers(CanvasDevice device)
    {
        foreach (var layer in Enum.GetValues<TerminalImageLayer>())
        {
            var text = new TerminalCell("M", 1, White, Black, false, false, false, false, false, false);
            var frame = Frame(3, 2, _ => null,
                Place(Solid(1, 1, 255, 0, 0), layer, 0, 2, 10, 20),
                Place(Solid(1, 1, 255, 0, 0), layer, 1, 1, 10, 40)) with
            {
                OverscanBelow = [new TerminalOverscanRow(2, [text with { Text = "", Background = Blue }, text, text])],
            };
            using var target = Draw(device, frame, 30, 40, -10);
            using var reference = Draw(device, frame with { Images = TerminalImages.Empty }, 30, 40, -10);
            Expect(target, 0, 39, layer == TerminalImageLayer.BelowBackground ? (byte)0 : (byte)255,
                0, layer == TerminalImageLayer.BelowBackground ? (byte)200 : (byte)0,
                "A wholly below-grid image must respect overscan backgrounds in its layer.");
            Expect(target, 19, 30, 255, 0, 0, "A crossing image must extend into the fractional overscan viewport.");
            var glyph = reference.GetPixelColors(10, 30, 10, 10);
            var painted = target.GetPixelColors(10, 30, 10, 10);
            var white = Enumerable.Range(0, glyph.Length).Where(i => glyph[i].R > 200 && glyph[i].G > 200 && glyph[i].B > 200).ToArray();
            if (white.Length == 0) throw new Exception("Overscan fixture must contain visible text.");
            if (white.Any(i => layer == TerminalImageLayer.AboveText ? painted[i].G != 0 : painted[i].G < 200))
                throw new Exception("Overscan text and images must use the ordinary layer order.");
        }
    }

    private static CanvasRenderTarget Draw(CanvasDevice device, TerminalFrame frame, int width, int height, float top = 0)
    {
        var target = new CanvasRenderTarget(device, width, height, 96);
        using var drawing = target.CreateDrawingSession();
        drawing.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));
        TerminalSurface.DrawFrame(drawing, frame, 10, 20, focused: false, cursorPhase: false, gridTop: top);
        return target;
    }

    private static void Expect(CanvasRenderTarget target, int x, int y, byte r, byte g, byte b, string message)
    {
        var color = target.GetPixelColors(x, y, 1, 1)[0];
        if (Math.Abs(color.R - r) > 2 || Math.Abs(color.G - g) > 2 || Math.Abs(color.B - b) > 2)
            throw new Exception($"{message} Pixel ({x},{y}) was {color.R},{color.G},{color.B}; expected {r},{g},{b}.");
    }
}
