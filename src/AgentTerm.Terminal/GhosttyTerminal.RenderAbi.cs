using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

[assembly: InternalsVisibleTo("AgentTerm.Terminal.Smoke")]
[assembly: InternalsVisibleTo("AgentTerm.ConPty.Smoke")]

namespace AgentTerm.Terminal;

// Render-state ABI for libghostty-vt. Keys mirror render.h, terminal.h, screen.h and types.h by name.
// RenderLayoutMismatch compares the struct mirrors with the linked library's ghostty_type_json manifest. The terminal
// smoke tests enforce it for the pinned build; parsing the 75 KB manifest costs about 10 ms, too much for first paint.
public sealed partial class GhosttyTerminal
{
    /// <summary>The libghostty-vt version string reported by the loaded native library.</summary>
    public static string NativeVersion => Marshal.PtrToStringUTF8(RenderNative.ghostty_version()) ?? string.Empty;

    private static class NativeResult
    {
        public const int Success = 0, OutOfMemory = -1, InvalidValue = -2, OutOfSpace = -3, NoValue = -4;
    }

    private enum RenderStateData
    {
        Columns = 1, Rows = 2, Dirty = 3, RowIterator = 4, ColorBackground = 5, ColorForeground = 6,
        ColorCursor = 7, ColorCursorHasValue = 8, Palette = 9, Cursor = 18,
    }
    private enum RenderDirty { Clean = 0, Partial = 1, Full = 2 }
    private enum RenderRowData { Dirty = 1, Cells = 3, Selection = 4, ViewportY = 6 }
    private enum RenderCellData { Raw = 1, Style = 2, BackgroundColor = 5, ForegroundColor = 6, GraphemesUtf8 = 9 }
    private enum CellData { Codepoint = 1, ContentTag = 2, Wide = 3, StyleId = 6, Hyperlink = 7 }
    private enum CellWide { Narrow = 0, Wide = 1, SpacerTail = 2, SpacerHead = 3 }
    private enum CursorVisualStyle { Bar = 0, Block = 1, Underline = 2, BlockHollow = 3 }
    private enum TerminalOption { Mode = 35, RenderHold = 42 }
    private enum TerminalData { CursorY = 4, ActiveScreen = 6, Scrollbar = 9, Title = 12, WorkingDirectory = 13, Selection = 31, Mode = 37 }
    private const ushort SynchronizedOutputMode = 2026;
    private const ushort ReverseColorsMode = 5;

    [Flags]
    private enum StyleFlags : ushort
    {
        Bold = 1 << 0, Italic = 1 << 1, Faint = 1 << 2, Blink = 1 << 3, Inverse = 1 << 4, Invisible = 1 << 5,
        Strikethrough = 1 << 6, Overline = 1 << 7, UnderlineMask = 0x7 << 8,
    }

