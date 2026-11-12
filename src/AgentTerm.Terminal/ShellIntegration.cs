using System.Text;

namespace AgentTerm.Terminal;

internal static class ShellIntegration
{
    internal static bool SupportsIntegration(ShellProfile profile)
    {
        var executable = Path.GetFileNameWithoutExtension(profile.Executable);
        var flags = profile.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (executable.Equals("pwsh", StringComparison.OrdinalIgnoreCase) || executable.Equals("powershell", StringComparison.OrdinalIgnoreCase)) &&
            flags.All(flag => flag.Equals("-NoLogo", StringComparison.OrdinalIgnoreCase) || flag.Equals("-NoProfile", StringComparison.OrdinalIgnoreCase));
    }

    internal static string Arguments(ShellProfile profile, string token, string commandPath)
    {
        var executable = Path.GetFileNameWithoutExtension(profile.Executable);
        var flags = profile.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (SupportsIntegration(profile))
        {
            // Inline startup leaves execution policy and the user's profile unchanged.
            // Ctrl+Shift+F12 re-anchors PSReadLine after a width change. PSReadLine 2.x recomputes its input origin as
            // X mod width (Render.cs RecomputeInitialCoords), which is wrong once the terminal reflows a wrapped prompt.
            // The handler derives the origin from the reflowed cursor, validates it with PSReadLine's own geometry, and
            // abstains when the cursor is on a later logical line or parked past an exactly full row. It never reruns or
            // rewrites the prompt. Keep script comments out: the encoded script shares CreateProcess's 32767-char limit.
            var script = """
                if ($ExecutionContext.SessionState.LanguageMode -eq 'FullLanguage') {
                    $global:__AgentTermPrompt = $function:prompt
                    $global:__AgentTermReadLine = $function:PSConsoleHostReadLine
                    $global:__AgentTermDraft = ''
                    $global:__AgentTermDraftCursor = 0
                    $global:__AgentTermPromptRows = 1
                    $global:__AgentTermCommandPath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('COMMAND_PATH'))
                    function global:__AgentTermReport($value) { [Console]::Write(([char]27 + "]777;TOKEN;" + $value + [char]7)) }
                    $editorCommand = Get-Command Set-PSReadLineKeyHandler -ErrorAction Ignore
                    if (-not $global:__AgentTermReadLine) { $global:__AgentTermReadLine = $function:PSConsoleHostReadLine }
                    if ($editorCommand -and $global:__AgentTermReadLine) {
                        function global:PSConsoleHostReadLine {
                            $line = & $global:__AgentTermReadLine
                            $enter = Get-PSReadLineKeyHandler -Chord Enter
                            if ($enter.Function -ne 'AgentTermAcceptLine') { __AgentTermReport 'entry;0' }
                            if (-not [string]::IsNullOrWhiteSpace($line) -and -not $global:__AgentTermCommandId) { __AgentTermReport 'start' }
                            return $line
                        }
                        function global:__AgentTermBindMailbox($chord, $handler) {
                            if ((Get-PSReadLineOption).EditMode -eq 'Vi') {
                                Set-PSReadLineKeyHandler -Chord $chord -ViMode Insert -ScriptBlock $handler
                                Set-PSReadLineKeyHandler -Chord $chord -ViMode Command -ScriptBlock $handler
                            } else { Set-PSReadLineKeyHandler -Chord $chord -ScriptBlock $handler }
                        }
                        __AgentTermBindMailbox 'Ctrl+F12' {
                            if (-not [IO.File]::Exists($global:__AgentTermCommandPath)) { return }
                            $line = ''; $cursor = 0
                            [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref]$line, [ref]$cursor)
                            $global:__AgentTermDraft = $line
                            $global:__AgentTermDraftCursor = $cursor
                            $mail = [IO.File]::ReadAllText($global:__AgentTermCommandPath, [Text.Encoding]::UTF8)
                            [IO.File]::Delete($global:__AgentTermCommandPath)
                            $split = $mail.IndexOf("`n")
                            $global:__AgentTermCommandId = $mail.Substring(0, $split)
                            $global:__AgentTermRestoreId = $global:__AgentTermCommandId
                            if ($line.Length -gt 0) { __AgentTermReport ('draft;' + $global:__AgentTermRestoreId) }
                            $command = $mail.Substring($split + 1)
                            if ($global:__AgentTermCommandId.StartsWith('c:')) {
                                [Microsoft.PowerShell.PSConsoleReadLine]::Replace(0, $line.Length, '')
                                [Console]::Write(([char]27 + '[J'))
                                # Erase only the prompt and editor, never prior user output.
                                for ($row = 1; $row -lt $global:__AgentTermPromptRows; $row++) { [Console]::Write(([char]27 + '[1A' + [char]13 + [char]27 + '[2K')) }
                                [Console]::Write(([char]13 + [char]27 + '[J'))
                                if ($global:__AgentTermPromptRows -gt 1) { [Console]::Write(([char]27 + '[' + ($global:__AgentTermPromptRows - 1) + 'B')) }
                                [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()
                                __AgentTermReport ('start;' + $global:__AgentTermCommandId)
                                return
                            }
                            $parseTokens = $null; $parseErrors = $null
                            $null = [Management.Automation.Language.Parser]::ParseInput($command, [ref]$parseTokens, [ref]$parseErrors)
                            if ($parseErrors.Count -gt 0) {
                                $diagnostic = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($parseErrors | Out-String)))
                                $command = "Write-Error ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + $diagnostic + "')))"
                            }
                            [Microsoft.PowerShell.PSConsoleReadLine]::Replace(0, $line.Length, $command)
                            [Console]::Write(([char]27 + '[J'))
                            [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()
                            if ($global:__AgentTermCommandId.StartsWith('a:')) { [Console]::WriteLine(('Command started [' + $global:__AgentTermCommandId + ']')) }
                            __AgentTermReport ('start;' + $global:__AgentTermCommandId)
                        }
                        __AgentTermBindMailbox 'Alt+Shift+F12' {
                            if ($global:__AgentTermDraft.Length -gt 0) { [Microsoft.PowerShell.PSConsoleReadLine]::Insert($global:__AgentTermDraft) }
                            [Microsoft.PowerShell.PSConsoleReadLine]::SetCursorPosition($global:__AgentTermDraftCursor)
                            $global:__AgentTermDraft = ''
                            $global:__AgentTermDraftCursor = 0
                            __AgentTermReport ('restored;' + $global:__AgentTermRestoreId)
                            $global:__AgentTermRestoreId = $null
                        }
                        __AgentTermBindMailbox 'Ctrl+Shift+F12' {
                            $w = [Console]::BufferWidth; $r = 'same'; $fx = $null
                            try {
                                $t = [Microsoft.PowerShell.PSConsoleReadLine]; $f = [Reflection.BindingFlags]'NonPublic,Instance'
                                $s = $t.GetField('_singleton', [Reflection.BindingFlags]'NonPublic,Static').GetValue($null)
                                $p = $t.GetField('_previousRender', $f).GetValue($s); $g = $p.GetType(); $ow = $g.GetField('bufferWidth').GetValue($p)
                                if ($ow -ne $w) {
                                    $r = 'skip'; $line = ''; $cur = 0
                                    [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref]$line, [ref]$cur)
                                    $fx = $t.GetField('_initialX', $f); $fy = $t.GetField('_initialY', $f); $ox = $fx.GetValue($s); $oy = $fy.GetValue($s)
                                    $m = $t.GetMethod('ConvertOffsetToPoint', $f); $a = @($cur) + @($null) * ($m.GetParameters().Count - 1)
                                    $px = $m.ReturnType.GetField('X'); $py = $m.ReturnType.GetField('Y')
                                    $left = [Console]::CursorLeft; $top = [Console]::CursorTop
                                    if (-not $line.Substring(0, $cur).Contains("`n")) {
                                        $fx.SetValue($s, 0); $fy.SetValue($s, 0); $q = $m.Invoke($s, $a); $d = $py.GetValue($q) * $w + $px.GetValue($q)
                                        $fx.SetValue($s, (($left - $d) % $w + $w) % $w); $q = $m.Invoke($s, $a); $y = $top - $py.GetValue($q)
                                        if ($px.GetValue($q) -eq $left -and ($cur -eq 0 -or ($ox + $d) % $ow -ne 0)) {
                                            $fy.SetValue($s, $y); $r = 'ok'
                                            foreach ($n in @{ bufferWidth = $w; bufferHeight = [Console]::BufferHeight; cursorLeft = $left; cursorTop = $top; initialY = $y }.GetEnumerator()) {
                                                $h = $g.GetField($n.Key); if ($h) { $h.SetValue($p, $n.Value) }
                                            }
                                        }
                                    }
                                    if ($r -ne 'ok') { $fx.SetValue($s, $ox); $fy.SetValue($s, $oy) }
                                }
                            } catch { try { $fx.SetValue($s, $ox); $fy.SetValue($s, $oy) } catch { }; $r = 'error' }
                            __AgentTermReport ('anchor;' + $w + ';' + $r)
                        }
                        $global:__AgentTermEnter = Get-PSReadLineKeyHandler -Chord Enter
                        if ($global:__AgentTermEnter.Group -ne 'Custom' -and $global:__AgentTermEnter.Function -eq 'AcceptLine') {
                            Set-PSReadLineKeyHandler -Chord Enter -BriefDescription AgentTermAcceptLine -ScriptBlock {
                                param($key, $arg)
                                $line = ''; $cursor = 0
                                [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref]$line, [ref]$cursor)
                                if ($line -cmatch '^/agent(?:\s|$)') {
                                    [Microsoft.PowerShell.PSConsoleReadLine]::AddToHistory($line)
                                    [Microsoft.PowerShell.PSConsoleReadLine]::Replace(0, $line.Length, '')
                                    [Console]::Write(([char]27 + '[J'))
                                    __AgentTermReport ('agent;' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($line.Substring(6).Trim())))
                                    return
                                }
                                [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine($key, $arg)
                            }
                            __AgentTermReport 'entry;1'
                        } else { __AgentTermReport 'entry;0' }
                        $global:__AgentTermEditor = $true
                    } else { __AgentTermReport 'unavailable' }
                    function global:prompt {
                        $ok = $global:?
                        $code = $global:LASTEXITCODE
                        $id = $global:__AgentTermCommandId
                        $global:__AgentTermCommandId = $null
                        $location = if ($pwd.Provider.Name -eq 'FileSystem') {
                            'file://localhost/' + (($pwd.ProviderPath.Replace('\', '/').Split('/') | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/')
                        } else { '' }
                        [Console]::Write(([char]27 + ']7;' + $location + [char]7))
                        if ($id -and $id.StartsWith('a:')) { [Console]::WriteLine(('Command ended [' + $id + ']')) }
                        if ($id -and $id.StartsWith('c:')) { [Console]::WriteLine() }
                        __AgentTermReport ('end;' + [int]$ok + ';' + $code + ';' + $id)
                        if ($global:__AgentTermEditor) { __AgentTermReport ('ready;' + $id) }
                        if (-not $ok) { Write-Error 'restore previous command status' -ErrorAction Ignore }
                        $result = if ($null -ne $global:__AgentTermPrompt) { & $global:__AgentTermPrompt } else { "PS $pwd> " }
                        $global:__AgentTermPromptRows = 1 + ([regex]::Matches(($result -join ''), "`n")).Count
                        $global:LASTEXITCODE = $code
                        $result
                    }
                } else { [Console]::Write(([char]27 + "]777;TOKEN;unavailable" + [char]7)) }
                """;
            return profile.Arguments + " -NoExit -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(
                script.Replace("TOKEN", token).Replace("COMMAND_PATH", Convert.ToBase64String(Encoding.UTF8.GetBytes(commandPath)))));
        }
        if (executable.Equals("cmd", StringComparison.OrdinalIgnoreCase) &&
            flags.All(flag => flag.Equals("/D", StringComparison.OrdinalIgnoreCase) || flag.Equals("/Q", StringComparison.OrdinalIgnoreCase)))
            return profile.Arguments + " /K \"prompt $E]7;$P$E\\$P$G\"";
        return profile.Arguments;
    }
}
