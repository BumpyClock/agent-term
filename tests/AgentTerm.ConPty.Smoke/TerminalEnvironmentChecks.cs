using System.Diagnostics;
using AgentTerm.Terminal;

internal static class TerminalEnvironmentChecks
{
    public static async Task RunAsync()
    {
        var keys = new[] { "TERM", "COLORTERM", "TERM_PROGRAM", "NO_COLOR", "AGENTTERM_ENV_TEST" };
        var saved = keys.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            Environment.SetEnvironmentVariable("TERM", "dumb");
            Environment.SetEnvironmentVariable("COLORTERM", null);
            Environment.SetEnvironmentVariable("TERM_PROGRAM", "outer");
            Environment.SetEnvironmentVariable("NO_COLOR", "1");
            Environment.SetEnvironmentVariable("AGENTTERM_ENV_TEST", "kept-é");
            await using var session = new TerminalSession(120, 15, TestShell.Profile);
            session.Start();
            session.Send("Write-Output ('ENV_'+'CHECK:'+ $env:TERM + '|' + $env:COLORTERM + '|' + $env:TERM_PROGRAM + '|' + [bool]$env:NO_COLOR + '|' + $env:AGENTTERM_ENV_TEST)\r");
            var clock = Stopwatch.StartNew();
            const string expected = "ENV_CHECK:xterm-256color|truecolor|AgentTerm|False|kept-é";
            while (!session.ReadText().Contains(expected, StringComparison.Ordinal))
            {
                if (session.Completion.IsCompleted || clock.Elapsed > TimeSpan.FromSeconds(20))
                    throw new InvalidOperationException("Child terminal environment was not set correctly. " + session.ReadText());
                await Task.Delay(40);
            }
            if (Environment.GetEnvironmentVariable("TERM") != "dumb" || Environment.GetEnvironmentVariable("NO_COLOR") != "1")
                throw new InvalidOperationException("Starting a terminal must not change its parent environment.");
        }
        finally
        {
            for (var i = 0; i < keys.Length; i++) Environment.SetEnvironmentVariable(keys[i], saved[i]);
        }
    }
}
