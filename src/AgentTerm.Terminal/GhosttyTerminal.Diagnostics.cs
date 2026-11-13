using System.Runtime.InteropServices;
using System.Text;

namespace AgentTerm.Terminal;

public sealed partial class GhosttyTerminal
{
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
        ThrowIfFailed(RenderNative.ghostty_terminal_get(_raw, 42, &memory), "Could not read terminal memory.");
        byte processingError = 0;
        ThrowIfFailed(RenderNative.ghostty_terminal_get(_raw, 33, &processingError), "Could not read VT processing status.");
        report.AppendLine($"Grid {_columns} x {_rows}, cell pixels {_cellWidthPx} x {_cellHeightPx}");
        report.AppendLine($"Host clipboard {_hostCapabilities.Clipboard}, host Kitty images {_hostCapabilities.KittyImages}");
        report.AppendLine($"Page resident bytes {memory.Values[2] + memory.Values[8]}, reserved bytes {memory.Values[1] + memory.Values[7]}");
        report.AppendLine($"Image bytes {memory.Values[5] + memory.Values[11]}, compression supported {memory.CompressionSupported != 0}");
        report.AppendLine($"VT processing error {processingError != 0} (sticky informational status; parsing continues)");
        return report.ToString();
    }

    private static unsafe partial class DiagnosticNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Memory { internal nuint Size; internal byte CompressionSupported; internal fixed ulong Values[12]; }
        [LibraryImport("ghostty-vt")]
        internal static partial IntPtr ghostty_version();
        [LibraryImport("ghostty-vt")]
        internal static partial int ghostty_build_info(int key, void* value);
    }
}
