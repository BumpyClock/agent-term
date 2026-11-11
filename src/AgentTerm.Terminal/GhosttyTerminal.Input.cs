using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentTerm.Terminal;

public sealed partial class GhosttyTerminal
{
    private KeyEncoderHandle? _keyEncoder;
    private KeyEventHandle? _keyEvent;
    private Dictionary<uint, uint>? _pressedLayoutCodepoints;

    /// <summary>Encodes a Windows virtual key or committed UTF-8 text for the active terminal mode.</summary>
    public unsafe byte[] EncodeKey(uint virtualKey, TerminalModifiers modifiers, TerminalKeyAction action,
        string? text = null)
    {
        ThrowIfDisposed();
        var nativeAction = action switch
        {
            TerminalKeyAction.Press => InputNative.KeyAction.Press,
            TerminalKeyAction.Repeat => InputNative.KeyAction.Repeat,
            TerminalKeyAction.Release => InputNative.KeyAction.Release,
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        var key = GetGhosttyKey(virtualKey);
        // The encoder expects text from the layout before Ctrl/Alt transforms.
        // Control characters are represented by their physical keys instead.
        if (action == TerminalKeyAction.Release || text.AsSpan().IndexOfAnyInRange('\0', '\u001f') >= 0 ||
            text?.Contains('\u007f') == true)
            text = null;
        if (key == InputNative.Key.Unidentified && string.IsNullOrEmpty(text))
            return [];

        bool shifted = (modifiers & TerminalModifiers.Shift) != 0;
        uint asciiCodepoint = GetAsciiKey(virtualKey);
        uint unshiftedCodepoint = asciiCodepoint;
        if (action == TerminalKeyAction.Release)
        {
            // Windows does not send text with key-up. Report the same layout codepoint
            // as the corresponding press when Kitty requests release events.
            if (_pressedLayoutCodepoints?.Remove(virtualKey, out var pressedCodepoint) == true)
                unshiftedCodepoint = pressedCodepoint;
        }
        else if (asciiCodepoint != 0 && !string.IsNullOrEmpty(text) &&
            Rune.TryGetRuneAt(text, 0, out var rune) && rune.Utf16SequenceLength == text.Length)
        {
            if (Rune.IsLetter(rune))
                unshiftedCodepoint = (uint)Rune.ToLowerInvariant(rune).Value;
            else if (!shifted)
                unshiftedCodepoint = (uint)rune.Value;

            if (unshiftedCodepoint != asciiCodepoint)
                (_pressedLayoutCodepoints ??= [])[virtualKey] = unshiftedCodepoint;
            else
                _pressedLayoutCodepoints?.Remove(virtualKey);
        }
        else if (action == TerminalKeyAction.Press)
        {
            _pressedLayoutCodepoints?.Remove(virtualKey);
        }

        if (string.IsNullOrEmpty(text) && asciiCodepoint != 0 && action != TerminalKeyAction.Release &&
            (modifiers & (TerminalModifiers.Control | TerminalModifiers.Alt)) != 0)
        {
            // Shortcut keydowns may arrive without a Windows character event.
            var ascii = (char)asciiCodepoint;
            text = (shifted ? GetShiftedAscii(ascii) : ascii).ToString();
        }

        if (_keyEncoder is null)
        {
            ThrowIfFailed(InputNative.ghostty_key_encoder_new(IntPtr.Zero, out var encoder),
                "Could not create a libghostty-vt key encoder.");
            _keyEncoder = new KeyEncoderHandle(encoder);
        }
        if (_keyEvent is null)
        {
            ThrowIfFailed(InputNative.ghostty_key_event_new(IntPtr.Zero, out var keyEvent),
                "Could not create a libghostty-vt key event.");
            _keyEvent = new KeyEventHandle(keyEvent);
        }

        ThrowIfFailed(InputNative.ghostty_key_encoder_set_from_terminal(_keyEncoder, _handle),
            "Could not synchronize libghostty-vt key modes.");
        const string eventError = "Could not set a libghostty-vt key event field.";
        ThrowIfFailed(InputNative.ghostty_key_event_set_action(_keyEvent, nativeAction), eventError);
        ThrowIfFailed(InputNative.ghostty_key_event_set_key(_keyEvent, key), eventError);
        // TerminalModifiers uses the corresponding GhosttyMods bit values.
        ThrowIfFailed(InputNative.ghostty_key_event_set_mods(_keyEvent, (ushort)modifiers), eventError);
        ThrowIfFailed(InputNative.ghostty_key_event_set_unshifted_codepoint(_keyEvent, unshiftedCodepoint), eventError);

        byte[] utf8 = string.IsNullOrEmpty(text) ? [] : Encoding.UTF8.GetBytes(text);
        fixed (byte* utf8Ptr = utf8)
        {
            ThrowIfFailed(InputNative.ghostty_key_event_set_utf8(_keyEvent,
                new InputNative.KeyText(utf8Ptr, (nuint)utf8.Length)), eventError);
            try
            {
                const string encodeError = "Could not encode a libghostty-vt key event.";
                Span<byte> buffer = stackalloc byte[128];
                int result;
                nuint written;
                fixed (byte* bufferPtr = buffer)
                    result = InputNative.ghostty_key_encoder_encode(_keyEncoder, _keyEvent, bufferPtr,
                        (nuint)buffer.Length, out written);
                if (result != InputNative.OutOfSpace)
                {
                    ThrowIfFailed(result, encodeError);
                    return buffer[..checked((int)written)].ToArray();
                }

                var output = new byte[checked((int)written)];
                fixed (byte* outputPtr = output)
                    ThrowIfFailed(InputNative.ghostty_key_encoder_encode(_keyEncoder, _keyEvent, outputPtr,
                        (nuint)output.Length, out _), encodeError);
                return output;
            }
            finally
            {
                // Ghostty borrows the UTF-8 bytes. Clear the event before unpinning.
                InputNative.ghostty_key_event_set_utf8(_keyEvent, default);
            }
        }
    }

    /// <summary>Encodes a paste, applying terminal framing and sanitizing control bytes.</summary>
    public unsafe byte[] EncodePaste(string text)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
            return [];

        bool bracketed = IsInputModeEnabled(InputNative.DecMode.BracketedPaste);
        var utf8 = Encoding.UTF8.GetBytes(text);
        const string encodeError = "Could not encode a libghostty-vt paste.";
        fixed (byte* utf8Ptr = utf8)
        {
            var result = InputNative.ghostty_paste_encode(utf8Ptr, (nuint)utf8.Length, bracketed,
                null, 0, out var needed);
            if (result != InputNative.OutOfSpace)
            {
                ThrowIfFailed(result, encodeError);
                return [];
            }

            var output = new byte[checked((int)needed)];
            fixed (byte* outputPtr = output)
                ThrowIfFailed(InputNative.ghostty_paste_encode(utf8Ptr, (nuint)utf8.Length, bracketed,
                    outputPtr, (nuint)output.Length, out _), encodeError);
            return output;
        }
    }

