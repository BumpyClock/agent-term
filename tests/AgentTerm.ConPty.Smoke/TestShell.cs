using AgentTerm.Terminal;

internal static class TestShell
{
    public static ShellProfile Profile { get; } = ShellProfile.Discover()
        .First(profile => profile.Id is "pwsh" or "powershell") with { Arguments = "-NoLogo -NoProfile" };
}
