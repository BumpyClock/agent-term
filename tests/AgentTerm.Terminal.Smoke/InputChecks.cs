using AgentTerm.Terminal;
using AgentTerm_App;

internal static class InputChecks
{
    public static void Run()
    {
        CheckKeyboardRouting();
        CheckComposition();
        using var terminal = new GhosttyTerminal();

        Check("a"u8, Key(0x41, text: "a"), "Printable key");
        Check("A"u8, Key(0x41, TerminalModifiers.Shift, text: "A"), "Shifted printable key");
        Check("é🙂界"u8, Key(0, text: "é🙂界"), "Committed Unicode text");
        var longText = new string('界', 100);
        Check(System.Text.Encoding.UTF8.GetBytes(longText), Key(0, text: longText), "Long committed text");
        Check("\r"u8, Key(0x0D), "Enter");
        Check("\x7f"u8, Key(0x08), "Backspace");
        Check("\t"u8, Key(0x09), "Tab");
        Check("\x1b"u8, Key(0x1B), "Escape");
        Check("\x1b[D"u8, Key(0x25), "Left");
        Check("\x1b[A"u8, Key(0x26), "Up");
        Check("\x1b[C"u8, Key(0x27), "Right");
        Check("\x1b[B"u8, Key(0x28), "Down");
        Check("\x1b[H"u8, Key(0x24), "Home");
        Check("\x1b[F"u8, Key(0x23), "End");
        Check("\x1b[5~"u8, Key(0x21), "Page Up");
        Check("\x1b[6~"u8, Key(0x22), "Page Down");
        Check("\x1b[2~"u8, Key(0x2D), "Insert");
        Check("\x1b[3~"u8, Key(0x2E), "Delete");
        Check("\x1bOP"u8, Key(0x70), "F1");
        Check("\x1b[45~"u8, Key(0x87), "F24");
        Check("\x1b[Z"u8, Key(0x09, TerminalModifiers.Shift), "Shift+Tab");
        Check("\x1b[1;5D"u8, Key(0x25, TerminalModifiers.Control), "Ctrl+Left");
        Check("\x03"u8, Key(0x43, TerminalModifiers.Control), "Ctrl+C");
        Check("\x03"u8, Key(0x43, TerminalModifiers.Control, text: "C"), "Ctrl+C with uppercase layout text");
        Check("\u001ba"u8, Key(0x41, TerminalModifiers.Alt), "Alt+A");
        Check("\u001bé"u8, Key(0, TerminalModifiers.Alt, text: "é"), "Alt with Unicode text");
        Check("\x1b[A"u8, Key(0x26, action: TerminalKeyAction.Repeat), "Legacy repeat");
        Check([], Key(0x26, action: TerminalKeyAction.Release), "Legacy release");

        terminal.Write("\x1b[?1h\x1b[?66h\x1b[?1035l"u8);
        Check("\x1bOA"u8, Key(0x26), "Application cursor Up");
        Check("\x1bOB"u8, Key(0x28), "Application cursor Down");
        Check("\x1bOq"u8, Key(0x61), "Application keypad 1");
        terminal.Write("\x1b[?1035h"u8);
        Check("1"u8, Key(0x61), "Numeric keypad override");
        terminal.Write("\x1b[?1l\x1b[?66l"u8);
        Check("\x1b[A"u8, Key(0x26), "Restored cursor Up");
        Check("1"u8, Key(0x61), "Restored keypad 1");

        terminal.Write("\x1b[?1036l"u8);
        Check("a"u8, Key(0x41, TerminalModifiers.Alt), "Alt escape prefix disabled");
        terminal.Write("\x1b[?1036h\x1b[?67h"u8);
        Check("\x08"u8, Key(0x08), "Backarrow mode");

        Check("é\rline"u8, terminal.EncodePaste("é\nline"), "Unbracketed paste");
        Check([], terminal.EncodeFocus(true), "Focus reporting disabled");
        terminal.Write("\x1b[?2004h\x1b[?1004h"u8);
        Check("\x1b[200~a\nb [201~ \x1b[201~"u8, terminal.EncodePaste("a\nb\u001b[201~\u0003"),
            "Bracketed paste sanitizes escape and interrupt bytes");
        Check([], terminal.EncodePaste(""), "Empty paste");
        Check("\x1b[I"u8, terminal.EncodeFocus(true), "Focus gained report");
        Check("\x1b[O"u8, terminal.EncodeFocus(false), "Focus lost report");
        terminal.Write("\x1b[?2004l\x1b[?1004l"u8);
        Check("plain"u8, terminal.EncodePaste("plain"), "Bracketed paste disabled");
        Check([], terminal.EncodeFocus(false), "Focus reporting restored to disabled");

        terminal.Write("\x1b[>3u"u8);
        Check("\x1b[1;1:2A"u8, Key(0x26, action: TerminalKeyAction.Repeat), "Kitty repeat event");
        Check("\x1b[1;1:3A"u8, Key(0x26, action: TerminalKeyAction.Release), "Kitty release event");
        terminal.Write("\x1b[<u"u8);
        Check([], Key(0x26, action: TerminalKeyAction.Release), "Kitty release reporting disabled");

        terminal.Write("\x1b[>11u"u8);
        Check("\x1b[1072u"u8, Key(0x41, text: "а"), "Kitty press uses the keyboard layout codepoint");
        Check("\x1b[1072;1:3u"u8, Key(0x41, action: TerminalKeyAction.Release), "Kitty release matches layout press");
        Check("\x1b[97u"u8, Key(0x41, text: "a"), "Kitty press after layout changes");
        Check("é🙂界"u8, Key(0, text: "é🙂界"), "Kitty committed Unicode text");
        Check([], Key(0xff, action: TerminalKeyAction.Release), "Unknown key release");

        byte[] Key(uint key, TerminalModifiers modifiers = TerminalModifiers.None,
            TerminalKeyAction action = TerminalKeyAction.Press, string? text = null) =>
            terminal.EncodeKey(key, modifiers, action, text);
    }

