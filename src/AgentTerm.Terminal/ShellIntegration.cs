using System.Text;

namespace AgentTerm.Terminal;

internal static class ShellIntegration
{
    internal static string Arguments(ShellProfile profile, string? token = null)
    {
        var executable = Path.GetFileNameWithoutExtension(profile.Executable);
        var flags = profile.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if ((executable.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
             executable.Equals("powershell", StringComparison.OrdinalIgnoreCase)) &&
            flags.All(flag => flag.Equals("-NoLogo", StringComparison.OrdinalIgnoreCase) ||
                              flag.Equals("-NoProfile", StringComparison.OrdinalIgnoreCase)))
        {
            // Inline startup leaves execution policy and the user's profile unchanged.
            var script = """
                if ($ExecutionContext.SessionState.LanguageMode -eq 'FullLanguage') {
                    $global:__AgentTermPrompt = $function:prompt
                    $global:__AgentTermManaged = $false
                    function global:__AgentTermReport($value) { [Console]::Write(([char]27 + "]777;TOKEN;" + $value + [char]7)) }
                    function global:__AgentTermStart {
                        if ($global:__AgentTermCommandId.StartsWith('a:')) { [Console]::WriteLine(('Agent command [' + $global:__AgentTermCommandId + ']')) }
                        __AgentTermReport ('start;' + $global:__AgentTermCommandId)
                    }
                    if (Get-Command Set-PSReadLineKeyHandler -ErrorAction Ignore) {
                        Set-PSReadLineKeyHandler -Chord Ctrl+F12 -ScriptBlock {
                            $line = ''; $cursor = 0
                            [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref]$line, [ref]$cursor)
                            try { $command = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($line)) }
                            catch { return }
                            $split = $command.IndexOf("`n")
                            $global:__AgentTermCommandId = $command.Substring(0, $split)
                            $command = $command.Substring($split + 1)
                            if ($global:__AgentTermCommandId.StartsWith('c:')) { [Microsoft.PowerShell.PSConsoleReadLine]::Replace(0, $line.Length, ''); [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine(); return }
                            $parseTokens = $null; $parseErrors = $null
                            $null = [Management.Automation.Language.Parser]::ParseInput(("__AgentTermStart; " + $command), [ref]$parseTokens, [ref]$parseErrors)
                            if ($parseErrors.Count -gt 0) {
                                $diagnostic = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($parseErrors | Out-String)))
                                $command = "Write-Error ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + $diagnostic + "')))"
                            }
                            [Microsoft.PowerShell.PSConsoleReadLine]::Replace(0, $line.Length, "__AgentTermStart; " + $command)
                            [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()
                        }
                        $global:__AgentTermEditor = $true
                    }
                    function global:prompt {
                        $ok = $global:?
                        $code = $global:LASTEXITCODE
                        $location = if ($pwd.Provider.Name -eq 'FileSystem') {
                            'file://localhost/' + (($pwd.ProviderPath.Replace('\', '/').Split('/') | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/')
                        } else { '' }
                        [Console]::Write(([char]27 + ']7;' + $location + [char]7))
                        if ($global:__AgentTermManaged) {
                            if ($global:__AgentTermCommandId -and $global:__AgentTermCommandId.StartsWith('a:')) { [Console]::WriteLine(('Agent completed [' + $global:__AgentTermCommandId + ']')) }
                            if ($global:__AgentTermCommandId -and $global:__AgentTermCommandId.StartsWith('c:')) { [Console]::WriteLine() }
                            __AgentTermReport ('end;' + [int]$ok + ';' + $code + ';' + $global:__AgentTermCommandId)
                            $names = (Get-Command -CommandType Alias,Function,Cmdlet,Application -ErrorAction Ignore | Select-Object -ExpandProperty Name -Unique) -join "`n"
                            __AgentTermReport ('commands;' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($names)))
                            __AgentTermReport ('ready;' + $global:__AgentTermCommandId)
                            $global:LASTEXITCODE = $code
                            return ''
                        }
                        if ($global:__AgentTermEditor) { __AgentTermReport 'raw' }
                        if (-not $ok) { Write-Error 'restore previous command status' -ErrorAction Ignore }
                        $result = if ($null -ne $global:__AgentTermPrompt) { & $global:__AgentTermPrompt } else { "PS $pwd> " }
                        $global:LASTEXITCODE = $code
                        $result
                    }
                }
                """;
            return profile.Arguments + " -NoExit -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script.Replace("TOKEN", token ?? "raw")));
        }
        if (executable.Equals("cmd", StringComparison.OrdinalIgnoreCase) &&
            flags.All(flag => flag.Equals("/D", StringComparison.OrdinalIgnoreCase) || flag.Equals("/Q", StringComparison.OrdinalIgnoreCase)))
            return profile.Arguments + " /K \"prompt $E]7;$P$E\\$P$G\"";
        return profile.Arguments;
    }
}
