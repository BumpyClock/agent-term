using System.Buffers.Binary;
using System.IO.Compression;

namespace AgentTerm.Terminal;

/// <summary>
/// Decodes PNG to straight 8-bit RGBA for Kitty graphics transmissions (f=100). libghostty-vt has no PNG decoder of
/// its own and rejects PNG payloads until the host installs one. Input is untrusted program output: every length and
/// dimension is checked, and the decoder reads no more inflated data than the declared image needs.
/// Supports every critical-chunk feature of PNG 1.2: bit depths 1 to 16, gray, RGB, palette, gray+alpha, RGBA, tRNS
/// transparency and Adam7 interlacing. Ancillary color management chunks are ignored.
/// </summary>
public static class PngDecoder
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Largest decoded image accepted, in pixels. Matches a 16384 x 16384 frame.</summary>
    public const long MaxPixels = 16384L * 16384;

    /// <summary>Returns straight RGBA pixels, or null when the data is not a PNG this decoder can read.</summary>
    public static byte[]? TryDecode(ReadOnlySpan<byte> png, out int width, out int height, long maxPixels = MaxPixels)
    {
        width = height = 0;
        try { return Decode(png, ref width, ref height, maxPixels); }
        catch (Exception error) when (error is InvalidDataException or OverflowException or ArgumentException or IndexOutOfRangeException)
        {
            width = height = 0;
            return null;
        }
    }

    private static byte[]? Decode(ReadOnlySpan<byte> png, ref int width, ref int height, long maxPixels)
    {
        if (png.Length < 8 || !png[..8].SequenceEqual(Signature)) return null;
        int depth = 0, colorType = -1, interlace = 0;
        byte[]? palette = null, transparency = null;
        using var compressed = new MemoryStream();
        var position = 8;
        var sawHeader = false;
        while (true)
        {
            if (png.Length - position < 12) return null;
            var length = BinaryPrimitives.ReadInt32BigEndian(png[position..]);
            if (length < 0 || length > png.Length - position - 12) return null;
            var type = png.Slice(position + 4, 4);
            var data = png.Slice(position + 8, length);
            position += 12 + length;
            if (!sawHeader && !type.SequenceEqual("IHDR"u8)) return null;
            if (type.SequenceEqual("IHDR"u8))
            {
                if (sawHeader || length != 13) return null;
                sawHeader = true;
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                depth = data[8];
                colorType = data[9];
                interlace = data[12];
                if (width <= 0 || height <= 0 || (long)width * height > maxPixels || data[10] != 0 || data[11] != 0 || interlace > 1) return null;
                var valid = colorType switch
                {
                    0 => depth is 1 or 2 or 4 or 8 or 16,
                    3 => depth is 1 or 2 or 4 or 8,
                    2 or 4 or 6 => depth is 8 or 16,
                    _ => false,
                };
                if (!valid) return null;
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                if (length % 3 != 0 || length / 3 > 256 || length == 0) return null;
                palette = data.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8)) transparency = data.ToArray();
            else if (type.SequenceEqual("IDAT"u8)) compressed.Write(data);
            else if (type.SequenceEqual("IEND"u8)) break;
            else if ((type[0] & 0x20) == 0) return null; // Unknown critical chunk.
        }
        if (colorType == 3 && palette is null) return null;
        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        var bitsPerPixel = channels * depth;
        var bytesPerPixel = Math.Max(1, bitsPerPixel / 8);
        var passes = interlace == 1 ? Adam7 : [(0, 0, 1, 1)];
        long expected = 0;
        foreach (var (x0, y0, dx, dy) in passes)
        {
            var (passWidth, passHeight) = PassSize(width, height, x0, y0, dx, dy);
            if (passWidth > 0 && passHeight > 0) expected += passHeight * (1 + ((long)passWidth * bitsPerPixel + 7) / 8);
        }
        if (expected > Array.MaxLength) return null;
        var inflated = new byte[expected];
        compressed.Position = 0;
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
        {
            var read = 0;
            while (read < inflated.Length)
            {
                var count = zlib.Read(inflated, read, inflated.Length - read);
                if (count == 0) return null;
                read += count;
            }
        }
        var output = new byte[checked((long)width * height * 4)];
        var offset = 0;
        foreach (var (x0, y0, dx, dy) in passes)
        {
            var (passWidth, passHeight) = PassSize(width, height, x0, y0, dx, dy);
            if (passWidth == 0 || passHeight == 0) continue;
            var stride = (int)(((long)passWidth * bitsPerPixel + 7) / 8);
            var previous = new byte[stride];
            var current = new byte[stride];
            for (var y = 0; y < passHeight; y++)
            {
                var filter = inflated[offset++];
                inflated.AsSpan(offset, stride).CopyTo(current);
                offset += stride;
                if (!Unfilter(filter, current, previous, bytesPerPixel)) return null;
                for (var x = 0; x < passWidth; x++)
                    WritePixel(output, ((y0 + y * dy) * width + x0 + x * dx) * 4, current, x, depth, colorType, palette, transparency);
                (previous, current) = (current, previous);
            }
        }
        return output;
    }

    private static readonly (int X, int Y, int Dx, int Dy)[] Adam7 =
        [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];

    private static (int Width, int Height) PassSize(int width, int height, int x0, int y0, int dx, int dy) =>
        (width > x0 ? (width - x0 + dx - 1) / dx : 0, height > y0 ? (height - y0 + dy - 1) / dy : 0);

    private static bool Unfilter(byte filter, Span<byte> row, ReadOnlySpan<byte> above, int bpp)
    {
        switch (filter)
        {
            case 0: return true;
            case 1: for (var i = bpp; i < row.Length; i++) row[i] += row[i - bpp]; return true;
            case 2: for (var i = 0; i < row.Length; i++) row[i] += above[i]; return true;
            case 3:
                for (var i = 0; i < row.Length; i++) row[i] += (byte)(((i >= bpp ? row[i - bpp] : 0) + above[i]) / 2);
                return true;
            case 4:
                for (var i = 0; i < row.Length; i++)
                {
                    int a = i >= bpp ? row[i - bpp] : 0, b = above[i], c = i >= bpp ? above[i - bpp] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    row[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                }
                return true;
            default: return false;
        }
    }

    private static void WritePixel(byte[] output, int target, ReadOnlySpan<byte> row, int x, int depth, int colorType,
        byte[]? palette, byte[]? transparency)
    {
        byte r, g, b, a = 255;
        switch (colorType)
        {
            case 0:
            {
                var raw = Sample(row, x, depth);
                r = g = b = Scale(raw, depth);
                if (transparency is { Length: >= 2 } && raw == BinaryPrimitives.ReadUInt16BigEndian(transparency)) a = 0;
                break;
            }
            case 2:
            {
                int sr = Sample(row, x * 3, depth), sg = Sample(row, x * 3 + 1, depth), sb = Sample(row, x * 3 + 2, depth);
                (r, g, b) = (Scale(sr, depth), Scale(sg, depth), Scale(sb, depth));
                if (transparency is { Length: >= 6 } && sr == BinaryPrimitives.ReadUInt16BigEndian(transparency) &&
                    sg == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2)) && sb == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4))) a = 0;
                break;
            }
            case 3:
            {
                var index = Sample(row, x, depth);
                if (index * 3 + 2 >= palette!.Length) throw new InvalidDataException("Palette index out of range.");
                (r, g, b) = (palette[index * 3], palette[index * 3 + 1], palette[index * 3 + 2]);
                if (transparency is not null && index < transparency.Length) a = transparency[index];
                break;
            }
            case 4:
                r = g = b = Scale(Sample(row, x * 2, depth), depth);
                a = Scale(Sample(row, x * 2 + 1, depth), depth);
                break;
            default:
                (r, g, b, a) = (Scale(Sample(row, x * 4, depth), depth), Scale(Sample(row, x * 4 + 1, depth), depth),
                    Scale(Sample(row, x * 4 + 2, depth), depth), Scale(Sample(row, x * 4 + 3, depth), depth));
                break;
        }
        output[target] = r; output[target + 1] = g; output[target + 2] = b; output[target + 3] = a;
    }

    /// <summary>Reads sample number <paramref name="index"/> of a row at the given bit depth.</summary>
    private static int Sample(ReadOnlySpan<byte> row, int index, int depth) => depth switch
    {
        8 => row[index],
        16 => BinaryPrimitives.ReadUInt16BigEndian(row[(index * 2)..]),
        _ => (row[index * depth / 8] >> (8 - depth - index * depth % 8)) & ((1 << depth) - 1),
    };

    private static byte Scale(int sample, int depth) => depth switch
    {
        8 => (byte)sample,
        16 => (byte)(sample >> 8),
        _ => (byte)(sample * 255 / ((1 << depth) - 1)),
    };
}
