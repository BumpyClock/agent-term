using AgentTerm.Terminal;

internal static class TestShell
{
    // UI sessions load the user's profile. Fixtures use a predictable prompt and startup state.
    public static ShellProfile Profile { get; } = ShellProfile.Discover()
        .First(profile => profile.Id is "pwsh" or "powershell") with { Arguments = "-NoLogo -NoProfile" };
}
