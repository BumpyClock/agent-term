using AgentTerm.Terminal;
using AgentTerm.Agents;

internal static class PromptModeChecks
{
    internal static async Task ProbeAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agent-transition-界-🙂-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { await ProbeDirectoryAsync(directory); }
        finally { Directory.Delete(directory, true); }
    }
    private static async Task ProbeDirectoryAsync(string directory)
    {
        await using var session = new TerminalSession(120, 30, TestShell.Profile with { StartingDirectory = directory });
        await TestShell.StartLegacyAsync(session);
        await Ready(session);
        var prompt = "PS " + new string('p', 85) + "> ";
        var setup = await session.ExecuteAsync(new("$global:__AgentTermPrompt = { '" + prompt + "' }; Set-PSReadLineOption -PredictionSource None -HistorySaveStyle SaveNothing"));
        Check(session, setup.Succeeded == true, "Transition fixture installs a deterministic ASCII prompt in a Unicode directory.");
        session.Send("echo transition-probe\r");
        await Wait(session, () => session.PromptState == ShellPromptState.Ready && session.ReadText().Replace("\r", "").Contains("echo transition-probe\ntransition-probe"));
        foreach (var width in new ushort[] { 72, 40, 120 })
        {
            session.Resize(width, 30);
            var marker = "resize-ready-" + width;
            session.Send("echo " + marker + "\r");
            await Wait(session, () => session.PromptState == ShellPromptState.Ready && session.ReadText().Contains("\n" + marker));
            var echo = prompt + "echo transition-probe";
            var rows = string.Join("\n", Enumerable.Range(0, (echo.Length + width - 1) / width)
                .Select(i => echo.Substring(i * width, Math.Min(width, echo.Length - i * width))));
            var screen = session.ReadText().Replace("\r", "");
            Check(session, screen.Contains(rows + "\ntransition-probe"), "Native echo and output stay adjacent after resize with an ASCII cell-width fixture.");
        }
    }
    private static async Task Wait(TerminalSession session, Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { while (!condition()) await Task.Delay(25, timeout.Token); }
        catch (OperationCanceledException) { throw new Exception("Native prompt timeout. " + session.PromptState + "\n" + session.ReadText()); }
    }
    private static async Task Ready(TerminalSession session)
    {
        await Wait(session, () => session.PromptState == ShellPromptState.Ready);
        await Task.Delay(250);
    }
    private static void Check(TerminalSession session, bool ok, string message)
    { if (!ok) throw new Exception(message + "\n" + session.ReadText()); }
    internal static async Task RunAsync()
    {
        await using var session = new TerminalSession(120, 30, TestShell.Profile);
        session.Start(); await Ready(session);
        Check(session, session.ReadText().Contains("PS ") && session.AgentEntryAvailable, "Native prompt and stock Enter entry are available at startup.");
        var pid = session.ProcessId;
        await session.ExecuteAsync(new("Set-PSReadLineOption -PredictionSource None"));
        session.Send("Get-ChildIt\t");
        await Wait(session, () => session.ReadText().Contains("Get-ChildItem"));
        session.Send("\u0003");
        await Wait(session, () => session.ReadText().Contains("Get-ChildItem^C"));
        await Ready(session);
        var fixture = Path.Combine(Path.GetTempPath(), "agent-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(fixture, "completion-directory"));
        try
        {
            await session.ExecuteAsync(new("Set-Location -LiteralPath '" + fixture.Replace("'", "''") + "'"));
            session.Send("Get-Item ./completion-dir\t");
            await Wait(session, () => session.ReadText().Replace("\r", "").Replace("\n", "").Contains("completion-directory"));
            session.Send(" | ForEach-Object { 'COMPLETION_OK:' + $_.Name.Length }\r");
            await Wait(session, () => session.ReadText().Contains("COMPLETION_OK:20") && session.PromptState == ShellPromptState.Ready);
            session.Send("Write-Output ('history-' + (++$global:nativeHistory))\r");
            await Wait(session, () => session.ReadText().Contains("history-1") && session.PromptState == ShellPromptState.Ready);
            session.Send("\u001b[A\r");
            await Wait(session, () => session.ReadText().Contains("history-2") && session.PromptState == ShellPromptState.Ready);
            session.Send("if ($true) {\r"); await Task.Delay(350);
            Check(session, session.PromptState == ShellPromptState.Ready, "Incomplete input is editing, not executing.");
            session.Send("\u0003"); await Ready(session);
            var failed = await session.ExecuteAsync(new("cmd /c exit 7"));
            Check(session, failed.Succeeded == false, "Fixture failure is captured.");
            session.Send("\u000c"); await Task.Delay(350);
            Check(session, session.LastCommandSucceeded == false, "Prompt redraw retains failure.");
            session.Send("/agent it's broken\r");
            string? request = null;
            await Wait(session, () => (request ??= session.TakeAgentRequest()) is not null);
            Check(session, request == "it's broken" && session.TakeAgentRequest() is null, "Apostrophe request routes once without evaluation.");
            session.Send("\u001b[A"); await Task.Delay(250);
            Check(session, session.ReadText().Contains("/agent it's broken"), "Agent request enters native history.");
            session.Send("\u0003"); await Ready(session);
            foreach (var reserve in new[] { false, true })
            {
                var draftPrefix = reserve ? "reserve-draft-" : "tool-draft-";
                var reset = await session.ExecuteAsync(new("$draftValue = 0"));
                Check(session, reset.Succeeded == true, "Each draft branch starts with a reset variable.");
                session.Paste("$draftValue = 7\nWrite-Output ($draftValue.ToString() + ':" + draftPrefix + "AC')"); await Task.Delay(250);
                session.Send("\u001b[D\u001b[D\u001b[D"); await Task.Delay(150);
                if (reserve)
                {
                    var anchor = session.ReserveRows();
                    Check(session, anchor is not null, "Reservation accepts an unsent multiline draft.");
                    await Ready(session); session.ReleaseAnchor(anchor!);
                }
                else
                {
                    var result = await session.ExecuteAsync(new("Write-Output ('tool-' + 'result')"));
                    Check(session, result.Status == TerminalExecutionStatus.Completed && !session.AgentOwnsTerminal, "Tool restores draft before releasing input.");
                }
                session.Send("B\r");
                await Wait(session, () => session.ReadText().Contains("7:" + draftPrefix + "ABC") && session.PromptState == ShellPromptState.Ready);
                var restoredValue = await session.ExecuteAsync(new("Write-Output ('DRAFT_STATE:' + $draftValue)"));
                Check(session, restoredValue.Succeeded == true && restoredValue.Output.Contains("DRAFT_STATE:7"), "The restored draft executes both its assignment and cursor-edited output.");
            }
            session.Send("echo human-after-agent\r");
            await Wait(session, () => session.ReadText().Replace("\r", "").Contains("echo human-after-agent\nhuman-after-agent") && session.PromptState == ShellPromptState.Ready);
            var humanOutput = session.ReadText();
            Check(session, !humanOutput.Contains("Agent completed ["), "Shell command completion does not invent a successful agent-turn outcome.");
            foreach (var width in new ushort[] { 72, 40, 120 })
            {
                var nativeToken = "flow" + width;
                var replacementToken = "draft" + width;
                session.Send("/agent " + new string('x', 240) + "\r");
                request = null;
                await Wait(session, () => (request ??= session.TakeAgentRequest()) is not null);
                session.Resize(width, 30); await Task.Delay(350);
                session.Send("echo " + nativeToken + "\r");
                await Wait(session, () => session.ReadText().Replace("\r", "").Contains("> echo " + nativeToken + "\n" + nativeToken) && session.PromptState == ShellPromptState.Ready);
                session.Send("echo " + new string('d', 360)); await Task.Delay(250);
                var replacement = await session.ExecuteAsync(new("echo " + replacementToken));
                Check(session, replacement.Status == TerminalExecutionStatus.Completed && replacement.Succeeded == true && replacement.Output.Contains(replacementToken), "This width's draft replacement executes successfully.");
                session.Send("\u0003"); await Ready(session);
                session.Resize(width, 30); await Task.Delay(350);
                Check(session, System.Text.RegularExpressions.Regex.IsMatch(session.ReadText().Replace("\r", ""), "> echo " + replacementToken + @"\nCommand started \[a:[0-9a-f]{8}\]\n" + replacementToken + @"\nCommand ended \[a:[0-9a-f]{8}\]"), "This width's replacement keeps exact command, capture boundaries and output adjacent without padding rows.");
                Check(session, session.ReadText().Replace("\r", "").Contains("> echo " + nativeToken + "\n" + nativeToken), "This width's wrapped agent line cleanup does not add reflow padding.");
            }
            Check(session, session.ProcessId == pid, "Native editing retains shell identity.");
            await session.ExecuteAsync(new("Set-Location $env:TEMP"));
        }
        finally { await session.StopAsync(); Directory.Delete(fixture, true); }
    }
}
