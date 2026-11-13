using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Microsoft.Win32.SafeHandles;

namespace AgentTerm.Terminal;

/// <summary>
/// Managed lifetime wrapper around libghostty-vt state, render snapshots, and input encoding.
/// The terminal is a state machine. It does not create a shell, PTY, or window.
/// Callers must serialize all access to an instance, including disposal. The one exception is the second phase of a
/// split frame capture (BeginFrame, then CompleteFrame), which may overlap any call that leaves the render state alone.
/// Native calls retain their handle for the whole call, so an unreachable wrapper is never finalized mid-operation.
/// </summary>
public sealed partial class GhosttyTerminal : IDisposable
{
    private readonly TerminalHandle _handle;
    private ushort _columns;
    private ushort _rows;
    private bool _disposed;
    internal long ContentVersion { get; private set; }
    private readonly Native.WritePtyCallback _writePty;
    private readonly List<byte[]> _responses = [];
    private ExceptionDispatchInfo? _callbackError;

    public GhosttyTerminal(ushort columns = 80, ushort rows = 24) : this(columns, rows, IntPtr.Zero) { }

    private GhosttyTerminal(ushort columns, ushort rows, IntPtr restored)
    {
        if (columns == 0)
            throw new ArgumentOutOfRangeException(nameof(columns), "Terminal width must be greater than zero.");
        if (rows == 0)
            throw new ArgumentOutOfRangeException(nameof(rows), "Terminal height must be greater than zero.");

        var handle = restored;
        var result = restored == IntPtr.Zero ? Native.ghostty_terminal_new(IntPtr.Zero, out handle, columns, rows) : 0;
        ThrowIfFailed(result, "Could not create the libghostty-vt terminal.");
        _handle = new TerminalHandle(handle);
        _columns = columns;
        _rows = rows;
        _writePty = (_, _, data, length) =>
        {
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
            if (restored == IntPtr.Zero) ConfigureScrollback();
            ConfigureHost(restored != IntPtr.Zero);
            ConfigureSnapshot();
            ConfigureRenderHold();
            ThrowIfFailed(Native.ghostty_terminal_set(_handle, 2, Marshal.GetFunctionPointerForDelegate(_writePty)),
                "Could not register terminal replies.");
        }
        catch { DisposeHost(); _handle.Dispose(); throw; }
    }

    /// <summary>Feeds raw VT bytes into the terminal state machine.</summary>
    public unsafe void Write(ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        if (data.IsEmpty)
            return;

        ContentVersion++;

        fixed (byte* bytes = data)
        {
            try { Native.ghostty_terminal_vt_write(_handle, bytes, (nuint)data.Length); }
            finally { ContentVersion++; }
        }
        SettleAfterWrite();
        GC.KeepAlive(_writePty);
        GC.KeepAlive(_renderHold);
        if (_callbackError is { } error)
        {
            _callbackError = null;
            error.Throw();
        }
    }

    /// <summary>Viewport and selection upkeep after parsed output. Also applied at a render-hold boundary inside a write.</summary>
    private void SettleAfterWrite()
    {
        _presentationDirty = true;
        CheckSelectionScreen();
        RestoreSelectedViewport();
    }

    /// <summary>Reads the last OSC 7 report. The returned string owns its memory.</summary>
    public unsafe string ReadWorkingDirectory()
    {
        ThrowIfDisposed();
        Native.Utf8String value = default;
        ThrowIfFailed(RenderNative.ghostty_terminal_get(_handle, (int)TerminalData.WorkingDirectory, &value), "Could not read terminal directory.");
        return value.Length == 0 ? string.Empty : Marshal.PtrToStringUTF8(value.Data, checked((int)value.Length))!;
    }

    /// <summary>Drains terminal query replies for delivery to the PTY input. Call after Write and host mutations such as Resize and SetTheme.</summary>
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
            GC.KeepAlive(_handle);
        }
    }

    /// <summary>Resizes the terminal grid. Cell dimensions are device pixels. Zero means unknown.</summary>
    public void Resize(ushort columns, ushort rows, uint cellWidthPx = 0, uint cellHeightPx = 0)
    {
        ThrowIfDisposed();
        if (columns == 0)
            throw new ArgumentOutOfRangeException(nameof(columns), "Terminal width must be greater than zero.");
        if (rows == 0)
            throw new ArgumentOutOfRangeException(nameof(rows), "Terminal height must be greater than zero.");

        var previousGeometry = (_columns, _rows, _cellWidthPx, _cellHeightPx);
        ContentVersion++;
        _columns = columns;
        _rows = rows;
        _cellWidthPx = cellWidthPx;
        _cellHeightPx = cellHeightPx;
        var result = Native.ghostty_terminal_resize(_handle, columns, rows, cellWidthPx, cellHeightPx);
        if (result != 0) (_columns, _rows, _cellWidthPx, _cellHeightPx) = previousGeometry;
        ThrowIfFailed(result, "Could not resize the libghostty-vt terminal.");
        _viewRevision++;
        _presentationDirty = true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DisposeHost();
        DisposeSearch();
        DisposeAnchors();
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

    private static partial class Native
    {
        private const string Library = "ghostty-vt";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void WritePtyCallback(IntPtr terminal, IntPtr userdata, IntPtr data, nuint length);

        [LibraryImport(Library)]
        internal static partial int ghostty_terminal_set(TerminalHandle terminal, int option, IntPtr value);

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
            internal int Tag;
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

        [LibraryImport(Library)]
        internal static partial int ghostty_terminal_new(IntPtr allocator, out IntPtr terminal, ushort columns, ushort rows);

        [LibraryImport(Library)]
        internal static partial void ghostty_terminal_free(IntPtr terminal);

        [LibraryImport(Library)]
        internal static unsafe partial void ghostty_terminal_vt_write(TerminalHandle terminal, byte* data, nuint length);

        [LibraryImport(Library)]
        internal static partial int ghostty_terminal_resize(TerminalHandle terminal, ushort columns, ushort rows, uint cellWidthPx, uint cellHeightPx);

        [LibraryImport(Library)]
        internal static partial int ghostty_formatter_terminal_new(IntPtr allocator, out IntPtr formatter, TerminalHandle terminal);

        [LibraryImport(Library)]
        internal static partial int ghostty_terminal_grid_ref(TerminalHandle terminal, Point point, out GridRef gridRef);

        [LibraryImport(Library)]
        internal static partial int ghostty_formatter_set(FormatterHandle formatter, FormatterOption option, in Selection value);

        [LibraryImport(Library)]
        internal static partial int ghostty_formatter_format_alloc(FormatterHandle formatter, IntPtr allocator, out IntPtr output, out nuint length);

        [LibraryImport(Library)]
        internal static partial void ghostty_formatter_free(IntPtr formatter);

        [LibraryImport(Library)]
        internal static partial void ghostty_free(IntPtr allocator, IntPtr output, nuint length);
    }
}
