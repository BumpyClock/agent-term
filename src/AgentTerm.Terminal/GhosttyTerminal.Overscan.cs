using System.Runtime.InteropServices;

namespace AgentTerm.Terminal;

public sealed partial class GhosttyTerminal
{
    private bool _overscanEnabled;

    /// <summary>Requests one owned row on each available side of future frames.</summary>
    public void SetOverscanEnabled(bool enabled)
    {
        ThrowIfDisposed();
        if (_overscanEnabled == enabled) return;
        _overscanEnabled = enabled;
        _viewRevision++;
    }

    private unsafe void ConfigureOverscan(bool enabled)
    {
        OverscanRequest request = enabled ? new() { Above = 1, Below = 1 } : default;
        ThrowIfFailed(OverscanNative.ghostty_render_state_set(_renderState, 2, &request), "Could not configure render overscan.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OverscanRequest { public ushort Above, Below; }

    private static unsafe partial class OverscanNative
    {
        [LibraryImport("ghostty-vt")]
        internal static partial int ghostty_render_state_set(IntPtr state, int option, void* value);
    }
}
