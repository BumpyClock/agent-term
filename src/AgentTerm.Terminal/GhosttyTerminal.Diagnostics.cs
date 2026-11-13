using System.Runtime.InteropServices;
using System.Text;

namespace AgentTerm.Terminal;

public sealed partial class GhosttyTerminal
{
    private DiagnosticNative.UnknownCallback? _unknownCallback;
    private readonly Dictionary<(int Kind, int Command), (long Count, long Truncated)> _unknownSequences = [];
    private long _otherUnknownSequences;
    public bool UnknownSequenceDiagnosticsEnabled { get; private set; }

    /// <summary>Opt-in compatibility counters. Retains identifiers only, never sequence payloads.</summary>
    public unsafe void SetUnknownSequenceDiagnostics(bool enabled)
    {
        ThrowIfDisposed();
        if (enabled == UnknownSequenceDiagnosticsEnabled) return;
        _unknownCallback ??= (_, _, sequence) =>
        {
            try
            {
                if (sequence->Kind is not (0 or 1)) return;
                // Unknown OSCs start with their numeric command. APCs have no common numeric grammar.
                // Inspect only that identifier; private markers, clipboard data and other payloads are not copied.
                var command = -1;
                if (sequence->Kind == 1)
                {
                    var bytes = new ReadOnlySpan<byte>((void*)sequence->Content.Data, (int)sequence->Content.Length);
                    var digits = 0;
                    var value = 0;
                    while (digits < bytes.Length && digits < 6 && bytes[digits] is >= (byte)'0' and <= (byte)'9')
                        value = value * 10 + bytes[digits++] - '0';
                    if (digits > 0 && (digits == bytes.Length || bytes[digits] == ';')) command = value;
                }
                var key = (sequence->Kind, command);
                if (!_unknownSequences.TryGetValue(key, out var counters) && _unknownSequences.Count == 16)
                {
                    _otherUnknownSequences++;
                    return;
                }
                _unknownSequences[key] = (counters.Count + 1, counters.Truncated + (sequence->Truncated != 0 ? 1 : 0));
            }
            catch (Exception error) { _callbackError = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error); }
        };
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 36, enabled ? Marshal.GetFunctionPointerForDelegate(_unknownCallback) : IntPtr.Zero),
            "Could not configure unsupported protocol diagnostics.");
        nuint limit = enabled ? 64u : 0u;
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 37, (IntPtr)(&limit)), "Could not bound unsupported protocol diagnostics.");
        UnknownSequenceDiagnosticsEnabled = enabled;
        if (!enabled) { _unknownSequences.Clear(); _otherUnknownSequences = 0; }
    }

    /// <summary>Copies native build and terminal diagnostics. VT processing errors are informational and sticky.</summary>
    public unsafe string ReadDiagnosticReport()
    {
        ThrowIfDisposed();
        var report = new StringBuilder();
        report.AppendLine(HostVersion);
        report.AppendLine("libghostty-vt " + Marshal.PtrToStringUTF8(DiagnosticNative.ghostty_version()));
        report.AppendLine($"Host runtime {RuntimeInformation.RuntimeIdentifier}");
        int optimization = 0;
        ThrowIfFailed(DiagnosticNative.ghostty_build_info(4, &optimization), "Could not read native build.");
        report.AppendLine($"Native optimization {new[] { "Debug", "ReleaseSafe", "ReleaseSmall", "ReleaseFast" }[optimization]}");
        var keys = new[] { 1, 2, 3, 11, 12, 13, 14, 15, 16, 17, 18, 19 };
        var names = new[] { "SIMD", "Kitty graphics", "tmux", "snapshot", "formatter", "selection", "search", "render state", "input encode", "color", "grid introspection", "glyph protocol" };
        for (var i = 0; i < keys.Length; i++)
        {
            byte enabled = 0;
            ThrowIfFailed(DiagnosticNative.ghostty_build_info(keys[i], &enabled), "Could not read native feature.");
            report.AppendLine($"Native {names[i]} {enabled != 0}");
        }
        DiagnosticNative.Memory memory = new() { Size = (nuint)sizeof(DiagnosticNative.Memory) };
        ThrowIfFailed(RenderNative.ghostty_terminal_get(_handle, 42, &memory), "Could not read terminal memory.");
        byte processingError = 0;
        ThrowIfFailed(RenderNative.ghostty_terminal_get(_handle, 33, &processingError), "Could not read VT processing status.");
        report.AppendLine($"Grid {_columns} x {_rows}, cell pixels {_cellWidthPx} x {_cellHeightPx}");
        report.AppendLine($"Host clipboard {_hostCapabilities.Clipboard || Effects.AdvertisesClipboardAccess}, host Kitty images {_hostCapabilities.KittyImages}");
        report.AppendLine($"Page resident bytes {memory.Values[2] + memory.Values[8]}, reserved bytes {memory.Values[1] + memory.Values[7]}");
        report.AppendLine($"Image bytes {memory.Values[5] + memory.Values[11]}, compression supported {memory.CompressionSupported != 0}");
        report.AppendLine($"Managed image raw cache bytes {_rawImages.Values.Sum(image => image.RawBytes)}, converted cache bytes {ImageCacheBytes}");
        report.AppendLine("Image cache figures exclude older retained frames and device resources.");
        report.AppendLine($"VT processing error {processingError != 0} (sticky informational status; parsing continues)");
        report.AppendLine($"Unsupported protocol counters {(UnknownSequenceDiagnosticsEnabled ? "enabled" : "disabled")} (identifiers only, no payloads)");
        foreach (var (key, counters) in _unknownSequences.OrderBy(pair => pair.Key.Kind).ThenBy(pair => pair.Key.Command))
            report.AppendLine($"Unsupported {(key.Kind == 1 ? "OSC" : "APC")} {(key.Command < 0 ? "unknown" : key.Command)}: {counters.Count}, truncated {counters.Truncated}");
        if (_otherUnknownSequences != 0) report.AppendLine($"Other unsupported sequences: {_otherUnknownSequences}");
        return report.ToString();
    }

    private static unsafe partial class DiagnosticNative
    {
        // Both union arms begin with a bool and a GhosttyString. The OSC terminator is unused.
        [StructLayout(LayoutKind.Explicit, Size = 136)]
        internal struct UnknownSequence
        {
            [FieldOffset(0)] internal int Kind;
            [FieldOffset(8)] internal byte Truncated;
            [FieldOffset(16)] internal Native.Utf8String Content;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void UnknownCallback(IntPtr terminal, IntPtr userdata, UnknownSequence* sequence);
        [StructLayout(LayoutKind.Sequential)]
        internal struct Memory { internal nuint Size; internal byte CompressionSupported; internal fixed ulong Values[12]; }
        [LibraryImport("ghostty-vt")]
        internal static partial IntPtr ghostty_version();
        [LibraryImport("ghostty-vt")]
        internal static partial int ghostty_build_info(int key, void* value);
    }
}
