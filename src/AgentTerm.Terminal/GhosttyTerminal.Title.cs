using System.Runtime.InteropServices;

namespace AgentTerm.Terminal;

public sealed partial class GhosttyTerminal
{
    /// <summary>Reads the last OSC 0 or OSC 2 title. The returned string owns its memory.</summary>
    public unsafe string ReadTitle()
    {
        ThrowIfDisposed();
        Native.Utf8String value = default;
        ThrowIfFailed(RenderNative.ghostty_terminal_get(_handle, (int)TerminalData.Title, &value), "Could not read terminal title.");
        return value.Length == 0 ? string.Empty : Marshal.PtrToStringUTF8(value.Data, checked((int)value.Length))!;
    }
}
