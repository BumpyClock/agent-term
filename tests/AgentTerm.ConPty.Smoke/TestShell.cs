using AgentTerm.Terminal;

internal static class TestShell
{
    public static async Task StartLegacyAsync(TerminalSession session)
    {
        session.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (session.PromptState != ShellPromptState.Ready) await Task.Delay(25, timeout.Token);
    }

    public static ShellProfile Profile { get; } = ShellProfile.Discover()
        .First(profile => profile.Id is "pwsh" or "powershell") with { Arguments = "-NoLogo -NoProfile" };
}
