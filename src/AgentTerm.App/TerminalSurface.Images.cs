using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using AgentTerm.Terminal;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace AgentTerm_App;

// Kitty graphics drawing. Frames carry owned premultiplied pixels; this file turns them into device bitmaps and
// draws each layer at its place in the cell paint order: below backgrounds, below text, above text.
public sealed partial class TerminalSurface
{
    private readonly ImageBitmaps _imageBitmaps = new();

    private static void DrawImages(CanvasDrawingSession drawing, TerminalFrame frame, TerminalImageLayer layer,
        float cellWidth, float cellHeight, float gridTop, ImageBitmaps? bitmaps)
    {
        var images = frame.Images;
        if (bitmaps is null || images.IsEmpty || images.CellPixelWidth <= 0 || images.CellPixelHeight <= 0) return;
        var grid = new Rect(0, gridTop, frame.Columns * cellWidth, TerminalGridGeometry.VisibleRows(frame) * cellHeight);
        if (grid.Width <= 0 || grid.Height <= 0) return;
        // Placements are in native pixels of one cell. Map them so a placement spanning n cells spans n drawn cells.
        float scaleX = cellWidth / images.CellPixelWidth, scaleY = cellHeight / images.CellPixelHeight;
        CanvasActiveLayer? clip = null;
        try
        {
            foreach (var placement in images.Placements)
            {
                if (placement.Layer != layer) continue;
                var target = PlacementBounds(placement, cellWidth, cellHeight, gridTop, scaleX, scaleY);
                if (target.Width <= 0 || target.Height <= 0 || !Intersects(target, grid)) continue;
                if (bitmaps.Get(drawing.Device, placement) is not { } bitmap) continue;
                clip ??= drawing.CreateLayer(1, grid);
                var size = bitmap.Bitmap.SizeInPixels;
                drawing.DrawImage(bitmap.Bitmap, target, new Rect(0, 0, size.Width, size.Height), 1, CanvasImageInterpolation.Linear);
            }
        }
        finally { clip?.Dispose(); }
    }

    /// <summary>The drawn rectangle of a placement in surface DIPs.</summary>
    internal static Rect PlacementBounds(TerminalImagePlacement placement, float cellWidth, float cellHeight, float gridTop,
        float scaleX, float scaleY) =>
        new(placement.Column * cellWidth + placement.CellOffsetX * scaleX,
            gridTop + placement.Row * cellHeight + placement.CellOffsetY * scaleY,
            placement.PixelWidth * scaleX, placement.PixelHeight * scaleY);

