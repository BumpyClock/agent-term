using System.Runtime.InteropServices;
using System.Text;
using AgentTerm.Terminal;

internal static class MouseSessionChecks
{
    public static async Task RunAsync()
    {
        var profile = new ShellProfile("mouse-fixture", "Mouse fixture",
            Path.Combine(AppContext.BaseDirectory, "AgentTerm.ConPty.Smoke.exe"),
            "--mouse-fixture", AppContext.BaseDirectory);
        await using var session = new TerminalSession(80, 24, profile);
        session.Start();
        await WaitAsync(session, "MOUSE_READY");
        var geometry = new TerminalMouseGeometry(800, 480, 10, 20);
        var press = new TerminalMouseEvent(TerminalMouseAction.Press, TerminalMouseButton.Left,
            TerminalModifiers.None, 25, 65);
        if (!session.SendMouse(press, geometry)) throw new Exception("The mouse fixture did not enable tracking.");
        await WaitAsync(session, "REPORT=1B5B3C303B333B344D"); // CSI <0;3;4M
        session.SendMouse(press with { Action = TerminalMouseAction.Release }, geometry);
        await WaitAsync(session, "REPORT=1B5B3C303B333B346D"); // CSI <0;3;4m
        session.SendMouse(press with { Button = TerminalMouseButton.WheelDown }, geometry);
        await WaitAsync(session, "REPORT=1B5B3C36353B333B344D"); // CSI <65;3;4M
        if (session.SendMouse(press with { Modifiers = TerminalModifiers.Shift }, geometry))
            throw new Exception("Shift must return mouse input to local selection.");
        session.Send("d");
        await WaitAsync(session, "MOUSE_DISABLED");
        if (session.SendMouse(press, geometry)) throw new Exception("Disabled mouse tracking must return input to local selection.");
        session.Send("q");
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        await SmartReleaseAsync(profile);
        await HoldMappingAsync(profile);
        Console.WriteLine("Mouse ConPTY fixture passed press, release, wheel, Shift bypass, tracking disable, and Smart release/held-motion clamping.");
    }

    private static async Task HoldMappingAsync(ShellProfile profile)
    {
        await using var session = new TerminalSession(80, 24, profile);
        session.Start();
        await WaitAsync(session, "MOUSE_READY");
        var displayed = session.ReadFrame();
        session.Send("t");
        await WaitAsync(session, "HOLD_READY");
        var geometry = new TerminalMouseGeometry(800, 480, 10, 20);
        var press = new TerminalMouseEvent(TerminalMouseAction.Press, TerminalMouseButton.Left, TerminalModifiers.None, 25, 65);
        session.SendMouse(press, geometry, displayed);
        await WaitAsync(session, "REPORT=1B5B3C303B333B344D");
        session.SendMouse(press with { Action = TerminalMouseAction.Release }, geometry, displayed);
        await WaitAsync(session, "REPORT=1B5B3C303B333B346D");
        session.Send("u");
        await WaitAsync(session, "HOLD_RELEASED");
        var current = session.ReadFrame();
        if (current.ActiveScreenOffset <= displayed.ActiveScreenOffset)
            throw new Exception("The render-hold fixture did not scroll the live screen.");
        session.Send("q");
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async Task SmartReleaseAsync(ShellProfile profile)
    {
        await using var session = new TerminalSession(80, 24, profile);
        session.SetBottomAnchored(true);
        session.Start();
        await WaitAsync(session, "MOUSE_READY");
        session.Send("s");
        await WaitAsync(session, "SMART_READY");
        var frame = session.ReadFrame();

        if (frame.Viewport.TopInset <= 0)
            throw new Exception($"Fixture did not expose history above active rows: {frame.Viewport}.");
        var geometry = new TerminalMouseGeometry(800, 480, 10, 20);
        var outside = new TerminalMouseEvent(TerminalMouseAction.Press, TerminalMouseButton.Left,
            TerminalModifiers.None, 25, 0);
        if (!session.SendMouse(outside, geometry)) throw new Exception("Tracking did not own the filtered press.");
        session.Send("c");
        await WaitAsync(session, "CHECKPOINT");
        if (session.ReadText().Contains("REPORT=", StringComparison.Ordinal))
            throw new Exception("Press outside active rows reached the fixture.");
        if (!session.SendMouse(outside with { Action = TerminalMouseAction.Release }, geometry))
            throw new Exception("Tracking did not own the release.");
        await WaitAsync(session, "REPORT=1B5B3C303B333B316D");
        var tallGeometry = new TerminalMouseGeometry(800, 960, 10, 20);
        var below = outside with { Y = 1000 };
        if (!session.SendMouse(below, tallGeometry)) throw new Exception("Tracking did not own the filtered lower press.");
        session.Send("c");
        if (!session.SendMouse(below with { Action = TerminalMouseAction.Release }, tallGeometry))
            throw new Exception("Tracking did not own the lower release.");
        await WaitAsync(session, "REPORT=1B5B3C303B333B32346D");
        if (session.ReadText().Split("REPORT=", StringSplitOptions.None).Length != 3)
            throw new Exception("Press below active rows reached the fixture.");
        session.Send("h");
        await WaitAsync(session, "HELD_READY");
        var held = outside with { Action = TerminalMouseAction.Motion };
        session.SendMouse(held, geometry);
        await WaitAsync(session, "REPORT=1B5B3C33323B333B314D");
        session.SendMouse(held with { Y = 1000 }, tallGeometry);
        await WaitAsync(session, "REPORT=1B5B3C33323B333B32344D");
        session.SendMouse(held with { Button = null }, geometry);
        session.Send("c");
        await WaitAsync(session, "CHECKPOINT-3");
        if (session.ReadText().Split("REPORT=", StringSplitOptions.None).Length != 5)
            throw new Exception("Unheld motion outside active rows reached the fixture.");
        session.Send("q");
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(20));
    }

