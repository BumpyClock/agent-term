using AgentTerm.Terminal;

internal static class ProfileChecks
{
    public static async Task RunAsync()
    {
        var profiles = ShellProfile.Discover();
        if (profiles.Count == 0 || profiles.Select(p => p.Id).Distinct().Count() != profiles.Count)
            throw new Exception("Shell profiles must be discoverable and have unique IDs.");
        var cmd = profiles.Single(p => p.Id == "cmd");
        await using var first = new TerminalSession(100, 12, TestShell.Profile);
        await using var second = new TerminalSession(100, 12, cmd);
        first.Start();
        second.Start();
        if (first.ProcessId == second.ProcessId || second.ShellName != "Command Prompt")
            throw new Exception("Each profile must own an independent shell.");
        first.Send("$agentTermState = 'STILL_' + 'ALIVE'; Write-Output ('FIRST_' + 'READY')\r");
        second.Send("echo %COMSPEC%\r");
        await WaitAsync(first, text => text.Contains("FIRST_READY"));
        await WaitAsync(second, text => text.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase));
        await second.DisposeAsync();
        first.Send("Clear-Host; Write-Output $agentTermState\r");
        await WaitAsync(first, text => text.Contains("STILL_ALIVE"));
        first.Send("exit\r");
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        Console.WriteLine("Profile checks passed PowerShell/Command Prompt launches and independent session disposal.");
    }

    private static async Task WaitAsync(TerminalSession session, Func<string, bool> matches)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < until)
        {
            if (matches(session.ReadText())) return;
            await Task.Delay(40);
        }
        throw new TimeoutException("Profile fixture output did not arrive. " + session.ReadText());
    }
}
