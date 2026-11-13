using AgentTerm.Terminal;

internal static class TitleChecks
{
    internal static async Task RunAsync()
    {
        await using var session = new TerminalSession(120, 20, TestShell.Profile);
        session.Start();
        await WaitAsync(session, () => session.PromptState == ShellPromptState.Ready, "PowerShell should reach its prompt.");
        if (session.Title != "") throw new Exception($"The prompt should clear the startup console title, got <{session.Title}>.");

        session.Send("[Console]::Write([char]27 + ']2;Agent é title' + [char]7); Start-Sleep -Seconds 2\r");
        await WaitAsync(session, () => session.Title == "Agent é title", "A running program's OSC 2 title should reach the session.");
        await WaitAsync(session, () => session.Title == "", "The next prompt should drop the program's title.");

        session.Send("Write-Host -NoNewline ([char]27 + ']0;Prompt title' + [char]7)\r");
        await Task.Delay(1500);
        if (session.Title != "")
            throw new Exception($"A title set before the prompt marker should clear at that prompt, got <{session.Title}>.");
        Console.WriteLine("Title checks passed OSC 0/2 titles through ConPTY, UTF-8, and prompt clearing.");
    }

    private static async Task WaitAsync(TerminalSession session, Func<bool> condition, string message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (!condition()) await Task.Delay(40, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new Exception($"{message} Title <{session.Title}>. Screen {session.ReadText()}");
        }
    }
}