    private static bool Intersects(Rect a, Rect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    /// <summary>
    /// Device bitmaps keyed by image generation, which uniquely names a version of an image's pixels, and the source
    /// rectangle. A cropped placement gets a bitmap of just its crop, because filtered sampling of a rectangle inside a
    /// larger bitmap blends in pixels from outside the crop at its edges. Bitmaps belong to one device: a new device,
    /// a lost device or a released canvas clears them. Bitmaps the current frame draws always stay; others are evicted
    /// oldest first above <see cref="Budget"/>, which keeps recently scrolled-away images cheap to bring back without
    /// holding every image the session ever showed.
    /// </summary>
    internal sealed class ImageBitmaps : IDisposable
    {
        internal const long Budget = 64L * 1024 * 1024;
        private readonly Dictionary<Key, Entry> _entries = [];
        private CanvasDevice? _device;
        private long _bytes, _clock;

        private readonly record struct Key(ulong Generation, int X, int Y, int Width, int Height)
        {
            internal static Key For(TerminalImagePlacement p) => new(p.Image.Generation, p.SourceX, p.SourceY, p.SourceWidth, p.SourceHeight);
        }

        /// <summary>A device bitmap of one source rectangle. <c>Scale</c> is below one when it was resampled to fit the device.</summary>
        internal sealed record Entry(CanvasBitmap Bitmap, float Scale, long Bytes)
        {
            public long Used { get; set; }
        }

        internal int Count => _entries.Count;
        internal long Bytes => _bytes;

        internal Entry? Get(CanvasDevice device, TerminalImagePlacement placement)
        {
            if (!ReferenceEquals(device, _device))
            {
                Clear();
                _device = device;
            }
            var key = Key.For(placement);
            if (_entries.TryGetValue(key, out var entry))
            {
                entry.Used = ++_clock;
                return entry;
            }
            var image = placement.Image;
            if (key.Width <= 0 || key.Height <= 0 || key.X < 0 || key.Y < 0 ||
                (long)key.X + key.Width > image.Width || (long)key.Y + key.Height > image.Height) return null;
            var (pixels, width, height, scale) = FitToDevice(Crop(image, key), key.Width, key.Height, device.MaximumBitmapSizeInPixels);
            var bitmap = CanvasBitmap.CreateFromBytes(device, pixels, width, height,
                DirectXPixelFormat.R8G8B8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
            entry = new Entry(bitmap, scale, (long)width * height * 4) { Used = ++_clock };
            _entries.Add(key, entry);
            _bytes += entry.Bytes;
            return entry;
        }

        /// <summary>Evicts bitmaps the frame does not draw until the cache fits its budget.</summary>
        internal void Trim(TerminalImages current)
        {
            if (_bytes <= Budget) return;
            var keep = current.Placements.Select(Key.For).ToHashSet();
            foreach (var (key, entry) in _entries.Where(e => !keep.Contains(e.Key)).OrderBy(e => e.Value.Used).ToArray())
            {
                if (_bytes <= Budget) break;
                entry.Bitmap.Dispose();
                _entries.Remove(key);
                _bytes -= entry.Bytes;
            }
        }

        internal void Clear()
        {
            foreach (var entry in _entries.Values) entry.Bitmap.Dispose();
            _entries.Clear();
            _bytes = 0;
            _device = null;
        }

        public void Dispose() => Clear();

        /// <summary>Returns the source rectangle's pixels, sharing the image's array when the rectangle is the whole image.</summary>
        private static byte[] Crop(TerminalImageData image, Key key)
        {
            var whole = MemoryMarshal.TryGetArray(image.Pixels, out var segment) && segment.Offset == 0 && segment.Count == segment.Array!.Length
                ? segment.Array : image.Pixels.ToArray();
            if (key.X == 0 && key.Y == 0 && key.Width == image.Width && key.Height == image.Height) return whole;
            var cropped = new byte[key.Width * key.Height * 4];
            for (var y = 0; y < key.Height; y++)
                Buffer.BlockCopy(whole, ((key.Y + y) * image.Width + key.X) * 4, cropped, y * key.Width * 4, key.Width * 4);
            return cropped;
        }

        /// <summary>
        /// Returns pixels no larger than the device's bitmap limit. Larger sources are resampled on the CPU with
        /// nearest sampling.
        /// </summary>
        private static (byte[] Pixels, int Width, int Height, float Scale) FitToDevice(byte[] pixels, int sourceWidth, int sourceHeight, int maximum)
        {
            if (sourceWidth <= maximum && sourceHeight <= maximum) return (pixels, sourceWidth, sourceHeight, 1);
            var scale = (float)maximum / Math.Max(sourceWidth, sourceHeight);
            int width = Math.Clamp((int)(sourceWidth * scale), 1, maximum), height = Math.Clamp((int)(sourceHeight * scale), 1, maximum);
            var resized = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                var sourceRow = Math.Min(sourceHeight - 1, (int)(y / scale)) * sourceWidth;
                for (var x = 0; x < width; x++)
                    Buffer.BlockCopy(pixels, (sourceRow + Math.Min(sourceWidth - 1, (int)(x / scale))) * 4, resized, (y * width + x) * 4, 4);
            }
            return (resized, width, height, scale);
        }
    }
}