    /// <summary>Encodes a focus report when the application enables DEC mode 1004.</summary>
    public unsafe byte[] EncodeFocus(bool focused)
    {
        ThrowIfDisposed();
        if (!IsInputModeEnabled(InputNative.DecMode.FocusEvent))
            return [];

        var output = new byte[3]; // CSI I or CSI O.
        fixed (byte* outputPtr = output)
            ThrowIfFailed(InputNative.ghostty_focus_encode(focused ? 0 : 1, outputPtr,
                (nuint)output.Length, out _), "Could not encode a libghostty-vt focus report.");
        return output;
    }

    private bool IsInputModeEnabled(InputNative.DecMode mode)
    {
        var config = new InputNative.ModeConfig { Mode = mode };
        ThrowIfFailed(InputNative.ghostty_terminal_get(_handle, InputNative.TerminalDataMode, ref config),
            $"Could not read libghostty-vt mode {(ushort)mode}.");
        return config.Value != 0;
    }

    private void DisposeInput()
    {
        _mouseEvent?.Dispose();
        _mouseEncoder?.Dispose();
        _keyEvent?.Dispose();
        _keyEncoder?.Dispose();
    }

    private static InputNative.Key GetGhosttyKey(uint key) => key switch
    {
        >= 0x30 and <= 0x39 => InputNative.Key.Digit0 + (int)(key - 0x30),
        >= 0x41 and <= 0x5A => InputNative.Key.A + (int)(key - 0x41),
        >= 0x60 and <= 0x69 => InputNative.Key.Numpad0 + (int)(key - 0x60),
        >= 0x70 and <= 0x87 => InputNative.Key.F1 + (int)(key - 0x70),
        0x08 => InputNative.Key.Backspace,
        0x09 => InputNative.Key.Tab,
        0x0C => InputNative.Key.NumpadClear,
        0x0D => InputNative.Key.Enter,
        0x10 or 0xA0 => InputNative.Key.ShiftLeft,
        0x11 or 0xA2 => InputNative.Key.ControlLeft,
        0x12 or 0xA4 => InputNative.Key.AltLeft,
        0x13 => InputNative.Key.Pause,
        0x14 => InputNative.Key.CapsLock,
        0x1B => InputNative.Key.Escape,
        0x20 => InputNative.Key.Space,
        0x21 => InputNative.Key.PageUp,
        0x22 => InputNative.Key.PageDown,
        0x23 => InputNative.Key.End,
        0x24 => InputNative.Key.Home,
        0x25 => InputNative.Key.ArrowLeft,
        0x26 => InputNative.Key.ArrowUp,
        0x27 => InputNative.Key.ArrowRight,
        0x28 => InputNative.Key.ArrowDown,
        0x2C => InputNative.Key.PrintScreen,
        0x2D => InputNative.Key.Insert,
        0x2E => InputNative.Key.Delete,
        0x2F => InputNative.Key.Help,
        0x5B => InputNative.Key.MetaLeft,
        0x5C => InputNative.Key.MetaRight,
        0x5D => InputNative.Key.ContextMenu,
        0x6A => InputNative.Key.NumpadMultiply,
        0x6B => InputNative.Key.NumpadAdd,
        0x6C => InputNative.Key.NumpadSeparator,
        0x6D => InputNative.Key.NumpadSubtract,
        0x6E => InputNative.Key.NumpadDecimal,
        0x6F => InputNative.Key.NumpadDivide,
        0x90 => InputNative.Key.NumLock,
        0x91 => InputNative.Key.ScrollLock,
        0xA1 => InputNative.Key.ShiftRight,
        0xA3 => InputNative.Key.ControlRight,
        0xA5 => InputNative.Key.AltRight,
        0xBA => InputNative.Key.Semicolon,
        0xBB => InputNative.Key.Equal,
        0xBC => InputNative.Key.Comma,
        0xBD => InputNative.Key.Minus,
        0xBE => InputNative.Key.Period,
        0xBF => InputNative.Key.Slash,
        0xC0 => InputNative.Key.Backquote,
        0xDB => InputNative.Key.BracketLeft,
        0xDC => InputNative.Key.Backslash,
        0xDD => InputNative.Key.BracketRight,
        0xDE => InputNative.Key.Quote,
        0xE2 => InputNative.Key.IntlBackslash,
        _ => InputNative.Key.Unidentified
    };

