using System.Runtime.InteropServices;

namespace AgentTerm.Terminal;

/// <summary>A tracked primary-screen cell where one command's output begins. Dispose it once the output is read.</summary>
internal sealed class CommandOutputStart(GhosttyTerminal.TrackedHandle handle) : IDisposable
{
    internal GhosttyTerminal.TrackedHandle Handle { get; } = handle;
    public void Dispose() => Handle.Dispose();
}

// Semantic command output comes from OSC 133 C..D row marks, which the native terminal keeps per row. Every method
// here needs terminal exclusion and keeps no untracked grid reference past its own call.
public sealed partial class GhosttyTerminal
{
    /// <summary>
    /// Selects the command output containing a screen-absolute cell, as Ghostty's select_output derives it.
    /// The row is <see cref="TerminalViewport.Offset"/> of the displayed frame plus the frame row, so a held frame maps
    /// correctly. Returns false when the cell is not command output; the current selection is then unchanged.
    /// </summary>
    public bool SelectCommandOutput(long screenRow, int column)
    {
        if (CommandOutputAt(screenRow, column) is not { } selection) return false;
        InstallSelection(selection);
        return true;
    }

    /// <summary>Plain text of the command output containing a screen-absolute cell, without changing the selection.</summary>
    public string? ReadCommandOutput(long screenRow, int column) =>
        CommandOutputAt(screenRow, column) is { } selection ? System.Text.Encoding.UTF8.GetString(FormatPlain(selection)) : null;

    private unsafe Native.Selection? CommandOutputAt(long screenRow, int column)
    {
        ThrowIfDisposed();
        if ((uint)column >= _columns || screenRow < 0 || screenRow > uint.MaxValue) return null;
        if (Native.ghostty_terminal_grid_ref(_handle, ScreenPoint((ushort)column, (uint)screenRow), out var cell) != 0) return null;
        Native.Selection selection = default;
        var result = SemanticNative.ghostty_terminal_select_output(_handle, &cell, &selection);
        if (result == NativeResult.NoValue) return null;
        ThrowIfFailed(result, "Could not select command output.");
        return selection;
    }

    /// <summary>Tracks the cursor row's first cell, where output starts after OSC 133 C. Null on the alternate screen.</summary>
    internal CommandOutputStart? TrackCommandOutputStart()
    {
        ThrowIfDisposed();
        if (ActiveScreen() != 0) return null;
        var point = new Native.Point { Tag = 0, Value = new Native.PointValue { X = 0, Y = (uint)ReadActiveCursorRow() } };
        ThrowIfFailed(ScrollNative.ghostty_terminal_grid_ref_track(_handle, point, out var tracked), "Could not track command output.");
        return new CommandOutputStart(new TrackedHandle(tracked));
    }

    /// <summary>
    /// The screen result of one fenced command: its native output region, formatted as plain unwrapped text with CR
    /// rewrites and cursor movement already applied, without the end fence. Lines end in CRLF like the transcript.
    /// Null means the screen no longer proves the region belongs to this command, and the caller keeps the transcript:
    /// the start expired from history, the screen switched, a clear removed a fence, or output arrived out of order.
    /// </summary>
    internal unsafe string? ReadFencedCommandOutput(CommandOutputStart start, string startFence, string endFence)
    {
        ThrowIfDisposed();
        if (ActiveScreen() != 0) return null;
        var result = ScrollNative.ghostty_tracked_grid_ref_snapshot(start.Handle, out var startRef);
        if (result == NativeResult.NoValue) return null;
        ThrowIfFailed(result, "Could not read command output start.");
        Native.PointValue origin = default;
        if (SearchNative.ghostty_terminal_point_from_grid_ref(_handle, &startRef, PointScreen, &origin) != 0 || origin.X != 0) return null;
        // The start fence must end on the row directly above the output, intact after any wrap or clear.
        var fenceRows = (uint)Math.Max(1, (startFence.Length + _columns - 1) / _columns);
        if (origin.Y < fenceRows || RowsText(origin.Y - fenceRows, origin.Y - 1) != startFence) return null;
        Native.Selection region = default;
        result = SemanticNative.ghostty_terminal_select_output(_handle, &startRef, &region);
        if (result == NativeResult.NoValue) return null;
        ThrowIfFailed(result, "Could not select command output.");
        if (ToScreen(region) is not { } span || span.StartY != origin.Y || span.StartX != 0) return null;
        var text = System.Text.Encoding.UTF8.GetString(FormatPlain(region));
        if (text.Contains(startFence, StringComparison.Ordinal)) return null;
        if (text == endFence) return "";
        if (!text.EndsWith("\n" + endFence, StringComparison.Ordinal)) return null;
        return text[..^endFence.Length].Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    private string? RowsText(uint first, uint last)
    {
        if (Native.ghostty_terminal_grid_ref(_handle, ScreenPoint(0, first), out var start) != 0 ||
            Native.ghostty_terminal_grid_ref(_handle, ScreenPoint((ushort)(_columns - 1), last), out var end) != 0) return null;
        return System.Text.Encoding.UTF8.GetString(FormatPlain(new Native.Selection { Start = start, End = end }));
    }

    /// <summary>Plain text with soft wraps joined and trailing whitespace trimmed, the clipboard form.</summary>
    private unsafe byte[] FormatPlain(Native.Selection selection)
    {
        ThrowIfFailed(Native.ghostty_formatter_terminal_new(IntPtr.Zero, out var ptr, _handle), "Could not create output formatter.");
        using var formatter = new FormatterHandle(ptr);
        byte unwrap = 1;
        ThrowIfFailed(ScrollNative.ghostty_formatter_set(formatter, 2, &unwrap), "Could not unwrap command output.");
        ThrowIfFailed(Native.ghostty_formatter_set(formatter, Native.FormatterOption.Selection, in selection), "Could not format command output.");
        return FormatUtf8(formatter, "Could not format command output.");
    }

    private static unsafe partial class SemanticNative
    {
        [LibraryImport("ghostty-vt")] internal static partial int ghostty_terminal_select_output(TerminalHandle terminal, Native.GridRef* reference, Native.Selection* selection);
    }
}
