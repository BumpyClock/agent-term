using System.Runtime.InteropServices;

namespace AgentTerm.Terminal;

public sealed partial class GhosttyTerminal
{
    private TerminalTheme? _theme;
    private ThemeNative.ColorSchemeCallback? _colorScheme;

    public void SetLightTheme(bool light) => SetTheme(TerminalThemes.Resolve("", light));

    /// <summary>Changes host defaults without replacing application colors, content, or history.</summary>
    public unsafe void SetTheme(TerminalTheme theme)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(theme);
        if (ReferenceEquals(_theme, theme)) return;

        if (_colorScheme is null)
        {
            _colorScheme = (_, _, scheme) =>
            {
                *scheme = _theme?.IsLight == true ? 0 : 1;
                return 1;
            };
            ThrowIfFailed(Native.ghostty_terminal_set(_handle, 8, Marshal.GetFunctionPointerForDelegate(_colorScheme)),
                "Could not register terminal color scheme queries.");
        }

        Rgb foreground = ToRgb(theme.Foreground), background = ToRgb(theme.Background), cursor = ToRgb(theme.Cursor);
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 12, (IntPtr)(&foreground)), "Could not set terminal foreground.");
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 13, (IntPtr)(&background)), "Could not set terminal background.");
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 14, (IntPtr)(&cursor)), "Could not set terminal cursor color.");

        Rgb* palette = stackalloc Rgb[256];
        ThemeNative.ghostty_color_palette_default(palette);
        for (var index = 0; index < 16; index++) palette[index] = ToRgb(theme.Ansi[index]);
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 15, (IntPtr)palette), "Could not set terminal palette.");
        _theme = theme;
        // A host theme switch must be visible even while an application holds synchronized output.
        _presentationDirty = true;
        _viewRevision++;
    }

    private static Rgb ToRgb(TerminalColor color) => new() { R = color.R, G = color.G, B = color.B };

    private static unsafe class ThemeNative
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate byte ColorSchemeCallback(IntPtr terminal, IntPtr userdata, int* scheme);

        [DllImport("ghostty-vt", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern void ghostty_color_palette_default(Rgb* palette);
    }
}