    private static uint GetAsciiKey(uint key) => key switch
    {
        >= 0x30 and <= 0x39 or 0x20 => key,
        >= 0x41 and <= 0x5A => key + ('a' - 'A'),
        0xBA => ';',
        0xBB => '=',
        0xBC => ',',
        0xBD => '-',
        0xBE => '.',
        0xBF => '/',
        0xC0 => '`',
        0xDB => '[',
        0xDC or 0xE2 => '\\',
        0xDD => ']',
        0xDE => '\'',
        _ => 0
    };

    private static char GetShiftedAscii(char key) => key switch
    {
        >= 'a' and <= 'z' => char.ToUpperInvariant(key),
        >= '0' and <= '9' => ")!@#$%^&*("[key - '0'],
        ';' => ':',
        '=' => '+',
        ',' => '<',
        '-' => '_',
        '.' => '>',
        '/' => '?',
        '`' => '~',
        '[' => '{',
        '\\' => '|',
        ']' => '}',
        '\'' => '"',
        _ => key
    };

    private sealed class KeyEncoderHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public KeyEncoderHandle(IntPtr value) : base(ownsHandle: true) => SetHandle(value);

        protected override bool ReleaseHandle()
        {
            InputNative.ghostty_key_encoder_free(handle);
            return true;
        }
    }

    private sealed class KeyEventHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public KeyEventHandle(IntPtr value) : base(ownsHandle: true) => SetHandle(value);

        protected override bool ReleaseHandle()
        {
            InputNative.ghostty_key_event_free(handle);
            return true;
        }
    }

    private static class InputNative
    {
        private const string Library = "ghostty-vt";
        internal const int OutOfSpace = -3;
        internal const int TerminalDataMode = 37;

        // Values from ghostty/vt/key/event.h.
        internal enum KeyAction { Release = 0, Press = 1, Repeat = 2 }
        internal enum Key
        {
            Unidentified = 0, Backquote = 1, Backslash = 2, BracketLeft = 3, BracketRight = 4, Comma = 5,
            Digit0 = 6, Equal = 16, IntlBackslash = 17, A = 20, Minus = 46, Period = 47, Quote = 48,
            Semicolon = 49, Slash = 50, AltLeft = 51, AltRight = 52, Backspace = 53, CapsLock = 54,
            ContextMenu = 55, ControlLeft = 56, ControlRight = 57, Enter = 58, MetaLeft = 59, MetaRight = 60,
            ShiftLeft = 61, ShiftRight = 62, Space = 63, Tab = 64, Delete = 68, End = 69, Help = 70,
            Home = 71, Insert = 72, PageDown = 73, PageUp = 74, ArrowDown = 75, ArrowLeft = 76, ArrowRight = 77,
            ArrowUp = 78, NumLock = 79, Numpad0 = 80, NumpadAdd = 90, NumpadClear = 92, NumpadDecimal = 95,
            NumpadDivide = 96, NumpadMultiply = 104, NumpadSubtract = 107, NumpadSeparator = 108, Escape = 120,
            F1 = 121, PrintScreen = 148, ScrollLock = 149, Pause = 150
        }

        // The top bit of GhosttyMode is clear for DEC private modes.
        internal enum DecMode : ushort { FocusEvent = 1004, BracketedPaste = 2004 }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ModeConfig
        {
            internal DecMode Mode;
            internal byte Value;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal readonly unsafe struct KeyText(byte* pointer, nuint length)
        {
            private readonly byte* _pointer = pointer;
            private readonly nuint _length = length;
        }

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_key_encoder_new(IntPtr allocator, out IntPtr encoder);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern void ghostty_key_encoder_free(IntPtr encoder);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_key_encoder_set_from_terminal(KeyEncoderHandle encoder, TerminalHandle terminal);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern unsafe int ghostty_key_encoder_encode(KeyEncoderHandle encoder, KeyEventHandle keyEvent,
            byte* buffer, nuint length, out nuint written);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_key_event_new(IntPtr allocator, out IntPtr keyEvent);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern void ghostty_key_event_free(IntPtr keyEvent);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_key_event_set_action(KeyEventHandle keyEvent, KeyAction action);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_key_event_set_key(KeyEventHandle keyEvent, Key key);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_key_event_set_mods(KeyEventHandle keyEvent, ushort mods);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_key_event_set_unshifted_codepoint(KeyEventHandle keyEvent, uint codepoint);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_key_event_set_utf8(KeyEventHandle keyEvent, KeyText text);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_terminal_get(TerminalHandle terminal, int data, ref ModeConfig config);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern unsafe int ghostty_paste_encode(byte* data, nuint dataLength,
            [MarshalAs(UnmanagedType.U1)] bool bracketed, byte* buffer, nuint bufferLength, out nuint written);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern unsafe int ghostty_focus_encode(int focusEvent, byte* buffer, nuint length, out nuint written);
    }
}
