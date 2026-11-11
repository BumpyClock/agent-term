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
        Console.WriteLine("Mouse ConPTY fixture passed press, release, wheel, Shift bypass, and tracking disable.");
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
            while (stream.ReadByte() is var value && value >= 0)
            {
                if (report.Count == 0 && value == 'q') break;
                if (report.Count == 0 && value == 'd')
                {
                    Console.Write("\x1b[?1000l\x1b[?1006lMOUSE_DISABLED\r\n");
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
