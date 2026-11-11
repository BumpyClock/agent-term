using AgentTerm.Terminal;

internal static class MouseChecks
{
    public static void Run()
    {
        using var terminal = new GhosttyTerminal(80, 24);
        var geometry = new TerminalMouseGeometry(800, 480, 10, 20);
        CheckTracking(false);
        Check([], Mouse(), "Disabled mouse reporting");

        terminal.Write("\x1b[?1000h"u8);
        CheckTracking(true);
        Check("\x1b[M \x22\x22"u8, Mouse(), "Normal left press uses one-based cells");
        Check("\x1b[M!\x22\x22"u8, Mouse(button: TerminalMouseButton.Middle), "Normal middle press");
        Check("\x1b[M\x22\x22\x22"u8, Mouse(button: TerminalMouseButton.Right), "Normal right press");
        Check("\x1b[M#\x22\x22"u8, Mouse(TerminalMouseAction.Release), "Normal button release");
        Check([], Mouse(TerminalMouseAction.Motion), "Normal mode filters drag motion");
        Check("\x1b[M`\x22\x22"u8, Mouse(button: TerminalMouseButton.WheelUp), "Normal wheel up");
        Check("\x1b[Ma\x22\x22"u8, Mouse(button: TerminalMouseButton.WheelDown), "Normal wheel down");
        Check("\x1b[M8\x22\x22"u8, Mouse(modifiers: TerminalModifiers.Control | TerminalModifiers.Alt),
            "Normal mode preserves keyboard modifiers");

        terminal.Write("\x1b[?1006h"u8);
        Check("\x1b[<0;2;2M"u8, Mouse(), "SGR press");
        Check("\x1b[<0;2;2m"u8, Mouse(TerminalMouseAction.Release), "SGR release identifies the button");
        Check("\x1b[<64;2;2M"u8, Mouse(button: TerminalMouseButton.WheelUp), "SGR wheel up");
        Check("\x1b[<65;2;2M"u8, Mouse(button: TerminalMouseButton.WheelDown), "SGR wheel down");
        Check("\x1b[<66;2;2M"u8, Mouse(button: TerminalMouseButton.WheelLeft), "SGR wheel left");
        Check("\x1b[<67;2;2M"u8, Mouse(button: TerminalMouseButton.WheelRight), "SGR wheel right");
        Check("\x1b[<0;1;1M"u8, Mouse(x: 9.99f, y: 19.99f), "Fractional coordinates stay within their cell");
        Check("\x1b[<0;2;2M"u8, Mouse(x: 10, y: 20), "Cell boundary advances both coordinates");
        Check([], Mouse(x: -1, y: -1), "Normal mode suppresses presses outside the surface");
        Check("\x1b[<0;1;1m"u8, Mouse(TerminalMouseAction.Release, x: -10, y: -20), "Release outside clamps to first cell");
        Check("\x1b[<0;80;24m"u8, Mouse(TerminalMouseAction.Release, x: 10000, y: 10000), "Release outside clamps to last cell");
        Check([], Mouse(x: 2300), "An out of surface press is filtered");
        var wideGeometry = new TerminalMouseGeometry(4000, 480, 10, 20);
        Check("\x1b[<0;231;2M"u8, terminal.EncodeMouse(new(TerminalMouseAction.Press,
            TerminalMouseButton.Left, TerminalModifiers.None, 2300, 34), wideGeometry), "SGR supports columns above 223");

        terminal.Write("\u001bc\x1b[?9h"u8);
        CheckTracking(true);
        Check("\x1b[M \x22\x22"u8, Mouse(modifiers: TerminalModifiers.Control | TerminalModifiers.Alt),
            "X10 tracking excludes modifiers");
        Check([], Mouse(TerminalMouseAction.Release), "X10 tracking filters release");
        Check([], Mouse(TerminalMouseAction.Motion), "X10 tracking filters motion");
        Check([], Mouse(button: TerminalMouseButton.WheelUp), "X10 tracking filters wheel");

        terminal.Write("\u001bc\x1b[?1002h\x1b[?1006h"u8);
        Check([], Mouse(TerminalMouseAction.Motion, button: null), "Button tracking filters passive hover");
        Check("\x1b[<32;2;2M"u8, Mouse(TerminalMouseAction.Motion), "Button tracking forwards drag");
        Check("\x1b[<32;1;1M"u8, Mouse(TerminalMouseAction.Motion, x: -20, y: -20), "Held drag outside the surface clamps");

        terminal.Write("\u001bc\x1b[?1003h\x1b[?1006h"u8);
        Check("\x1b[<35;2;2M"u8, Mouse(TerminalMouseAction.Motion, button: null), "Any-event tracking forwards passive hover");
        Check([], Mouse(TerminalMouseAction.Motion, button: null, x: -10), "Passive hover outside the surface is filtered");
        Check("\x1b[<32;2;2M"u8, Mouse(TerminalMouseAction.Motion), "Any-event tracking forwards held motion");
        Check("\x1b[<35;2;2M"u8, Mouse(TerminalMouseAction.Motion, button: null), "No-button motion clears the previous button");

        terminal.Write("\x1b[?1016h"u8);
        Check("\x1b[<0;12;35M"u8, Mouse(x: 12.25f, y: 34.75f), "SGR pixels uses surface pixels");
        Check("\x1b[<35;13;35M"u8, Mouse(TerminalMouseAction.Motion, button: null, x: 12.75f, y: 34.75f),
            "SGR pixels preserves motion within a cell");
        Check("\x1b[<0;18;52M"u8, terminal.EncodeMouse(new(TerminalMouseAction.Press,
            TerminalMouseButton.Left, TerminalModifiers.None, 12.25f, 34.75f), geometry with { PixelScale = 1.5f }),
            "SGR pixels uses physical DPI scaling");
        Check("\x1b[<0;22;45M"u8, terminal.EncodeMouse(new(TerminalMouseAction.Press,
            TerminalMouseButton.Left, TerminalModifiers.None, 22.25f, 44.75f), new(1200, 720, 15, 30)),
            "Pixel geometry updates after a resize");

        terminal.Write("\u001bc"u8);
        CheckTracking(false);
        Check([], Mouse(), "Terminal reset disables mouse reporting");
        terminal.Write("\x1b[?1000h\x1b[?1006h"u8);
        Check("\x1b[<0;2;2M"u8, Mouse(), "Encoder follows modes enabled after reset");
        terminal.Write("\x1b[?1000l"u8);
        CheckTracking(false);
        Check([], Mouse(), "Tracking can be disabled while a format remains enabled");

        void CheckTracking(bool expected)
        {
            if (terminal.IsMouseTrackingEnabled != expected)
                throw new InvalidOperationException($"Mouse tracking should be {expected}.");
        }

        byte[] Mouse(TerminalMouseAction action = TerminalMouseAction.Press,
            TerminalMouseButton? button = TerminalMouseButton.Left, TerminalModifiers modifiers = TerminalModifiers.None,
            float x = 12, float y = 34) => terminal.EncodeMouse(new(action, button, modifiers, x, y), geometry);
    }

    private static void Check(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string description)
    {
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException($"{description}. Expected {Convert.ToHexString(expected)}, got {Convert.ToHexString(actual)}.");
    }
}
