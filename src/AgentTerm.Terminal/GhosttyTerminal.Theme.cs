using System.Runtime.InteropServices;

namespace AgentTerm.Terminal;

public sealed partial class GhosttyTerminal
{
    private bool? _lightTheme;
    private ThemeNative.ColorSchemeCallback? _colorScheme;

    /// <summary>Changes host defaults without replacing application colors, content, or history.</summary>
    public unsafe void SetLightTheme(bool light)
    {
        ThrowIfDisposed();
        if (_lightTheme == light) return;

        if (_colorScheme is null)
        {
            _colorScheme = (_, _, scheme) =>
            {
                *scheme = _lightTheme == true ? 0 : 1;
                return 1;
            };
            ThrowIfFailed(Native.ghostty_terminal_set(_handle, 8, Marshal.GetFunctionPointerForDelegate(_colorScheme)),
                "Could not register terminal color scheme queries.");
        }

        Rgb foreground = light ? new() { R = 0x24, G = 0x24, B = 0x24 } : new() { R = 255, G = 255, B = 255 };
        Rgb background = light ? new() { R = 0xfa, G = 0xfa, B = 0xfa } : default;
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 12, (IntPtr)(&foreground)), "Could not set terminal foreground.");
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 13, (IntPtr)(&background)), "Could not set terminal background.");
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 14, (IntPtr)(&foreground)), "Could not set terminal cursor color.");

        if (light)
        {
            // Keep Ghostty's extended 240 colors. Only ANSI colors need adapting for a light background.
            Rgb* palette = stackalloc Rgb[256];
            ThemeNative.ghostty_color_palette_default(palette);
            ReadOnlySpan<Rgb> ansi =
            [
                new() { R = 0x2e, G = 0x34, B = 0x36 }, new() { R = 0xa4, G = 0x00, B = 0x00 },
                new() { R = 0x3b, G = 0x65, B = 0x00 }, new() { R = 0x80, G = 0x60, B = 0x00 },
                new() { R = 0x20, G = 0x4a, B = 0x87 }, new() { R = 0x75, G = 0x50, B = 0x7b },
                new() { R = 0x00, G = 0x5c, B = 0x64 }, new() { R = 0x55, G = 0x57, B = 0x53 },
                new() { R = 0x66, G = 0x68, B = 0x66 }, new() { R = 0xc0, G = 0x00, B = 0x00 },
                new() { R = 0x46, G = 0x70, B = 0x00 }, new() { R = 0x8f, G = 0x68, B = 0x00 },
                new() { R = 0x34, G = 0x65, B = 0xa4 }, new() { R = 0x8d, G = 0x46, B = 0x84 },
                new() { R = 0x00, G = 0x78, B = 0x80 }, new() { R = 0x2e, G = 0x34, B = 0x36 },
            ];
            ansi.CopyTo(new Span<Rgb>(palette, 16));
            ThrowIfFailed(Native.ghostty_terminal_set(_handle, 15, (IntPtr)palette), "Could not set light terminal palette.");
        }
        else
        {
            ThrowIfFailed(Native.ghostty_terminal_set(_handle, 15, IntPtr.Zero), "Could not restore dark terminal palette.");
        }

        _lightTheme = light;
        // A host theme switch must be visible even while an application holds synchronized output.
        _viewRevision++;
    }

    private static unsafe class ThemeNative
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate byte ColorSchemeCallback(IntPtr terminal, IntPtr userdata, int* scheme);

        [DllImport("ghostty-vt", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern void ghostty_color_palette_default(Rgb* palette);
    }
}
