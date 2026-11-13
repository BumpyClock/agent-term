using System.Runtime.InteropServices;

namespace AgentTerm.Terminal;

public sealed partial class GhosttyTerminal
{
    internal const int MaximumSnapshotBytes = 64 * 1024 * 1024;
    private const int MaximumContinuationBytes = 1024 * 1024;

    private unsafe void ConfigureSnapshot()
    {
        nuint limit = MaximumContinuationBytes;
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 32, (IntPtr)(&limit)), "Could not enable snapshot continuation tracking.");
    }

    /// <summary>Copies complete native state. Serialize with all terminal access, including frame capture.</summary>
    public unsafe byte[] ExportSnapshot()
    {
        ThrowIfDisposed();
        nuint length = 0;
        var result = SnapshotNative.ghostty_snapshot_encode_buf(_handle, null, 0, &length);
        if (result != -3) ThrowIfFailed(result, "Could not measure terminal snapshot. Its unfinished sequence may exceed the continuation limit.");
        if (length == 0 || length > MaximumSnapshotBytes) throw new InvalidDataException("Terminal snapshot exceeds the 64 MiB archive limit.");
        var bytes = new byte[(int)length];
        fixed (byte* pointer = bytes)
            ThrowIfFailed(SnapshotNative.ghostty_snapshot_encode_buf(_handle, pointer, length, &length), "Could not encode terminal snapshot.");
        GC.KeepAlive(this);
        return bytes;
    }

    /// <summary>Restores into a new independent terminal. Existing terminals are never modified.</summary>
    public static unsafe GhosttyTerminal FromSnapshot(ReadOnlySpan<byte> snapshot)
    {
        if (snapshot.IsEmpty || snapshot.Length > MaximumSnapshotBytes) throw new InvalidDataException("Invalid terminal snapshot size. Maximum is 64 MiB.");
        fixed (byte* pointer = snapshot)
        {
            DecodeCheck(SnapshotNative.ghostty_snapshot_decoder_new_buf(IntPtr.Zero, out var decoder, pointer, (nuint)snapshot.Length));
            IntPtr terminal = IntPtr.Zero;
            try
            {
                nuint maximum = MaximumContinuationBytes;
                byte yes = 1;
                DecodeCheck(SnapshotNative.ghostty_snapshot_decoder_set(decoder, 1, &maximum));
                DecodeCheck(SnapshotNative.ghostty_snapshot_decoder_set(decoder, 2, &yes));
                DecodeCheck(SnapshotNative.ghostty_snapshot_decoder_set(decoder, 3, &yes));
                ulong imageLimit = TerminalImages.DefaultStorageLimit;
                DecodeCheck(SnapshotNative.ghostty_snapshot_decoder_set(decoder, 4, &imageLimit));
                DecodeCheck(SnapshotNative.ghostty_snapshot_decoder_decode(decoder, out terminal));
                nuint consumed = 0;
                DecodeCheck(SnapshotNative.ghostty_snapshot_decoder_get(decoder, 2, &consumed));
                if (consumed != (nuint)snapshot.Length) throw new InvalidDataException("Terminal snapshot has trailing data.");
                ushort columns = 0, rows = 0;
                DecodeCheck(SnapshotNative.ghostty_terminal_get(terminal, 1, &columns));
                DecodeCheck(SnapshotNative.ghostty_terminal_get(terminal, 2, &rows));
                var owned = terminal;
                terminal = IntPtr.Zero; // Constructor takes ownership even when callback setup fails.
                return new GhosttyTerminal(columns, rows, owned);
            }
            finally
            {
                SnapshotNative.ghostty_snapshot_decoder_free(decoder);
                if (terminal != IntPtr.Zero) Native.ghostty_terminal_free(terminal);
            }
        }
    }

    private static void DecodeCheck(int result)
    {
        if (result != 0) throw new InvalidDataException($"Terminal snapshot is invalid, truncated, unsupported, or exceeds a decoder limit (libghostty-vt result {result}).");
    }

    private static unsafe partial class SnapshotNative
    {
        private const string Lib = "ghostty-vt";
        [LibraryImport(Lib)] internal static partial int ghostty_terminal_get(IntPtr terminal, int key, void* value);
        [LibraryImport(Lib)] internal static partial int ghostty_snapshot_encode_buf(TerminalHandle terminal, byte* data, nuint capacity, nuint* written);
        [LibraryImport(Lib)] internal static partial int ghostty_snapshot_decoder_new_buf(IntPtr allocator, out IntPtr decoder, byte* data, nuint length);
        [LibraryImport(Lib)] internal static partial int ghostty_snapshot_decoder_set(IntPtr decoder, int option, void* value);
        [LibraryImport(Lib)] internal static partial int ghostty_snapshot_decoder_get(IntPtr decoder, int key, void* value);
        [LibraryImport(Lib)] internal static partial int ghostty_snapshot_decoder_decode(IntPtr decoder, out IntPtr terminal);
        [LibraryImport(Lib)] internal static partial void ghostty_snapshot_decoder_free(IntPtr decoder);
    }
}
