using System.IO.Compression;
using System.Text;
using AgentTerm.Terminal;

/// <summary>Kitty graphics through the native parser, render state and owned frame records.</summary>
internal static class KittyImageChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static byte[] Apc(string control, ReadOnlySpan<byte> payload = default) =>
        Encoding.ASCII.GetBytes($"\x1b_G{control}{(payload.IsEmpty ? "" : ";" + Convert.ToBase64String(payload))}\x1b\\");

    internal static byte[] Rgba(params (byte R, byte G, byte B, byte A)[] pixels) =>
        pixels.SelectMany(p => new[] { p.R, p.G, p.B, p.A }).ToArray();

    public static void Run()
    {
        CheckRequiresCellSize();
        CheckPlacementAndOwnership();
        CheckFormatsAndCompression();
        CheckCropScaleOffsetAndLayers();
        CheckScrollingAndScreens();
        CheckPlacementHandoff();
        CheckVirtualPlacement();
        CheckRepliesAndDisabledMedia();
        CheckStorageLimit();
        CheckAnimation();
        CheckHoldCaptureAndLiveEdge();
        Console.WriteLine("Kitty image checks passed placement, ownership, formats, layers, scrolling, screens, replies and animation.");
    }

    private static void CheckHoldCaptureAndLiveEdge()
    {
        using var terminal = Terminal(8, 6);
        var red = Rgba((255, 0, 0, 255));
        var blue = Rgba((0, 0, 255, 255));
        terminal.Write(Apc("a=T,f=32,s=1,v=1,i=70,q=2,C=1", red));
        var capture = terminal.BeginFrame();
        terminal.Write("\x1b[?2026h"u8);
        terminal.Write(Apc("a=T,f=32,s=1,v=1,i=70,q=2,C=1", blue));
        var original = terminal.CompleteFrame(capture);
        Check(original.Images.Placements[0].Image.Pixels.Span.SequenceEqual(red), "An in-flight capture owns the old image pixels.");
        var held = terminal.ReadFrame();
        Check(held.Images.Placements[0].Image.Pixels.Span.SequenceEqual(red), "A handoff holds image pixels from the exact boundary.");
        terminal.Write("\x1b[?2026l"u8);
        Check(terminal.ReadFrame().Images.Placements[0].Image.Pixels.Span.SequenceEqual(blue), "Release publishes the new image pixels.");
        terminal.Write("\x1b[5;1H"u8);
        terminal.Write(Apc("a=T,f=32,s=1,v=1,i=71,q=2,C=1", red));
        terminal.Write("\x1b[H"u8);
        terminal.SetBottomAnchored(true);
        var anchored = terminal.ReadFrame();
        Check(anchored.Viewport.TotalRows == 5 && anchored.Images.Placements.Any(p => p.Image.Id == 71),
            "Images below the last text row remain visible in bottom-anchored mode.");
        var anchoredCapture = terminal.BeginFrame();
        terminal.Write("\x1b[6;1H"u8);
        terminal.Write(Apc("a=T,f=32,s=1,v=1,i=72,q=2,C=1", red));
        terminal.Write("\x1b[H\x1b[?2026h"u8);
        terminal.CompleteFrame(anchoredCapture);
        var imageBoundary = terminal.ReadFrame();
        Check(imageBoundary.Viewport.TotalRows == 6 && imageBoundary.Images.Placements.Any(p => p.Image.Id == 72),
            "In-flight presentation scanning includes the exact image extent without borrowing image iterators.");
        terminal.Write("\x1b[?2026l"u8);
    }

    private static GhosttyTerminal Terminal(ushort columns = 8, ushort rows = 4)
    {
        var terminal = new GhosttyTerminal(columns, rows);
        terminal.SetImageStorageLimit(TerminalSession.DefaultImageStorageLimit);
        terminal.SetCellPixelSize(10, 20);
        return terminal;
    }

    private static void CheckRequiresCellSize()
    {
        using var terminal = new GhosttyTerminal(8, 4);
        terminal.SetImageStorageLimit(TerminalSession.DefaultImageStorageLimit);
        terminal.Write(Apc("a=T,f=32,s=1,v=1,i=1,q=2", Rgba((1, 2, 3, 255))));
        Check(terminal.ReadFrame().Images.IsEmpty, "The terminal positions no image until it knows the cell size.");
        terminal.SetCellPixelSize(10, 20);
        var frame = terminal.ReadFrame();
        Check(frame.Images.Placements.Count == 1, "Setting the cell size must reveal stored placements.");
        Check(frame.Images.CellPixelWidth == 10 && frame.Images.CellPixelHeight == 20, "Images must report the cell size used to place them.");
    }

    private static void CheckPlacementAndOwnership()
    {
        using var terminal = Terminal();
        terminal.Write("ab"u8);
        terminal.Write(Apc("a=T,f=32,s=2,v=2,i=1,q=2", Rgba((255, 0, 0, 255), (0, 255, 0, 128), (0, 0, 255, 0), (10, 20, 30, 255))));
        var frame = terminal.ReadFrame();
        Check(frame.Images.Placements.Count == 1, $"One transmitted image must produce one placement, got {frame.Images.Placements.Count}.");
        var placement = frame.Images.Placements[0];
        Check(placement.Column == 2 && placement.Row == 0 && placement.Columns == 1 && placement.Rows == 1,
            $"A direct placement must sit at the cursor cell, got {placement.Column},{placement.Row} {placement.Columns}x{placement.Rows}.");
        Check(placement.PixelWidth == 2 && placement.PixelHeight == 2 && placement.SourceWidth == 2 && placement.SourceHeight == 2,
            "An unscaled placement must draw at its image size.");
        Check(placement.Layer == TerminalImageLayer.AboveText && placement.Z == 0 && placement.Kind == TerminalImagePlacementKind.Fixed,
            "Default z-index places a fixed image above text.");
        var image = placement.Image;
        Check(image.Id == 1 && image.Width == 2 && image.Height == 2 && image.Generation != 0, "Image identity and size must be preserved.");
        Check(image.Pixels.Span.SequenceEqual(Rgba((255, 0, 0, 255), (0, 128, 0, 128), (0, 0, 0, 0), (10, 20, 30, 255))),
            "Straight RGBA must become premultiplied RGBA.");
        Check(terminal.ImageCacheBytes == 16, "The terminal must hold exactly the placed image's decoded pixels.");
        Check(ReferenceEquals(frame, terminal.ReadFrame()), "An unchanged terminal must keep the frame with images.");

        terminal.Write(Apc("a=T,f=32,s=1,v=1,i=1,q=2", Rgba((9, 9, 9, 255))));
        var replaced = terminal.ReadFrame();
        Check(replaced.Images.Placements.Count == 1 && replaced.Images.Placements[0].Image.Generation != image.Generation &&
            replaced.Images.Placements[0].Image.Pixels.Span.SequenceEqual(Rgba((9, 9, 9, 255))), "Replacing an image must publish its new pixels.");
        Check(image.Width == 2 && image.Pixels.Span[0] == 255 && frame.Images.Placements[0].Image == image,
            "Older frames must keep their own image pixels.");
        Check(terminal.ImageCacheBytes == 4, "Replaced pixels must leave the cache.");

        var cells = replaced.Cells;
        terminal.Write(Apc("a=d,d=I,i=1,q=2"));
        var deleted = terminal.ReadFrame();
        Check(deleted.Images.IsEmpty && ReferenceEquals(deleted.Cells, cells), "Deleting an image is an image-only update that keeps the cell grid.");
        Check(terminal.ImageCacheBytes == 0, "Deleted images must free their cached pixels.");
    }

    private static void CheckFormatsAndCompression()
    {
        using var terminal = Terminal();
        terminal.Write(Apc("a=T,f=24,s=2,v=1,i=2,q=2", [1, 2, 3, 4, 5, 6]));
        var rgb = terminal.ReadFrame().Images.Placements.Single().Image;
        Check(rgb.Pixels.Span.SequenceEqual(Rgba((1, 2, 3, 255), (4, 5, 6, 255))), "RGB must become opaque RGBA.");

        terminal.Write("\x1b[2J\x1b[H"u8);
        terminal.Write(Apc("a=d,d=A,q=2"));
        var raw = Rgba((200, 100, 50, 255), (0, 0, 0, 255), (1, 1, 1, 255), (255, 255, 255, 255));
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);
        terminal.Write(Apc("a=T,f=32,o=z,s=2,v=2,i=3,q=2", compressed.ToArray()));
        var inflated = terminal.ReadFrame().Images.Placements.Single().Image;
        Check(inflated.Id == 3 && inflated.Pixels.Span.SequenceEqual(raw), "Zlib payloads must arrive inflated.");

        terminal.Write(Apc("a=d,d=A,q=2"));
        terminal.Write(Apc("a=T,f=100,i=4,q=2", Png(3, 1, [(255, 0, 0, 255), (0, 255, 0, 255), (0, 0, 255, 128)])));
        var png = terminal.ReadFrame().Images.Placements.Single().Image;
        Check(png.Width == 3 && png.Height == 1 && png.Pixels.Span.SequenceEqual(Rgba((255, 0, 0, 255), (0, 255, 0, 255), (0, 0, 128, 128))),
            "PNG payloads must decode to premultiplied RGBA.");

        foreach (var (name, width, height, fixture, rgba) in PngFixtures.All.Where(f => f.Name.EndsWith("interlace 1", StringComparison.Ordinal)))
        {
            terminal.Write(Apc("a=d,d=A,q=2"));
            terminal.Write(Apc("a=T,f=100,i=13,q=2", Convert.FromBase64String(fixture)));
            var decoded = terminal.ReadFrame().Images.Placements.Single().Image;
            var straight = Convert.FromBase64String(rgba);
            var premultiplied = GhosttyRgba(straight);
            Check(decoded.Width == width && decoded.Height == height && decoded.Pixels.Span.SequenceEqual(premultiplied),
                $"PNG {name} must reach the frame through the terminal.");
        }
    }

    private static byte[] GhosttyRgba(byte[] straight)
    {
        var output = (byte[])straight.Clone();
        for (var i = 0; i < output.Length; i += 4)
            for (var c = 0; c < 3; c++) output[i + c] = (byte)((straight[i + c] * straight[i + 3] + 127) / 255);
        return output;
    }

    private static void CheckCropScaleOffsetAndLayers()
    {
        using var terminal = Terminal(10, 4);
        var pixels = Rgba((1, 0, 0, 255), (2, 0, 0, 255), (3, 0, 0, 255), (4, 0, 0, 255));
        terminal.Write(Apc("a=t,f=32,s=2,v=2,i=5,q=2", pixels));
        terminal.Write(Apc("a=p,i=5,p=1,x=1,y=0,w=1,h=2,c=3,r=2,X=3,Y=4,z=5,C=1,q=2"));
        terminal.Write("\x1b[1;5H"u8);
        terminal.Write(Apc("a=p,i=5,p=2,z=-1,C=1,q=2"));
        terminal.Write("\x1b[3;1H"u8);
        terminal.Write(Apc("a=p,i=5,p=3,z=-1073741825,C=1,q=2"));
        var images = terminal.ReadFrame().Images.Placements;
        Check(images.Count == 3, $"Three placements of one image must draw, got {images.Count}.");
        Check(images.Select(p => p.Layer).SequenceEqual([TerminalImageLayer.BelowBackground, TerminalImageLayer.BelowText, TerminalImageLayer.AboveText]),
            "Placements must be ordered by layer, then z-index.");
        Check(images.All(p => ReferenceEquals(p.Image, images[0].Image)), "Placements of one image must share its pixels.");
        var cropped = images[2];
        Check(cropped.PlacementId == 1 && cropped.SourceX == 1 && cropped.SourceY == 0 && cropped.SourceWidth == 1 && cropped.SourceHeight == 2,
            "The source rectangle must crop the image.");
        Check(cropped.Columns == 3 && cropped.Rows == 2 && cropped.PixelWidth == 30 - 3 && cropped.PixelHeight == 40 - 4,
            $"Explicit cells must scale the image within its cells after the offset, got {cropped.PixelWidth}x{cropped.PixelHeight}.");
        Check(cropped.CellOffsetX == 3 && cropped.CellOffsetY == 4 && cropped.Z == 5, "Cell offsets and z-index must be preserved.");
        Check(images[1].Column == 4 && images[1].Row == 0 && images[0].Column == 0 && images[0].Row == 2, "Each placement must keep its cell.");
    }

    private static void CheckScrollingAndScreens()
    {
        using var terminal = Terminal(8, 4);
        terminal.Write(Apc("a=T,f=32,s=1,v=1,i=6,r=2,c=1,q=2", Rgba((7, 7, 7, 255))));
        var placed = terminal.ReadFrame().Images.Placements.Single();
        Check(placed.Row == 0 && placed.Rows == 2, "The image must start at the top row.");
        terminal.Write("\r\n\r\n\r\n"u8);
        var partial = terminal.ReadFrame().Images.Placements.Single();
        Check(partial.Row == -1 && partial.Rows == 2, $"Scrolling output must move the image up and keep its full size, got row {partial.Row}.");
        terminal.Write("\r\n"u8);
        Check(terminal.ReadFrame().Images.IsEmpty, "An image scrolled out of view must not draw.");
        terminal.ScrollLines(-2);
        var history = terminal.ReadFrame().Images.Placements.Single();
        Check(history.Row == 0, $"Scrolling history back must show the image again, got row {history.Row}.");
        terminal.ScrollToBottom();

        terminal.Write("\x1b[?1049h"u8);
        Check(terminal.ReadFrame().Images.IsEmpty, "The alternate screen has its own image storage.");
        terminal.Write(Apc("a=T,f=32,s=1,v=1,i=6,q=2", Rgba((8, 8, 8, 255))));
        var alternate = terminal.ReadFrame().Images.Placements.Single();
        Check(alternate.Image.Pixels.Span[0] == 8, "The alternate screen must draw its own image with the same id.");
        terminal.Write("\x1b[?1049l"u8);
        terminal.ScrollLines(-2);
        var restored = terminal.ReadFrame().Images.Placements.Single();
        Check(restored.Image.Pixels.Span[0] == 7, "Leaving the alternate screen must restore primary images.");
        terminal.Write("\u001bc"u8);
        var reset = terminal.ReadFrame();
        Check(reset.Images.IsEmpty && terminal.ImageCacheBytes == 0, $"A full reset must delete every image, got {reset.Images.Placements.Count} placements and {terminal.ImageCacheBytes} bytes.");
    }

    private static void CheckPlacementHandoff()
    {
        using var terminal = Terminal(8, 4);
        terminal.Write(Apc("a=T,f=32,s=1,v=1,i=6,r=2,c=1,q=2", Rgba((7, 7, 7, 255))));
        var first = terminal.ReadFrame().Images.Placements.Single();
        var capture = terminal.BeginFrame();
        terminal.Write("\r\n\r\n\r\n\x1b[?2026h"u8);
        terminal.CompleteFrame(capture);
        var boundary = terminal.ReadFrame().Images.Placements.Single();
        Check(boundary.Row == -1 && ReferenceEquals(first.Image, boundary.Image),
            $"A replacement render state must rebuild placement rows even when its local generation repeats, got {boundary.Row}.");
        terminal.Write("\x1b[?2026l"u8);
    }

    private static void CheckVirtualPlacement()
    {
        using var terminal = Terminal(8, 4);
        terminal.Write(Apc("a=T,U=1,f=32,s=2,v=1,i=7,c=2,r=1,q=2", Rgba((1, 1, 1, 255), (2, 2, 2, 255))));
        Check(terminal.ReadFrame().Images.IsEmpty, "A virtual placement draws nothing until placeholder cells exist.");
        terminal.Write(Encoding.UTF8.GetBytes("\x1b[2;3H\x1b[38;5;7m\U0010EEEE\u0305\u0305\U0010EEEE\u0305\u030D\x1b[0m"));
        var virtualFrame = terminal.ReadFrame();
        var placeholders = virtualFrame.Cells.Where(c => c.Text.StartsWith("\U0010EEEE", StringComparison.Ordinal)).ToArray();
        Check(placeholders.Length == 2 && placeholders.All(c => c.Hidden && c.Width == 1),
            "Placeholder cells retain their grapheme and extent but suppress glyphs, including cursor redraws.");
        var placement = virtualFrame.Images.Placements.Single();
        Check(placement.Kind == TerminalImagePlacementKind.Virtual && placement.Column == 2 && placement.Row == 1 && placement.Columns == 2,
            $"Placeholder cells must position a virtual placement, got {placement.Kind} {placement.Column},{placement.Row} {placement.Columns}.");
        Check(placement.Layer == TerminalImageLayer.BelowText, "Virtual placements draw below text.");
        terminal.Write("\x1b[2;3H"u8);
        var cursorFrame = terminal.ReadFrame();
        Check(cursorFrame.Cursor is { Column: 2, Row: 1 } && cursorFrame.Cells[10].Hidden,
            "A cursor on a placeholder must use the same suppressed canonical cell.");
        terminal.SetOverscanEnabled(true);
        terminal.Write("\x1b[4;1H\r\n\r\n\r\n"u8);
        terminal.ScrollTo(2);
        var overscan = terminal.ReadFrame().OverscanAbove.SelectMany(r => r.Cells)
            .Where(c => c.Text.StartsWith("\U0010EEEE", StringComparison.Ordinal)).ToArray();
        Check(overscan.Length == 2 && overscan.All(c => c.Hidden && c.Width == 1),
            "Overscan placeholders preserve graphemes and extent while suppressing glyphs.");
        terminal.ScrollToBottom();


        terminal.Write(Apc("a=t,f=32,s=1,v=1,i=8,q=2", Rgba((3, 3, 3, 255))));
        terminal.Write("\x1b[1;1H"u8);
        terminal.Write(Apc("a=p,i=8,p=1,C=1,q=2"));
        terminal.Write(Apc("a=p,i=8,p=2,P=8,Q=1,H=3,V=2,C=1,q=2"));
        var relative = terminal.ReadFrame().Images.Placements.Single(p => p.Kind == TerminalImagePlacementKind.Relative);
        Check(relative.PlacementId == 2 && relative.Column == 3 && relative.Row == 2,
            $"A relative placement must be offset from its parent, got {relative.Column},{relative.Row}.");
    }

    private static void CheckRepliesAndDisabledMedia()
    {
        using var terminal = Terminal();
        terminal.Write(Apc("a=q,f=32,s=1,v=1,i=31", Rgba((0, 0, 0, 255))));
        var query = string.Concat(terminal.TakeResponses().Select(Encoding.ASCII.GetString));
        Check(query.Contains("i=31;OK", StringComparison.Ordinal), $"An inline query must report support, got <{query}>.");
        foreach (var medium in new[] { "f", "t", "s" })
        {
            terminal.Write(Apc($"a=t,t={medium},f=32,s=1,v=1,i=32", Encoding.ASCII.GetBytes(@"C:\Windows\win.ini")));
            var reply = string.Concat(terminal.TakeResponses().Select(Encoding.ASCII.GetString));
            Check(reply.Contains("i=32;", StringComparison.Ordinal) && !reply.Contains(";OK", StringComparison.Ordinal),
                $"Disabled medium {medium} must reply with an error, got <{reply}>.");
        }
        Check(terminal.ReadFrame().Images.IsEmpty, "Rejected media must store nothing.");
    }

    private static void CheckStorageLimit()
    {
        using var terminal = Terminal();
        Check(terminal.ImageStorageLimit == TerminalSession.DefaultImageStorageLimit, $"The enabled host image storage limit must be 64 MiB, got {terminal.ImageStorageLimit}.");
        terminal.Write(Apc("a=T,f=32,s=1,v=1,i=1,q=2", Rgba((1, 2, 3, 255))));
        Check(!terminal.ReadFrame().Images.IsEmpty, "Images must draw under the default limit.");
        terminal.SetImageStorageLimit(100);
        var sixteen = Rgba(Enumerable.Repeat(((byte)5, (byte)5, (byte)5, (byte)255), 16).ToArray());
        terminal.Write(Apc("a=T,f=32,s=4,v=4,i=11,q=2", sixteen));
        terminal.Write(Apc("a=T,f=32,s=4,v=4,i=12,q=2", sixteen));
        var evicted = terminal.ReadFrame().Images.Placements;
        Check(evicted.Count == 1 && evicted[0].Image.Id == 12 && terminal.ImageCacheBytes == 64,
            $"A full storage must evict the oldest image, got {string.Join(",", evicted.Select(p => p.Image.Id))}.");
        terminal.SetImageStorageLimit(0);
        Check(terminal.ReadFrame().Images.IsEmpty && terminal.ImageCacheBytes == 0, "A zero limit must delete every image.");
        terminal.Write(Apc("a=T,f=32,s=1,v=1,i=2", Rgba((1, 2, 3, 255))));
        var reply = string.Concat(terminal.TakeResponses().Select(Encoding.ASCII.GetString));
        Check(!reply.Contains(";OK", StringComparison.Ordinal) && terminal.ReadFrame().Images.IsEmpty,
            $"A disabled protocol must not report success, got <{reply}>.");
    }

    private static void CheckAnimation()
    {
        using var terminal = Terminal();
        terminal.Write(Apc("a=T,f=32,s=1,v=1,i=9,q=2", Rgba((10, 0, 0, 255))));
        terminal.Write(Apc("a=f,f=32,s=1,v=1,i=9,z=40,q=2", Rgba((20, 0, 0, 255))));
        terminal.Write(Apc("a=a,i=9,r=1,z=40,q=2"));
        terminal.Write(Apc("a=a,i=9,s=3,v=1,q=2"));
        var first = terminal.ReadFrame();
        Check(terminal.PendingFrameRefreshDelay is { TotalMilliseconds: <= 1000 }, "A playing animation must schedule its next frame.");
        var seen = new HashSet<byte> { first.Images.Placements.Single().Image.Pixels.Span[0] };
        for (var i = 0; i < 20 && seen.Count < 2; i++)
        {
            Thread.Sleep(50);
            seen.Add(terminal.ReadFrame().Images.Placements.Single().Image.Pixels.Span[0]);
        }
        Check(seen.SetEquals([10, 20]), $"An animation must publish each frame's pixels, saw {string.Join(",", seen)}.");
        terminal.Write(Apc("a=a,i=9,s=1,q=2"));
        terminal.ReadFrame();
        terminal.ReadFrame();
        Check(terminal.PendingFrameRefreshDelay is null, "A stopped animation must not keep scheduling frames.");
    }

    /// <summary>Encodes a minimal 8-bit RGBA PNG.</summary>
    internal static byte[] Png(int width, int height, (byte R, byte G, byte B, byte A)[] pixels)
    {
        var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < width; x++) { var p = pixels[y * width + x]; raw.Write([p.R, p.G, p.B, p.A]); }
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw.ToArray());
        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();

        static void Chunk(Stream stream, string type, byte[] data)
        {
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            stream.Write(length);
            var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            stream.Write(typed);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, Crc(typed));
            stream.Write(length);
        }

        static uint Crc(byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
            return ~crc;
        }
    }
}
