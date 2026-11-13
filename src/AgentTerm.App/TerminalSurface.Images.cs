using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using AgentTerm.Terminal;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace AgentTerm_App;

public sealed partial class TerminalSurface
{
    private static void DrawImages(CanvasDrawingSession drawing, TerminalFrame frame, TerminalImageLayer layer,
        float cellWidth, float cellHeight, float gridTop, ImageBitmaps? bitmaps, Rect? viewportClip = null, Rect? damageClip = null)
    {
        var images = frame.Images;
        if (bitmaps is null || images.IsEmpty || images.CellPixelWidth <= 0 || images.CellPixelHeight <= 0) return;
        var grid = viewportClip ?? new Rect(0, gridTop, frame.Columns * cellWidth, TerminalGridGeometry.VisibleRows(frame) * cellHeight);
        if (grid.Width <= 0 || grid.Height <= 0) return;
        float scaleX = cellWidth / images.CellPixelWidth, scaleY = cellHeight / images.CellPixelHeight;
        CanvasActiveLayer? clip = null;
        try
        {
            foreach (var placement in images.Placements)
            {
                if (placement.Layer != layer) continue;
                var target = PlacementBounds(placement, cellWidth, cellHeight, gridTop, scaleX, scaleY);
                if (target.Width <= 0 || target.Height <= 0 || !Intersects(target, grid) ||
                    damageClip is { } damage && !Intersects(target, damage)) continue;
                if (bitmaps.Get(drawing.Device, placement, drawing) is not { } bitmap) continue;
                clip ??= drawing.CreateLayer(1, grid);
                var size = bitmap.Bitmap.SizeInPixels;
                try { drawing.DrawImage(bitmap.Bitmap, target, new Rect(0, 0, size.Width, size.Height), 1, CanvasImageInterpolation.Linear); }
                finally
                {
                    if (bitmap.Transient)
                    {
                        drawing.Flush();
                        bitmap.Bitmap.Dispose();
                    }
                }
            }
        }
        finally { clip?.Dispose(); }
    }

    internal static Rect PlacementBounds(TerminalImagePlacement placement, float cellWidth, float cellHeight, float gridTop,
        float scaleX, float scaleY) =>
        new(placement.Column * cellWidth + placement.CellOffsetX * scaleX,
            gridTop + placement.Row * cellHeight + placement.CellOffsetY * scaleY,
            placement.PixelWidth * scaleX, placement.PixelHeight * scaleY);

    private static bool Intersects(Rect a, Rect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    internal static void AddImageDamage(TerminalImages previous, TerminalImages current, int columns, int rows,
        float cellWidth, float cellHeight, float gridTop,
        List<(int FirstRow, int EndRow, int FirstColumn, int EndColumn)> spans)
    {
        var sameScale = previous.CellPixelWidth == current.CellPixelWidth && previous.CellPixelHeight == current.CellPixelHeight;
        for (var i = 0; i < Math.Max(previous.Placements.Count, current.Placements.Count); i++)
        {
            var before = i < previous.Placements.Count ? previous.Placements[i] : null;
            var after = i < current.Placements.Count ? current.Placements[i] : null;
            if (sameScale && before == after) continue;
            Add(before, previous);
            Add(after, current);
        }

        void Add(TerminalImagePlacement? placement, TerminalImages images)
        {
            if (placement is null || images.CellPixelWidth <= 0 || images.CellPixelHeight <= 0) return;
            var bounds = PlacementBounds(placement, cellWidth, cellHeight, gridTop,
                cellWidth / images.CellPixelWidth, cellHeight / images.CellPixelHeight);
            var firstRow = (int)Math.Clamp(Math.Floor((bounds.Top - gridTop) / cellHeight), 0, rows);
            var endRow = (int)Math.Clamp(Math.Ceiling((bounds.Bottom - gridTop) / cellHeight), 0, rows);
            var firstColumn = (int)Math.Clamp(Math.Floor(bounds.Left / cellWidth), 0, columns);
            var endColumn = (int)Math.Clamp(Math.Ceiling(bounds.Right / cellWidth), 0, columns);
            if (firstRow < endRow && firstColumn < endColumn)
            {
                var span = (firstRow, endRow, firstColumn, endColumn);
                if (spans.Count == 0 || spans[^1] != span) spans.Add(span);
            }
        }
    }

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

        internal sealed record Entry(CanvasBitmap Bitmap, float Scale, long Bytes)
        {
            public long Used { get; set; }
            public bool Transient { get; init; }
        }

        internal int Count => _entries.Count;
        internal long Bytes => _bytes;
        internal long Requests { get; private set; }

        internal Entry? Get(CanvasDevice device, TerminalImagePlacement placement, CanvasDrawingSession? drawing = null)
        {
            Requests++;
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
            if (_bytes + (long)width * height * 4 > Budget)
            {
                drawing?.Flush();
                EvictTo(Math.Max(0, Budget - (long)width * height * 4));
            }
            var bitmap = CanvasBitmap.CreateFromBytes(device, pixels, width, height,
                DirectXPixelFormat.R8G8B8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
            entry = new Entry(bitmap, scale, (long)width * height * 4) { Used = ++_clock };
            if (entry.Bytes > Budget) return entry with { Transient = true };
            _entries.Add(key, entry);
            _bytes += entry.Bytes;
            return entry;
        }

        internal void Trim(TerminalImages current)
        {
            if (_bytes <= Budget) return;
            EvictTo(Budget);
        }

        private void EvictTo(long limit)
        {
            foreach (var (key, entry) in _entries.OrderBy(e => e.Value.Used).ToArray())
            {
                if (_bytes <= limit) break;
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