    public static void RunFixture()
    {
        var input = GetStdHandle(-10);
        if (!GetConsoleMode(input, out var originalMode) || !SetConsoleMode(input, 0x0200))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        Console.OutputEncoding = new UTF8Encoding(false);
        using var stream = Console.OpenStandardInput();
        try
        {
            Console.Write("\x1b[?1000h\x1b[?1006hMOUSE_READY\r\n");
            var report = new List<byte>();
            var checkpoint = 0;
            while (stream.ReadByte() is var value && value >= 0)
            {
                if (report.Count == 0 && value == 'q') break;
                if (report.Count == 0 && value == 'd')
                {
                    Console.Write("\x1b[?1000l\x1b[?1006lMOUSE_DISABLED\r\n");
                    continue;
                }
                if (report.Count == 0 && value == 'h')
                {
                    Console.Write("\x1b[?1003hHELD_READY\r\n");
                    continue;
                }
                if (report.Count == 0 && value == 's')
                {
                    for (var i = 0; i < 30; i++) Console.Write($"history-{i}\r\n");
                    Console.Write("\x1b[H\x1b[2JSMART_READY");
                    continue;
                }
                if (report.Count == 0 && value == 't')
                {
                    Console.Write("\x1b[?2026h");
                    for (var i = 0; i < 50; i++) Console.Write($"held-history-{i}\r\n");
                    Console.Write("HOLD_READY\r\n");
                    continue;
                }
                if (report.Count == 0 && value == 'u')
                {
                    Console.Write("\x1b[?2026lHOLD_RELEASED\r\n");
                    continue;
                }
                if (report.Count == 0 && value == 'c')
                {
                    Console.Write($"\r\nCHECKPOINT-{++checkpoint}\r\n");
                    continue;
                }
                report.Add((byte)value);
                if (value is 'M' or 'm')
                {
                    Console.Write($"REPORT={Convert.ToHexString(report.ToArray())}\r\n");
                    report.Clear();
                }
            }
        }
        finally
        {
            Console.Write("\x1b[?1000l\x1b[?1006l");
            SetConsoleMode(input, originalMode);
        }
    }

    private static async Task WaitAsync(TerminalSession session, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (session.ReadText().Contains(expected, StringComparison.Ordinal)) return;
            if (session.Completion.IsCompleted) break;
            await Task.Delay(30);
        }
        throw new Exception($"Mouse fixture did not show {expected}.\n{session.ReadText()}");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);
}
