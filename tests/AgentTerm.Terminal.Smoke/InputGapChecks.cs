using System.Text;
using AgentTerm.Terminal;
using AgentTerm_App;

internal static class InputGapChecks
{
    internal static void Run()
    {
        using var terminal = new GhosttyTerminal();
        var keyboard = new TerminalKeyboardInput();
        var enter = new TerminalKeyboardInput.PhysicalKey(0x1C, false);
        var keypadEnter = new TerminalKeyboardInput.PhysicalKey(0x1C, true);
        var letter = new TerminalKeyboardInput.PhysicalKey(0x10, false);
        const TerminalModifiers locks = TerminalModifiers.CapsLock | TerminalModifiers.NumLock;
        const TerminalModifiers altGr = TerminalModifiers.Control | TerminalModifiers.Alt | TerminalModifiers.AltRight;
        const TerminalModifiers consumed = TerminalModifiers.Control | TerminalModifiers.Alt;

        Equal("\r", Encode(0x0D, enter), "Main Enter in legacy mode");
        Equal("\r", Encode(0x0D, keypadEnter), "Keypad Enter in legacy mode");
        terminal.Write("\x1b[?66h\x1b[?1035l"u8);
        Equal("\x1bOM", Encode(0x0D, keypadEnter), "Application keypad Enter");
        Equal("\r", Encode(0x0D, enter), "Main Enter remains distinct from application keypad Enter");
        terminal.Write("\x1b[?1035h"u8);
        Equal("\r", Encode(0x0D, keypadEnter, TerminalModifiers.NumLock), "NumLock overrides application keypad mode");
        terminal.Write("\x1b[?66l\x1b[>15u"u8);
        Equal("\x1b[57414u", Encode(0x0D, keypadEnter), "Kitty keypad Enter identity");
        Equal("\x1b[13u", Encode(0x0D, enter), "Kitty main Enter identity");
        Equal("\x1b[57414;1:2u", Encode(0x0D, keypadEnter, action: TerminalKeyAction.Repeat), "Keypad repeat identity");
        Equal("\x1b[57414;1:3u", Encode(0x0D, keypadEnter, action: TerminalKeyAction.Release), "Keypad release identity");
        Equal("\x1b[57423u", Encode(0x24, new(0x47, false)), "NumLock-off keypad Home identity");
        Equal("\x1b[1;1:1H", Encode(0x24, new(0x47, true)), "Dedicated Home identity");

        // AZERTY logical A is at the physical Q position. Both values must survive.
        keyboard.KeyDown(letter, 0x41, locks, false, TerminalKeyboardInput.Route.Text, layoutCodepoint: 'a');
        var text = keyboard.Text(letter, "A")!.Value;
        Equal("\x1b[97::113;193u", Route(text), "Layout A, physical Q, and both lock states");
        Equal("\x1b[97::113;193:3u", Route(keyboard.KeyUp(letter, locks)!.Value), "Layout release retains physical identity");
        Equal("\x1b[97;65u", Encode(0x41, new(0x1E, false), TerminalModifiers.CapsLock, text: "A", layout: 'a'), "CapsLock report");
        Equal("\x1b[97;129u", Encode(0x41, new(0x1E, false), TerminalModifiers.NumLock, text: "a", layout: 'a'), "NumLock report");
        if (TerminalKeyboardInput.ShortcutModifiers(locks | TerminalModifiers.Control | TerminalModifiers.ControlRight)
            != TerminalModifiers.Control) throw new InvalidOperationException("Locks and modifier side must not change shortcut matching.");

        keyboard.KeyDown(letter, 0x51, altGr, false, TerminalKeyboardInput.Route.Text, consumed, 'q');
        var altGrText = keyboard.Text(letter, "@")!.Value;
        // Kitty preserves actual modifiers, while native consumed-mods determines text routing.
        Equal("\x1b[113;7u", Route(altGrText), "Kitty AltGr retains raw modifier state");
        Equal("\x1b[113;7:3u", Route(keyboard.KeyUp(letter, altGr)!.Value), "Kitty AltGr release");
        terminal.Write("\x1b[>3u"u8);
        Equal("@", Route(altGrText), "Kitty consumes translated modifiers when choosing its plain text path");
        terminal.Write("\x1b[<u"u8);
        Equal("\x1b[113;7u", Route(altGrText), "Popping Kitty flags restores full raw modifier reporting");

        terminal.Write("\x1b[<u"u8);
        // This catches encoders that accept consumed-mods but still turn AltGr into Ctrl+Alt shortcuts.
        Equal("@", Route(altGrText), "Legacy AltGr committed text");
        Equal("\x1b€", terminal.EncodeKey(0x45, consumed, TerminalKeyAction.Press, "€", 0x12,
            consumedModifiers: TerminalModifiers.Control, layoutCodepoint: 'e'), "Consumed Ctrl preserves unconsumed Alt");
        Equal("\x03", terminal.EncodeKey(0x43, consumed, TerminalKeyAction.Press, "c", 0x2E,
            consumedModifiers: TerminalModifiers.Alt, layoutCodepoint: 'c'), "Consumed Alt preserves unconsumed Ctrl");
        Equal("\x03", Encode(0x43, new(0x2E, false), TerminalModifiers.Control, text: "c", layout: 'c'), "Unconsumed Ctrl+C remains an interrupt");
        Equal("\x1bq", Encode(0x51, letter, TerminalModifiers.Alt, text: "q", layout: 'q'), "Unconsumed Alt keeps its escape prefix");
        terminal.Write("\x1b[>4;2m"u8);
        Equal("@", Route(altGrText), "Consumed AltGr in modifyOtherKeys mode 2");
        Equal("\x1b[27;5;99~", Encode(0x43, new(0x2E, false), TerminalModifiers.Control, text: "c", layout: 'c'), "Unconsumed Ctrl+C in modifyOtherKeys mode 2");
        terminal.Write("\x1b[>4;0m"u8);
        Equal("a", Encode(0x41, new(0x1E, false), locks, text: "a", layout: 'a'), "Legacy ignores lock bits for text");

        terminal.Write("\x1b[>15u"u8);
        var copy = new TerminalKeyboardInput.PhysicalKey(0x2E, false);
        keyboard.KeyDown(copy, 0x43, TerminalModifiers.Control | locks, false, TerminalKeyboardInput.Route.Shortcut);
        if (keyboard.Text(copy, "c") is not null || keyboard.KeyUp(copy, locks) is not null || keyboard.Reset().Length != 0)
            throw new InvalidOperationException("Local shortcuts must not leak text, Kitty release, or focus-loss releases.");

        byte[] Encode(uint key, TerminalKeyboardInput.PhysicalKey physical, TerminalModifiers mods = TerminalModifiers.None,
            TerminalKeyAction action = TerminalKeyAction.Press, string? text = null, uint? layout = null) =>
            terminal.EncodeKey(key, mods, action, text, physical.ScanCode, physical.Extended, layoutCodepoint: layout);
        byte[] Route(TerminalKeyboardInput.KeyEvent key) => terminal.EncodeKey(key.Key, key.Modifiers, key.Action,
            key.Text, key.Physical.ScanCode, key.Physical.Extended, key.ConsumedModifiers, key.LayoutCodepoint);
    }

    private static void Equal(string expected, byte[] actual, string description)
    {
        var wanted = Encoding.UTF8.GetBytes(expected);
        if (!actual.AsSpan().SequenceEqual(wanted))
            throw new InvalidOperationException($"{description}. Expected {Convert.ToHexString(wanted)}, got {Convert.ToHexString(actual)}.");
    }
}
