using System.Runtime.InteropServices;
using System.Text;
using AgentTerm.Terminal;

internal static class ProgramStatusSessionChecks
{
    private static async Task CheckDetectionAsync()
    {
        var profile = new ShellProfile("status-fixture", "Status fixture",
            Path.Combine(AppContext.BaseDirectory, "AgentTerm.ConPty.Smoke.exe"), "--status-fixture", AppContext.BaseDirectory);
        await using var session = new TerminalSession(120, 30, profile);
        session.Start();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        if (session.ReadProgramStatuses().SingleOrDefault() is not { State: ProgramState.Done })
            throw new Exception("A raw terminal program must receive the exact OSC 7501 detection response. " + session.ReadText());
    }

    public static void RunFixture()
    {
        var input = GetStdHandle(-10);
        if (!GetConsoleMode(input, out var mode) || !SetConsoleMode(input, 0x0200))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        Console.OutputEncoding = new UTF8Encoding(false);
        using var stream = Console.OpenStandardInput();
        try
        {
            Console.Write("\u001b]7501;?\u001b\\\u001b[c");
            var expected = Encoding.UTF8.GetBytes("\u001b]7501;?:states=idle,working,done,blocked,error:kinds=permission,question,auth\u001b\\");
            var reply = new byte[expected.Length];
            stream.ReadExactly(reply);
            Console.Write(reply.SequenceEqual(expected) ? "\u001b]7501;state=done\a" : "\u001b]7501;state=error\a");
        }
        finally { SetConsoleMode(input, mode); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    public static async Task RunAsync()
    {
        await CheckDetectionAsync();
        await using var session = new TerminalSession(120, 30, TestShell.Profile);
        session.Start();
        async Task Wait(Func<bool> condition, string message)
        {
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (!condition())
            {
                if (DateTime.UtcNow >= deadline) throw new Exception(message + "\n" + session.ReadText());
                await Task.Delay(25);
            }
        }
        await Wait(() => session.PromptState == ShellPromptState.Ready, "Status fixture managed prompt must be ready.");
        session.SubmitCommand("""
            function Report-Status($body) { [Console]::Write(([char]27 + ']7501;' + $body + [char]7)) }
            Report-Status 'state=done:id=completed'
            Report-Status 'state=working:app=fixture:progress=42'
            Report-Status 'state=blocked:id=worker:kind=question:title=V29ya2Vy:msg=QW5zd2VyIG5lZWRlZA=='
            $null = Read-Host 'status-fixture-input'
            """);
        await Wait(() => session.ReadProgramStatuses().Count == 3, "ConPTY must transport all three status reports.");
        var records = session.ReadProgramStatuses();
        if (records.Single(r => r.Id == "").Progress != 42 || records.Single(r => r.Id == "worker") is not { EffectiveApp: "fixture", Message: "Answer needed", Kind: "question" })
            throw new Exception("Session snapshots must expose protocol details and inherited apps.");
        await Wait(() => session.ReadText().Contains("status-fixture-input:"), "Fixture must reach its input prompt.");
        session.Send("answer\r");
        await Wait(() => session.PromptState == ShellPromptState.Ready, "Fixture command must return to the shell.");
        if (session.ReadProgramStatuses() is not { Count: 1 } || session.ReadProgramStatuses()[0] is not { Id: "completed", IsActive: false })
            throw new Exception("Native prompt must clear active records while preserving completed reports.");
        session.SubmitCommand("Report-Status 'state=working:id=running'; Report-Status 'state=error:id=failed'; exit 0");
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(25));
        records = session.ReadProgramStatuses();
        if (records.Count != 2 || records.Any(r => r.IsActive))
            throw new Exception("Process exit must clear active status while retaining completed and failed outcomes.");
        Console.WriteLine("Program status ConPTY checks passed detection round-trip, hierarchy, prompt cleanup and process exit.");
    }
}
