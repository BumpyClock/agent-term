using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Microsoft.Win32.SafeHandles;

namespace AgentTerm.Terminal;

/// <summary>
/// Managed lifetime wrapper around libghostty-vt state, render snapshots, and input encoding.
/// The terminal is a state machine. It does not create a shell, PTY, or window.
/// Callers must serialize all access to an instance, including disposal.
/// </summary>
public sealed partial class GhosttyTerminal : IDisposable
{
    private readonly TerminalHandle _handle;
    private ushort _columns;
    private ushort _rows;
    private bool _disposed;
    private readonly Native.WritePtyCallback _writePty;
    private readonly List<byte[]> _responses = [];
    private ExceptionDispatchInfo? _callbackError;

    public GhosttyTerminal(ushort columns = 80, ushort rows = 24)
    {
        if (columns == 0)
            throw new ArgumentOutOfRangeException(nameof(columns), "Terminal width must be greater than zero.");
        if (rows == 0)
            throw new ArgumentOutOfRangeException(nameof(rows), "Terminal height must be greater than zero.");

        var result = Native.ghostty_terminal_new(IntPtr.Zero, out var handle, columns, rows);
        ThrowIfFailed(result, "Could not create the libghostty-vt terminal.");
        _handle = new TerminalHandle(handle);
        _columns = columns;
        _rows = rows;
        _writePty = (_, _, data, length) =>
        {
            // Never unwind managed exceptions through Ghostty's C stack.
            try
            {
                var response = new byte[checked((int)length)];
                Marshal.Copy(data, response, 0, response.Length);
                _responses.Add(response);
            }
            catch (Exception exception) { _callbackError = ExceptionDispatchInfo.Capture(exception); }
        };
        try
        {
            ConfigureScrollback();
            ThrowIfFailed(Native.ghostty_terminal_set(_handle, 2, Marshal.GetFunctionPointerForDelegate(_writePty)),
                "Could not register terminal replies.");
        }
        catch { _handle.Dispose(); throw; }
    }

    /// <summary>Feeds raw VT bytes into the terminal state machine.</summary>
    public unsafe void Write(ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        if (data.IsEmpty)
            return;

        fixed (byte* bytes = data)
        {
            Native.ghostty_terminal_vt_write(_handle, bytes, (nuint)data.Length);
        }
        CheckSelectionScreen();
        RestoreSelectedViewport();
        GC.KeepAlive(_writePty);
        _callbackError?.Throw();
    }

    /// <summary>Reads the last OSC 7 report. The returned string owns its memory.</summary>
    public unsafe string ReadWorkingDirectory()
    {
        ThrowIfDisposed();
        Native.Utf8String value = default;
        ThrowIfFailed(RenderNative.ghostty_terminal_get(_handle, 13, &value), "Could not read terminal directory.");
        return value.Length == 0 ? string.Empty : Marshal.PtrToStringUTF8(value.Data, checked((int)value.Length))!;
    }

    /// <summary>Drains terminal query replies for delivery to the PTY input. Call after each Write.</summary>
    public byte[][] TakeResponses()
    {
        ThrowIfDisposed();
        var responses = _responses.ToArray();
        _responses.Clear();
        return responses;
    }

    /// <summary>Formats the active grid as plain text, excluding scrollback and trimming trailing whitespace.</summary>
    public string ReadText()
    {
        ThrowIfDisposed();
        var result = Native.ghostty_formatter_terminal_new(IntPtr.Zero, out var formatter, _handle);
        ThrowIfFailed(result, "Could not create a libghostty-vt formatter.");

        try
        {
            using var formatterHandle = new FormatterHandle(formatter);

            // The formatter's default includes scrollback. Select only the active grid.
            var selection = new Native.Selection();
            result = Native.ghostty_terminal_grid_ref(_handle, new Native.Point(), out selection.Start);
            ThrowIfFailed(result, "Could not find the first active terminal cell.");
            var end = new Native.Point
            {
                Value = new Native.PointValue { X = (ushort)(_columns - 1), Y = (uint)(_rows - 1) }
            };
            result = Native.ghostty_terminal_grid_ref(_handle, end, out selection.End);
            ThrowIfFailed(result, "Could not find the last active terminal cell.");
            result = Native.ghostty_formatter_set(formatterHandle, Native.FormatterOption.Selection, in selection);
            ThrowIfFailed(result, "Could not select the active terminal grid.");

            result = Native.ghostty_formatter_format_alloc(formatterHandle, IntPtr.Zero, out var output, out var length);
            ThrowIfFailed(result, "Could not format the libghostty-vt terminal.");
            try
            {
                if (length == 0)
                    return string.Empty;
                if (length > int.MaxValue)
                    throw new InvalidOperationException("Formatted terminal output is too large for a managed string.");

                return Marshal.PtrToStringUTF8(output, (int)length)!;
            }
            finally
            {
                Native.ghostty_free(IntPtr.Zero, output, length);
            }
        }
        finally
        {
            // The formatter borrows the terminal until formatter disposal finishes.
            GC.KeepAlive(_handle);
        }
    }

