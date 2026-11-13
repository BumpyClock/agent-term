using AgentTerm.Terminal;

/// <summary>PngDecoder against independently encoded fixtures and hostile input.</summary>
internal static class PngDecoderChecks
{
    public static void Run()
    {
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
