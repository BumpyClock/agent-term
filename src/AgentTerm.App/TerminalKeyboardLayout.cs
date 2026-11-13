using System.Runtime.InteropServices;
using System.Text;

namespace AgentTerm_App;

internal static class TerminalKeyboardLayout
{
    internal static uint? UnshiftedCodepoint(uint virtualKey, uint scanCode)
    {
        // ToUnicodeEx flag 4 reads the layout without changing its dead-key state.
        // No keyboard layout, lock state, or user setting is changed.
        var state = new byte[256];
        var buffer = new StringBuilder(8);
        int count = ToUnicodeEx(virtualKey, scanCode, state, buffer, buffer.Capacity, 4, GetKeyboardLayout(0));
        return count > 0 && Rune.TryGetRuneAt(buffer.ToString(), 0, out var rune) && rune.Utf16SequenceLength == count
            ? (uint)rune.Value : null;
    }

    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] state,
        StringBuilder buffer, int capacity, uint flags, nint layout);
}
