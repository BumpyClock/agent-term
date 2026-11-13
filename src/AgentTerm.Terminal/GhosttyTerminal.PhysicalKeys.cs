namespace AgentTerm.Terminal;

public sealed partial class GhosttyTerminal
{
    // Windows set-1 scan codes identify positions independently of the active layout.
    // The virtual key is used only for scanless input and keypad NumLock navigation.
    private static InputNative.Key GetPhysicalKey(uint virtualKey, uint scanCode, bool extended)
    {
        if (scanCode == 0 || virtualKey == 0x13) return GetGhosttyKey(virtualKey);
        if (scanCode == 0x45 && virtualKey == 0x90) return InputNative.Key.NumLock;
        if (extended)
            return scanCode switch
            {
                0x1C => InputNative.Key.NumpadEnter, 0x1D => InputNative.Key.ControlRight,
                0x35 => InputNative.Key.NumpadDivide, 0x37 => InputNative.Key.PrintScreen,
                0x38 => InputNative.Key.AltRight, 0x47 => InputNative.Key.Home,
                0x48 => InputNative.Key.ArrowUp, 0x49 => InputNative.Key.PageUp,
                0x4B => InputNative.Key.ArrowLeft, 0x4D => InputNative.Key.ArrowRight,
                0x4F => InputNative.Key.End, 0x50 => InputNative.Key.ArrowDown,
                0x51 => InputNative.Key.PageDown, 0x52 => InputNative.Key.Insert,
                0x53 => InputNative.Key.Delete, 0x5B => InputNative.Key.MetaLeft,
                0x5C => InputNative.Key.MetaRight, 0x5D => InputNative.Key.ContextMenu,
                _ => InputNative.Key.Unidentified
            };
        return scanCode switch
        {
            0x01 => InputNative.Key.Escape,
            >= 0x02 and <= 0x0A => InputNative.Key.Digit0 + (int)(scanCode - 1),
            0x0B => InputNative.Key.Digit0, 0x0C => InputNative.Key.Minus, 0x0D => InputNative.Key.Equal,
            0x0E => InputNative.Key.Backspace, 0x0F => InputNative.Key.Tab,
            >= 0x10 and <= 0x19 => Letter("QWERTYUIOP"[(int)scanCode - 0x10]),
            0x1A => InputNative.Key.BracketLeft, 0x1B => InputNative.Key.BracketRight,
            0x1C => InputNative.Key.Enter, 0x1D => InputNative.Key.ControlLeft,
            >= 0x1E and <= 0x26 => Letter("ASDFGHJKL"[(int)scanCode - 0x1E]),
            0x27 => InputNative.Key.Semicolon, 0x28 => InputNative.Key.Quote,
            0x29 => InputNative.Key.Backquote, 0x2A => InputNative.Key.ShiftLeft,
            0x2B => InputNative.Key.Backslash,
            >= 0x2C and <= 0x32 => Letter("ZXCVBNM"[(int)scanCode - 0x2C]),
            0x33 => InputNative.Key.Comma, 0x34 => InputNative.Key.Period, 0x35 => InputNative.Key.Slash,
            0x36 => InputNative.Key.ShiftRight, 0x37 => InputNative.Key.NumpadMultiply,
            0x38 => InputNative.Key.AltLeft, 0x39 => InputNative.Key.Space, 0x3A => InputNative.Key.CapsLock,
            >= 0x3B and <= 0x44 => InputNative.Key.F1 + (int)(scanCode - 0x3B),
            0x45 => InputNative.Key.NumLock, 0x46 => InputNative.Key.ScrollLock,
            0x47 => virtualKey == 0x24 ? InputNative.Key.NumpadHome : InputNative.Key.Numpad0 + 7,
            0x48 => virtualKey == 0x26 ? InputNative.Key.NumpadUp : InputNative.Key.Numpad0 + 8,
            0x49 => virtualKey == 0x21 ? InputNative.Key.NumpadPageUp : InputNative.Key.Numpad0 + 9,
            0x4A => InputNative.Key.NumpadSubtract,
            0x4B => virtualKey == 0x25 ? InputNative.Key.NumpadLeft : InputNative.Key.Numpad0 + 4,
            0x4C => virtualKey == 0x0C ? InputNative.Key.NumpadBegin : InputNative.Key.Numpad0 + 5,
            0x4D => virtualKey == 0x27 ? InputNative.Key.NumpadRight : InputNative.Key.Numpad0 + 6,
            0x4E => InputNative.Key.NumpadAdd,
            0x4F => virtualKey == 0x23 ? InputNative.Key.NumpadEnd : InputNative.Key.Numpad0 + 1,
            0x50 => virtualKey == 0x28 ? InputNative.Key.NumpadDown : InputNative.Key.Numpad0 + 2,
            0x51 => virtualKey == 0x22 ? InputNative.Key.NumpadPageDown : InputNative.Key.Numpad0 + 3,
            0x52 => virtualKey == 0x2D ? InputNative.Key.NumpadInsert : InputNative.Key.Numpad0,
            0x53 => virtualKey == 0x2E ? InputNative.Key.NumpadDelete : InputNative.Key.NumpadDecimal,
            0x54 => InputNative.Key.PrintScreen,
            0x56 => InputNative.Key.IntlBackslash, 0x57 => InputNative.Key.F1 + 10,
            0x58 => InputNative.Key.F1 + 11, 0x59 => InputNative.Key.NumpadEqual,
            0x70 => InputNative.Key.KanaMode, 0x73 => InputNative.Key.IntlRo,
            0x79 => InputNative.Key.Convert, 0x7B => InputNative.Key.NonConvert, 0x7D => InputNative.Key.IntlYen,
            _ => scanCode >= 0x64 && virtualKey is >= 0x7C and <= 0x87
                ? GetGhosttyKey(virtualKey) : InputNative.Key.Unidentified
        };

        static InputNative.Key Letter(char letter) => InputNative.Key.A + letter - 'A';
    }
}
