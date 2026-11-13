using System.Runtime.InteropServices;

namespace AgentTerm.Terminal;

public enum TerminalExportFormat { Plain = 0, StyledVt = 1, Html = 2 }

public sealed partial class GhosttyTerminal
{
    /// <summary>Exports the active screen and scrollback, or its inclusive native selection. Soft wraps unwrap; trailing whitespace trims.</summary>
    public unsafe string? ExportHistory(TerminalExportFormat format, bool selectionOnly = false, bool unwrap = true, bool trim = true)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        Native.Selection selection = default;
        if (selectionOnly)
        {
            var result = RenderNative.ghostty_terminal_get(_raw, 31, &selection);
            if (result == -4) return null;
            ThrowIfFailed(result, "Could not read export selection.");
        }
        ThrowIfFailed(Native.ghostty_formatter_terminal_new(IntPtr.Zero, out var ptr, _handle), "Could not create history formatter.");
        using var formatter = new FormatterHandle(ptr);
        int emit = (int)format;
        byte u = unwrap && (!selectionOnly || selection.Rectangle == 0) ? (byte)1 : (byte)0, t = trim ? (byte)1 : (byte)0;
        ThrowIfFailed(ScrollNative.ghostty_formatter_set(formatter, 1, &emit), "Could not set export format.");
        ThrowIfFailed(ScrollNative.ghostty_formatter_set(formatter, 2, &u), "Could not set export wrapping.");
        ThrowIfFailed(ScrollNative.ghostty_formatter_set(formatter, 3, &t), "Could not set export trimming.");
        if (selectionOnly) ThrowIfFailed(Native.ghostty_formatter_set(formatter, Native.FormatterOption.Selection, in selection), "Could not set export selection.");
        ThrowIfFailed(Native.ghostty_formatter_format_alloc(formatter, IntPtr.Zero, out var output, out var length), "Could not export history.");
        try { return length == 0 ? string.Empty : Marshal.PtrToStringUTF8(output, checked((int)length))!; }
        finally { Native.ghostty_free(IntPtr.Zero, output, length); }
    }

    public void NavigatePrompt(int delta)
    {
        ThrowIfDisposed();
        EnsurePresentation();
        ThrowIfFailed(ScrollNative.ghostty_terminal_scroll_viewport(_handle, new ScrollBehavior { Tag = 4, Value = delta }), "Could not navigate prompts.");
        _following = ReadViewport().Offset == ReadViewport().MaximumOffset;
        _viewRevision++;
        CaptureSelectedViewport();
    }

    public ulong CompressionActivity
    {
        get { ThrowIfDisposed(); ThrowIfFailed(HistoryNative.ghostty_terminal_compression_activity(_handle, out var token), "Could not read compression activity."); return token; }
    }
    /// <summary>One bounded step. True means another idle step is useful. Unsupported targets return false.</summary>
    public bool CompressHistoryStep()
    {
        ThrowIfDisposed();
        ThrowIfFailed(HistoryNative.ghostty_terminal_compress(_handle, 0, out var result), "Could not compress history.");
        return result == 1;
    }
    private static partial class HistoryNative
    {
        [LibraryImport("ghostty-vt")] internal static partial int ghostty_terminal_compression_activity(TerminalHandle terminal, out ulong activity);
        [LibraryImport("ghostty-vt")] internal static partial int ghostty_terminal_compress(TerminalHandle terminal, int mode, out int result);
    }
}
