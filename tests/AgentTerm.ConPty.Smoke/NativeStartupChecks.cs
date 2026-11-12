using System.Reflection;
using System.Text;
using AgentTerm.Terminal;

internal static class NativeStartupChecks
{
    internal static async Task RunAsync()
    {
        // Launch the actual generated integration after an isolated, in-memory profile.
        // This must not write or replace the user's PowerShell profile.
        var profile = TestShell.Profile;
        var integration = typeof(TerminalSession).Assembly.GetType("AgentTerm.Terminal.ShellIntegration")!;
        var arguments = (string)integration.GetMethod("Arguments", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [profile, "native-startup-fixture", Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".input")])!;
        var encoded = arguments.Split("-EncodedCommand ")[1];
        var body = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        var setup = """
            Import-Module PSReadLine
            $global:fixtureReadLine = $function:PSConsoleHostReadLine
            function global:PSConsoleHostReadLine {
                $global:fixturePreviousOk = $?
                & $global:fixtureReadLine
            }
            function global:prompt { "fixture-native> " }
            Set-PSReadLineKeyHandler -Chord Enter -BriefDescription FixtureCustom -ScriptBlock {
                $global:fixtureEnterCount++
                [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()
            }
            """;
        var gate = new object();
        using var terminal = new GhosttyTerminal(100, 30);
        var protocol = new ShellPromptProtocol("native-startup-fixture");
        var scanner = new OscScanner(); scanner.Sequence += protocol.Accept;
        await using var connection = new ConPtyConnection(100, 30);
        connection.OutputReceived += bytes =>
        {
            lock (gate)
            {
                terminal.Write(bytes.Span); scanner.Feed(bytes.Span);
                foreach (var reply in terminal.TakeResponses()) connection.Send(reply);
            }
        };
        connection.Start(profile.Executable, profile.Arguments + " -NoExit -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(setup + "\n" + body)), profile.StartingDirectory);
        async Task Wait(Func<bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            while (true)
            {
                lock (gate) { if (predicate()) return; }
                await Task.Delay(25, timeout.Token);
            }
        }
        await Wait(() => protocol.State == ShellPromptState.Ready && terminal.ReadText().Contains("fixture-native>"));
        lock (gate) { if (protocol.AgentEntryAvailable) throw new Exception("Custom Enter must disable only agent entry."); }
        connection.Send(Encoding.UTF8.GetBytes("Write-Output ('custom-count-' + $global:fixtureEnterCount)\r"));
        await Wait(() => terminal.ReadText().Contains("custom-count-1") && protocol.State == ShellPromptState.Ready);
        connection.Send(Encoding.UTF8.GetBytes("cmd /c exit 7\r"));
        await Wait(() => protocol.LastCommandSucceeded == false && protocol.State == ShellPromptState.Ready);
        connection.Send(Encoding.UTF8.GetBytes("Write-Output ('previous-ok-' + $global:fixturePreviousOk)\r"));
        await Wait(() => terminal.ReadText().Contains("previous-ok-False") && protocol.State == ShellPromptState.Ready);
    }
}