    /// <summary>Resizes the terminal grid. Cell pixel dimensions are optional for VT parsing.</summary>
    public void Resize(ushort columns, ushort rows)
    {
        ThrowIfDisposed();
        if (columns == 0)
            throw new ArgumentOutOfRangeException(nameof(columns), "Terminal width must be greater than zero.");
        if (rows == 0)
            throw new ArgumentOutOfRangeException(nameof(rows), "Terminal height must be greater than zero.");

        var result = Native.ghostty_terminal_resize(_handle, columns, rows, 0, 0);
        ThrowIfFailed(result, "Could not resize the libghostty-vt terminal.");
        _columns = columns;
        _rows = rows;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DisposeSearch();
        DisposeSelection();
        DisposeInput();
        DisposeRender();
        _handle.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void ThrowIfFailed(int result, string message)
    {
        if (result != 0)
            throw new InvalidOperationException($"{message} (libghostty-vt result {result}).");
    }

    private sealed class TerminalHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public TerminalHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);
        protected override bool ReleaseHandle()
        {
            Native.ghostty_terminal_free(handle);
            return true;
        }
    }

    private sealed class FormatterHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public FormatterHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);
        protected override bool ReleaseHandle()
        {
            Native.ghostty_formatter_free(handle);
            return true;
        }
    }

    private static class Native
    {
        private const string Library = "ghostty-vt";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void WritePtyCallback(IntPtr terminal, IntPtr userdata, IntPtr data, nuint length);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_terminal_set(TerminalHandle terminal, int option, IntPtr value);

        internal enum FormatterOption { Selection = 4 }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Utf8String
        {
            internal IntPtr Data;
            internal nuint Length;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            internal int Tag; // GHOSTTY_POINT_TAG_ACTIVE = 0.
            internal PointValue Value;
        }

        [StructLayout(LayoutKind.Explicit, Size = 16)]
        internal struct PointValue
        {
            [FieldOffset(0)] private ulong _alignment;
            [FieldOffset(0)] internal ushort X;
            [FieldOffset(4)] internal uint Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct GridRef
        {
            internal IntPtr Node;
            internal ushort X;
            internal ushort Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Selection
        {
            internal GridRef Start;
            internal GridRef End;
            internal byte Rectangle;
        }

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_terminal_new(IntPtr allocator, out IntPtr terminal, ushort columns, ushort rows);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern void ghostty_terminal_free(IntPtr terminal);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern unsafe void ghostty_terminal_vt_write(TerminalHandle terminal, byte* data, nuint length);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_terminal_resize(TerminalHandle terminal, ushort columns, ushort rows, uint cellWidthPx, uint cellHeightPx);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_formatter_terminal_new(IntPtr allocator, out IntPtr formatter, TerminalHandle terminal);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_terminal_grid_ref(TerminalHandle terminal, Point point, out GridRef gridRef);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_formatter_set(FormatterHandle formatter, FormatterOption option, in Selection value);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_formatter_format_alloc(FormatterHandle formatter, IntPtr allocator, out IntPtr output, out nuint length);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern void ghostty_formatter_free(IntPtr formatter);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern void ghostty_free(IntPtr allocator, IntPtr output, nuint length);
    }
}
