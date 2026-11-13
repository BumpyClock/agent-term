using AgentTerm.Terminal;
using System.Buffers.Binary;
using System.IO.Compression;

/// <summary>PngDecoder against independently encoded fixtures and hostile input.</summary>
internal static class PngDecoderChecks
{
    public static void Run()
    {
        CheckDecodeAllocation();
        foreach (var (name, width, height, png, rgba) in PngFixtures.All)
        {
            var pixels = PngDecoder.TryDecode(Convert.FromBase64String(png), out var w, out var h);
            if (pixels is null || w != width || h != height || !pixels.AsSpan().SequenceEqual(Convert.FromBase64String(rgba)))
                throw new InvalidOperationException($"PNG {name} must decode to its expected RGBA.");
        }
        var sample = Convert.FromBase64String(PngFixtures.All[^1].Png);
        Reject(sample[..20], "a truncated header");
        Reject(sample[..^20], "missing image data");
        Reject([.. sample[..8], .. sample[33..]], "a missing IHDR");
        var huge = (byte[])sample.Clone();
        huge[16] = 0x7F;
        Reject(huge, "dimensions beyond the pixel limit");
        if (PngDecoder.TryDecode(sample, out _, out _, maxPixels: 10) is not null)
            throw new InvalidOperationException("The caller's pixel limit must apply.");
        var impossible = (byte[])sample.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(impossible.AsSpan(16), uint.MaxValue);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(impossible.AsSpan(20), uint.MaxValue);
        Reject(impossible, "maximum unsigned IHDR dimensions");
        RejectCallbackBeforeAllocator(impossible);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(impossible.AsSpan(16), 16384);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(impossible.AsSpan(20), 16384);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        Reject(impossible, "a huge IHDR and tiny compressed stream");
        RejectCallbackBeforeAllocator(impossible);
        if (GC.GetAllocatedBytesForCurrentThread() - allocated > 1024 * 1024)
            throw new InvalidOperationException("Impossible inflated size must reject with bounded scratch allocation.");
        var random = new Random(7);
        for (var i = 0; i < 2000; i++)
        {
            var (_, _, _, png, _) = PngFixtures.All[i % PngFixtures.All.Length];
            var mutated = Convert.FromBase64String(png);
            for (var flips = random.Next(1, 4); flips > 0; flips--) mutated[random.Next(8, mutated.Length)] ^= (byte)random.Next(1, 256);
            var pixels = PngDecoder.TryDecode(mutated, out var w, out var h);
            if (pixels is not null && pixels.Length != w * h * 4)
                throw new InvalidOperationException("A decoded PNG must hold four bytes for every pixel.");
        }
        Console.WriteLine($"PNG decoder checks passed {PngFixtures.All.Length} fixtures and 2000 corrupted inputs.");
    }

    private static void CheckDecodeAllocation()
    {
        // Build valid, compressible pixels without constructing an image-sized fixture buffer.
        const int width = 1024, height = 1024, outputBytes = width * height * 4;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[width * 4 + 1];
            for (var x = 0; x < width; x++) { row[1 + x * 4] = 173; row[4 + x * 4] = 255; }
            for (var y = 0; y < height; y++) zlib.Write(row);
        }
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6;
        Chunk("IHDR"u8, header); Chunk("IDAT"u8, compressed.ToArray()); Chunk("IEND"u8, []);
        var fixture = png.ToArray();
        PngDecoder.TryDecode(Convert.FromBase64String(PngFixtures.All[0].Png), out _, out _);
        var before = GC.GetAllocatedBytesForCurrentThread();
        if (PngDecoder.TryDecode(fixture, out _, out _, maxDecodedBytes: outputBytes - 1) is not null)
            throw new InvalidOperationException("A valid PNG above the caller's decoded byte budget must be rejected.");
        var rejectedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        if (rejectedBytes > 128 * 1024) throw new InvalidOperationException("An over-budget PNG must reject before image-sized allocation.");
        before = GC.GetAllocatedBytesForCurrentThread();
        var decoded = PngDecoder.TryDecode(fixture, out var w, out var h, maxDecodedBytes: outputBytes);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (decoded is null || w != width || h != height || decoded.Length != outputBytes)
            throw new InvalidOperationException("A PNG exactly at its byte budget must decode.");
        for (var i = 0; i < decoded.Length; i += 4)
            if (decoded[i] != 173 || decoded[i + 1] != 0 || decoded[i + 2] != 0 || decoded[i + 3] != 255)
                throw new InvalidOperationException("Streaming PNG rows must preserve every pixel.");
        if (allocated > outputBytes + 512 * 1024)
            throw new InvalidOperationException($"PNG decoding must retain only RGBA plus bounded row/stream scratch; allocated {allocated} bytes.");
        Console.WriteLine($"PNG 1024x1024 allocated {allocated} bytes for {outputBytes} RGBA bytes; over-budget rejection allocated {rejectedBytes} bytes.");

        void Chunk(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length); png.Write(length); png.Write(type); png.Write(data);
            uint crc = uint.MaxValue;
            foreach (var value in type) Update(value);
            foreach (var value in data) Update(value);
            BinaryPrimitives.WriteUInt32BigEndian(length, ~crc); png.Write(length);
            void Update(byte value)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
            }
        }
    }

    private static unsafe void RejectCallbackBeforeAllocator(byte[] png)
    {
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte*, nuint, GhosttyTerminal.SysImage*, byte> decode = &GhosttyTerminal.DecodePng;
        var before = RenderOwnershipChecks.FailingAllocator.Allocations;
        GhosttyTerminal.SysImage output = default;
        fixed (byte* data = png)
            if (decode(IntPtr.Zero, RenderOwnershipChecks.FailingAllocator.Instance, data, (nuint)png.Length, &output) != 0)
                throw new InvalidOperationException("The native PNG callback must reject hostile dimensions and impossible inflated sizes.");
        if (RenderOwnershipChecks.FailingAllocator.Allocations != before)
            throw new InvalidOperationException("Rejected PNG dimensions and short inflated streams must not reach the native allocator.");
    }

    private static void Reject(byte[] png, string reason)
    {
        if (PngDecoder.TryDecode(png, out _, out _) is not null)
            throw new InvalidOperationException($"PNG with {reason} must be rejected.");
    }
}