    private static void Check(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string description)
    {
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException($"{description}. Expected {Convert.ToHexString(expected)}, got {Convert.ToHexString(actual)}.");
    }

    private static void CheckComposition()
    {
        var composition = new TerminalCompositionInput();
        composition.Start("a");
        Expect(composition.IsComposing, "Composition should stay active while candidates change.");
        Expect(composition.Finish("a日本語") == "日本語" && !composition.IsComposing,
            "A committed composition should send its final text once without earlier input.");
        Expect(composition.Finish("日本語") == "", "A duplicate completion must not send text again.");
        composition.Start("");
        Expect(composition.Finish("") == "", "A cancelled composition must not send text.");
        composition.Start("");
        composition.Cancel();
        Expect(composition.Finish("界") == "", "A completion after cancellation must not send text.");
    }

    private static void CheckKeyboardRouting()
    {
        var keyboard = new TerminalKeyboardInput();
        var tab = new TerminalKeyboardInput.PhysicalKey(0x0F, false);
        var escape = new TerminalKeyboardInput.PhysicalKey(0x01, false);
        var letter = new TerminalKeyboardInput.PhysicalKey(0x12, false);
        var ime = new TerminalKeyboardInput.PhysicalKey(0, false);
        const TerminalModifiers ctrlAlt = TerminalModifiers.Control | TerminalModifiers.Alt;

        keyboard.KeyDown(letter, 0x45, TerminalModifiers.Shift, false, TerminalKeyboardInput.Route.Text);
        Expect(keyboard.Text(letter, "E") is { Key: 0x45, Text: "E", Modifiers: TerminalModifiers.Shift } &&
            keyboard.KeyUp(letter, TerminalModifiers.Shift) is { Key: 0x45, Action: TerminalKeyAction.Release },
            "Native text input should retain its physical key and release.");
        Expect(keyboard.Text(null, "日本語") is { Key: 0, Text: "日本語" } && keyboard.Text(null, "") is null,
            "Multi-character native text should be forwarded once without a physical key.");

        var firstTab = keyboard.KeyDown(tab, 0x09, TerminalModifiers.Shift, false, TerminalKeyboardInput.Route.Direct);
        var repeatTab = keyboard.KeyDown(tab, 0x09, TerminalModifiers.Shift, true, TerminalKeyboardInput.Route.Direct);
        Expect(firstTab == TerminalKeyAction.Press && repeatTab == TerminalKeyAction.Repeat &&
            keyboard.Character(tab, '\t') is null &&
            keyboard.KeyUp(tab, TerminalModifiers.Shift) is { Key: 0x09, Action: TerminalKeyAction.Release },
            "Shift+Tab should forward the physical press, repeat and release once.");

        keyboard.KeyDown(escape, 0x1B, TerminalModifiers.None, false, TerminalKeyboardInput.Route.Direct);
        Expect(keyboard.Character(escape, '\u001B') is null &&
            keyboard.Character(ime, '界') is { Key: 0, Text: "界" } &&
            keyboard.KeyUp(escape, TerminalModifiers.None) is { Key: 0x1B, Action: TerminalKeyAction.Release },
            "Escape should forward its physical key and preserve unrelated IME text.");

        keyboard.KeyDown(letter, 0x45, ctrlAlt, false, TerminalKeyboardInput.Route.Text, ctrlAlt);
        keyboard.KeyDown(tab, 0x09, TerminalModifiers.Shift, false, TerminalKeyboardInput.Route.Direct);
        Expect(keyboard.Character(letter, '€') is { Key: 0x45, Text: "€", Modifiers: TerminalModifiers.None } &&
            keyboard.KeyUp(letter, ctrlAlt) is { Key: 0x45, Action: TerminalKeyAction.Release, Modifiers: TerminalModifiers.None },
            "AltGr text should match its own key and consume Ctrl and Alt.");

        keyboard.KeyDown(tab, 0x09, TerminalModifiers.Control | TerminalModifiers.Shift, false,
            TerminalKeyboardInput.Route.Shortcut);
        Expect(keyboard.IsShortcut(tab) && keyboard.Character(tab, '\t') is null &&
            keyboard.KeyUp(tab, TerminalModifiers.None) is null,
            "The local focus shortcut should consume its terminal character and release.");

        Expect(keyboard.Character(ime, '\uDC00') is null && keyboard.Character(ime, '\uD83D') is null &&
            keyboard.Character(ime, '\uDE42') is { Key: 0, Text: "🙂" },
            "Supplementary characters should assemble and an unpaired surrogate should be ignored.");
        keyboard.KeyDown(escape, 0x1B, TerminalModifiers.None, false, TerminalKeyboardInput.Route.Direct);
        Expect(keyboard.Reset() is [{ Key: 0x1B, Action: TerminalKeyAction.Release }] &&
            keyboard.KeyUp(escape, TerminalModifiers.None) is null,
            "Focus loss should release held terminal keys once and clear pending keyboard state.");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
