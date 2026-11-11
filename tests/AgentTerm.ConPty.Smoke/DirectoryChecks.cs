using AgentTerm.Terminal;

internal static class DirectoryChecks
{
    internal static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentterm-directory-" + Guid.NewGuid().ToString("N"), "space é # %20");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var profile in new[] { TestShell.Profile, ShellProfile.Discover().First(profile => profile.Id is "pwsh" or "powershell"), ShellProfile.Discover().Single(profile => profile.Id == "cmd") })
            {
                await using var session = new TerminalSession(120, 20, profile);
                session.Start();
                await WaitAsync(session, value => value is not null);
                session.Send(profile.Id == "cmd" ? $"cd /d \"{directory}\"\r" : $"Set-Location -LiteralPath '{directory.Replace("'", "''")}'\r");
                await WaitAsync(session, value => value?.Path.Equals(directory, StringComparison.OrdinalIgnoreCase) == true);
                if (profile.Id != "cmd")
                {
                    session.Send("Set-Location HKCU:\\\r");
                    await WaitAsync(session, value => value is null);
                }
            }
        }
        finally { Directory.Delete(Path.GetDirectoryName(directory)!, recursive: true); }
        Console.WriteLine("Directory checks passed PowerShell and cmd prompt reports, spaces, Unicode, and non-filesystem clearing.");
    }

    private static async Task WaitAsync(TerminalSession session, Func<ShellDirectory?, bool> expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (!expected(session.ReadWorkingDirectory())) await Task.Delay(40, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new Exception($"Directory report did not arrive. Actual {session.ReadWorkingDirectory()}. Screen {session.ReadText()}");
        }
    }
}