    [StructLayout(LayoutKind.Sequential)] private struct Mode { public ushort Value; public byte Enabled; }
    [StructLayout(LayoutKind.Sequential)] private struct Rgb { public byte R, G, B; public readonly TerminalColor ToColor() => new(R, G, B); }
    [StructLayout(LayoutKind.Explicit, Size = 16)] private struct Style
    {
        [FieldOffset(8)] public byte UnderlineTag;
        [FieldOffset(9)] public byte UnderlineR;
        [FieldOffset(10)] public byte UnderlineG;
        [FieldOffset(11)] public byte UnderlineB;
        [FieldOffset(12)] public ushort Flags;
        public readonly bool Has(StyleFlags flag) => ((StyleFlags)Flags & flag) != 0;
    }
    [StructLayout(LayoutKind.Sequential)] private unsafe struct ByteBuffer { public byte* Pointer; public nuint Capacity, Length; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RenderCursor
    {
        public nuint Size;
        public byte ViewportHasValue;
        public ushort ViewportX, ViewportY;
        public byte WideTail, Visible, Blinking, PasswordInput;
        public CursorVisualStyle VisualStyle;
        public static RenderCursor Sized() => new() { Size = (nuint)Unsafe.SizeOf<RenderCursor>() };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RowSelection
    {
        public nuint Size;
        public ushort StartX, EndX;
        public static RowSelection Sized() => new() { Size = (nuint)Unsafe.SizeOf<RowSelection>() };
    }

    // Each entry names a managed mirror, its C type, and the fields whose offsets the managed code depends on.
    private static readonly (string Native, Type Managed, (string Native, string Managed)[] Fields)[] CheckedLayouts =
    [
        ("GhosttyColorRgb", typeof(Rgb), [("r", nameof(Rgb.R)), ("g", nameof(Rgb.G)), ("b", nameof(Rgb.B))]),
        ("GhosttyStyle", typeof(Style), [("flags", nameof(Style.Flags))]),
        ("GhosttyBuffer", typeof(ByteBuffer), [("ptr", nameof(ByteBuffer.Pointer)), ("cap", nameof(ByteBuffer.Capacity)), ("len", nameof(ByteBuffer.Length))]),
        ("GhosttyTerminalModeConfig", typeof(Mode), [("mode", nameof(Mode.Value)), ("value", nameof(Mode.Enabled))]),
        ("GhosttyRenderStateRowSelection", typeof(RowSelection), [("size", nameof(RowSelection.Size)), ("start_x", nameof(RowSelection.StartX)), ("end_x", nameof(RowSelection.EndX))]),
        ("GhosttyRenderStateCursor", typeof(RenderCursor), [
            ("size", nameof(RenderCursor.Size)), ("viewport_has_value", nameof(RenderCursor.ViewportHasValue)),
            ("viewport_x", nameof(RenderCursor.ViewportX)), ("viewport_y", nameof(RenderCursor.ViewportY)),
            ("wide_tail", nameof(RenderCursor.WideTail)), ("visible", nameof(RenderCursor.Visible)),
            ("blinking", nameof(RenderCursor.Blinking)), ("password_input", nameof(RenderCursor.PasswordInput)),
            ("visual_style", nameof(RenderCursor.VisualStyle))]),
    ];

    private static readonly Lazy<string?> LayoutMismatch = new(FindLayoutMismatch);

    /// <summary>Null when every managed render mirror matches the linked library, otherwise the first difference.</summary>
    internal static string? RenderLayoutMismatch => LayoutMismatch.Value;

    private static string? FindLayoutMismatch()
    {
        using var manifest = JsonDocument.Parse(Marshal.PtrToStringUTF8(RenderNative.ghostty_type_json()) ?? "{}");
        var root = manifest.RootElement;
        if (!root.TryGetProperty("abi", out var abi) || abi.GetProperty("pointer_size").GetInt32() != IntPtr.Size)
            return "libghostty-vt pointer size does not match this process.";
        var types = root.GetProperty("types");
        foreach (var (nativeName, managed, fields) in CheckedLayouts)
        {
            if (!types.TryGetProperty(nativeName, out var type)) return $"{nativeName} is missing from the libghostty-vt manifest.";
            var size = Marshal.SizeOf(managed);
            if (type.GetProperty("size").GetInt32() != size) return $"{nativeName} is {type.GetProperty("size").GetInt32()} bytes; managed mirror is {size}.";
            var nativeFields = type.GetProperty("fields");
            foreach (var (nativeField, managedField) in fields)
            {
                if (!nativeFields.TryGetProperty(nativeField, out var field)) return $"{nativeName}.{nativeField} is missing from the libghostty-vt manifest.";
                var offset = (int)Marshal.OffsetOf(managed, managedField);
                if (field.GetProperty("offset").GetInt32() != offset)
                    return $"{nativeName}.{nativeField} is at offset {field.GetProperty("offset").GetInt32()}; managed mirror uses {offset}.";
            }
        }
        return null;
    }

    private sealed class RenderHandle : SafeHandleZeroOrMinusOneIsInvalid { public RenderHandle(IntPtr value) : base(true) => SetHandle(value); protected override bool ReleaseHandle() { RenderNative.ghostty_render_state_free(handle); return true; } }
    private sealed class RenderRowsHandle : SafeHandleZeroOrMinusOneIsInvalid { public RenderRowsHandle(IntPtr value) : base(true) => SetHandle(value); protected override bool ReleaseHandle() { RenderNative.ghostty_render_state_row_iterator_free(handle); return true; } }
    private sealed class RenderCellsHandle : SafeHandleZeroOrMinusOneIsInvalid { public RenderCellsHandle(IntPtr value) : base(true) => SetHandle(value); protected override bool ReleaseHandle() { RenderNative.ghostty_render_state_row_cells_free(handle); return true; } }

    /// <summary>
    /// Keeps a SafeHandle's native object alive across a run of raw-pointer calls. Explicit disposal during the lease
    /// defers the native free until the lease ends, and an unreachable owner cannot be finalized mid-operation.
    /// </summary>
    private readonly ref struct HandleLease
    {
        private readonly SafeHandle? _handle;
        public readonly IntPtr Raw;
        public HandleLease(SafeHandle? handle)
        {
            if (handle is null) return;
            var added = false;
            handle.DangerousAddRef(ref added);
            _handle = handle;
            Raw = handle.DangerousGetHandle();
        }
        public void Dispose() => _handle?.DangerousRelease();
    }

    // Terminal calls take the SafeHandle so marshalling retains the terminal for each call. Render-state calls use raw
    // pointers held under a HandleLease for the whole capture. SuppressGCTransition stays only on calls that do a
    // fixed, tiny amount of work: decoding a packed cell value and advancing an iterator by one position.
    private static unsafe partial class RenderNative
    {
        private const string Lib = "ghostty-vt";
        [LibraryImport(Lib)] internal static partial IntPtr ghostty_version();
        [LibraryImport(Lib)] internal static partial IntPtr ghostty_type_json();
        [LibraryImport(Lib)] internal static partial int ghostty_terminal_get(TerminalHandle terminal, int key, void* value);
        [LibraryImport(Lib)] internal static partial int ghostty_render_state_new(IntPtr allocator, out IntPtr state);
        [LibraryImport(Lib)] internal static partial void ghostty_render_state_free(IntPtr state);
        [LibraryImport(Lib)] internal static partial int ghostty_render_state_update(IntPtr state, TerminalHandle terminal);
        [LibraryImport(Lib)] internal static partial int ghostty_render_state_begin_update(IntPtr state, TerminalHandle terminal);
        [LibraryImport(Lib)] internal static partial int ghostty_render_state_end_update(IntPtr state);
        [LibraryImport(Lib)] internal static partial int ghostty_render_state_get(IntPtr state, RenderStateData key, void* value);
        [LibraryImport(Lib)] internal static partial int ghostty_render_state_get_multi(IntPtr state, nuint count, RenderStateData* keys, void** values, nuint* written);
        [LibraryImport(Lib)] internal static partial int ghostty_render_state_clean(IntPtr state);
        [LibraryImport(Lib)] internal static partial int ghostty_render_state_row_iterator_new(IntPtr allocator, out IntPtr rows);
        [LibraryImport(Lib)] internal static partial void ghostty_render_state_row_iterator_free(IntPtr rows);
        [LibraryImport(Lib), SuppressGCTransition] internal static partial byte ghostty_render_state_row_iterator_next(IntPtr rows);
        [LibraryImport(Lib)] internal static partial byte ghostty_render_state_row_iterator_next_dirty(IntPtr rows, ushort* y);
        [LibraryImport(Lib)] internal static partial int ghostty_render_state_row_get(IntPtr rows, RenderRowData key, void* value);
        [LibraryImport(Lib)] internal static partial int ghostty_render_state_row_cells_new(IntPtr allocator, out IntPtr cells);
        [LibraryImport(Lib)] internal static partial void ghostty_render_state_row_cells_free(IntPtr cells);
        [LibraryImport(Lib), SuppressGCTransition] internal static partial byte ghostty_render_state_row_cells_next(IntPtr cells);
        [LibraryImport(Lib)] internal static partial int ghostty_render_state_row_cells_get(IntPtr cells, RenderCellData key, void* value);
        [LibraryImport(Lib)] internal static partial int ghostty_render_state_row_cells_get_multi(IntPtr cells, nuint count, RenderCellData* keys, void** values, nuint* written);
        [LibraryImport(Lib), SuppressGCTransition] internal static partial int ghostty_cell_get(ulong cell, CellData key, void* value);
    }
}
