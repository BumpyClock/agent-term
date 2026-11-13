using System.Runtime.InteropServices;
using System.Text;

namespace AgentTerm.Terminal;

/// <summary>Host effects. Callers serialize these methods with other terminal access.</summary>
public sealed partial class GhosttyTerminal
{
    private uint _cellWidthPx, _cellHeightPx;
    private HostNative.SizeCallback? _sizeQuery;
    private HostNative.VersionCallback? _versionQuery;
    private HostNative.AttributesCallback? _attributesQuery;
    private IntPtr _versionText;
    private TerminalHostCapabilities _hostCapabilities = new();

    public static string HostVersion => "AgentTerm " + (typeof(GhosttyTerminal).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    private unsafe void ConfigureHost(bool preserveImageStorage = false)
    {
        var versionBytes = Encoding.UTF8.GetBytes(HostVersion);
        _versionText = Marshal.AllocHGlobal(versionBytes.Length);
        Marshal.Copy(versionBytes, 0, _versionText, versionBytes.Length);
        _versionQuery = (_, _) => new Native.Utf8String { Data = _versionText, Length = (nuint)versionBytes.Length };
        _sizeQuery = (_, _, size) =>
        {
            *size = new HostNative.Size { Rows = _rows, Columns = _columns, CellWidth = _cellWidthPx, CellHeight = _cellHeightPx };
            return 1;
        };
        _attributesQuery = (_, _, attrs) =>
        {
            *attrs = default;
            attrs->Conformance = 62;
            attrs->Features[0] = 22; // ANSI color, supported by the host renderer.
            attrs->FeatureCount = 1;
            if (_hostCapabilities.Clipboard) attrs->Features[attrs->FeatureCount++] = 52;
            attrs->DeviceType = 1;
            return 1;
        };
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 5, Marshal.GetFunctionPointerForDelegate(_versionQuery)), "Could not set host identity.");
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 7, Marshal.GetFunctionPointerForDelegate(_sizeQuery)), "Could not set host geometry query.");
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 9, Marshal.GetFunctionPointerForDelegate(_attributesQuery)), "Could not set host capabilities.");
        if (!preserveImageStorage) SetHostCapabilities(_hostCapabilities);
        fixed (byte* name = "xterm-256color"u8)
        {
            var value = new Native.Utf8String { Data = (IntPtr)name, Length = 14 };
            ThrowIfFailed(Native.ghostty_terminal_set(_handle, 38, (IntPtr)(&value)), "Could not set terminfo identity.");
        }
    }

    /// <summary>Enable only after the corresponding host feature is operational. Kitty images have no DA1 code.</summary>
    public unsafe void SetHostCapabilities(TerminalHostCapabilities capabilities)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(capabilities);
        nuint imageLimit = capabilities.KittyImages ? capabilities.KittyImageStorageLimitBytes : 0;
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 16, (IntPtr)(&imageLimit)), "Could not configure host image support.");
        _hostCapabilities = capabilities;
    }

    /// <summary>ConPTY owns redraw and history movement during resize.</summary>
    public unsafe void ConfigureConPtyHost()
    {
        ThrowIfDisposed();
        byte pull = 0;
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 41, (IntPtr)(&pull)), "Could not configure ConPTY resize history.");
    }

    private void DisposeHost() { Marshal.FreeHGlobal(_versionText); _versionText = IntPtr.Zero; }

    private static unsafe partial class HostNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Size { internal ushort Rows, Columns; internal uint CellWidth, CellHeight; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Attributes
        {
            internal ushort Conformance;
            internal fixed ushort Features[64];
            internal nuint FeatureCount;
            internal ushort DeviceType, Firmware, Cartridge;
            internal uint UnitId;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate byte SizeCallback(IntPtr terminal, IntPtr userdata, Size* size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate Native.Utf8String VersionCallback(IntPtr terminal, IntPtr userdata);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate byte AttributesCallback(IntPtr terminal, IntPtr userdata, Attributes* attributes);
    }
}

public sealed record TerminalHostCapabilities(bool Clipboard = false, bool KittyImages = false, uint KittyImageStorageLimitBytes = TerminalImages.DefaultStorageLimit);
