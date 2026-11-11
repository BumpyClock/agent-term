using System.Text;

namespace AgentTerm.Terminal;

internal static class ShellIntegration
{
    internal static string Arguments(ShellProfile profile)
    {
        var executable = Path.GetFileNameWithoutExtension(profile.Executable);
        var flags = profile.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if ((executable.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
             executable.Equals("powershell", StringComparison.OrdinalIgnoreCase)) &&
            flags.All(flag => flag.Equals("-NoLogo", StringComparison.OrdinalIgnoreCase) ||
                              flag.Equals("-NoProfile", StringComparison.OrdinalIgnoreCase)))
        {
            // Inline startup leaves execution policy and the user's profile unchanged.
            const string script = """
                if ($ExecutionContext.SessionState.LanguageMode -eq 'FullLanguage') {
                    $global:__AgentTermPrompt = $function:prompt
                    function global:prompt {
                        $ok = $global:?
                        $code = $global:LASTEXITCODE
                        $location = if ($pwd.Provider.Name -eq 'FileSystem') {
                            'file://localhost/' + (($pwd.ProviderPath.Replace('\', '/').Split('/') | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/')
                        } else { '' }
                        [Console]::Write(([char]27 + ']7;' + $location + [char]7))
                        if (-not $ok) { Write-Error 'restore previous command status' -ErrorAction Ignore }
                        $result = if ($null -ne $global:__AgentTermPrompt) { & $global:__AgentTermPrompt } else { "PS $pwd> " }
                        $global:LASTEXITCODE = $code
                        $result
                    }
                }
                """;
            return profile.Arguments + " -NoExit -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        }
        if (executable.Equals("cmd", StringComparison.OrdinalIgnoreCase) &&
            flags.All(flag => flag.Equals("/D", StringComparison.OrdinalIgnoreCase) || flag.Equals("/Q", StringComparison.OrdinalIgnoreCase)))
            return profile.Arguments + " /K \"prompt $E]7;$P$E\\$P$G\"";
        return profile.Arguments;
    }
}
