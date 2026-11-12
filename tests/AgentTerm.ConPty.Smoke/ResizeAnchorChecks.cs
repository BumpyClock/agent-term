using AgentTerm.Terminal;

internal static class ResizeAnchorChecks
{
    private const int TargetPromptCells = 91;

    internal static async Task AttributionAsync()
    {
        var directory = CreateDirectory();
        try
        {
            var prompt = FixturePrompt;
            var profile = TestShell.Profile with
            {
                Arguments = "-NoLogo -NoProfile -NoExit -Command \"function global:prompt { '" + FixturePrompt + "' }; Set-PSReadLineOption -PredictionSource None -HistorySaveStyle SaveNothing\"",
                StartingDirectory = directory
            };
            await using var session = new TerminalSession(40, 30, profile);
            session.Start();
            await Wait(session, () => Flat(session).EndsWith(prompt.TrimEnd(), StringComparison.Ordinal), "plain prompt");
            await Task.Delay(250);
            session.Resize(120, 30);
            await Task.Delay(350);
            const string token = "anchor-plain";
            var echo = "echo " + token;
            var origin = prompt.Length % 40;
            Check(session, origin + echo.Length <= prompt.Length, "Fixture prompt must be long enough to contain the misplaced echo.");
            var painted = (prompt[..origin] + echo + prompt[(origin + echo.Length)..]).TrimEnd();
            session.Send(echo + "\r");
            await Wait(session, () => Text(session).Contains("\n" + token + "\n") || Text(session).EndsWith("\n" + token, StringComparison.Ordinal), "plain output");
            await Task.Delay(250);
            Check(session, Text(session).Contains(painted + "\n" + token),
                $"ATTRIBUTION NOT REPRODUCED: stock PSReadLine did not paint at modulo column {origin}. Expected row:\n{painted}");
            Console.WriteLine($"Attribution reproduced: stock PSReadLine painted the echo at column {origin} of the {prompt.Length}-cell prompt row after 40->120.");
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static async Task RunAsync()
    {
        var directory = CreateDirectory();
        try
        {
            await using var session = new TerminalSession(40, 30, TestShell.Profile with { StartingDirectory = directory });
            await TestShell.StartLegacyAsync(session);
            await Ready(session);
            var options = await session.ExecuteAsync(new("$global:__AgentTermPrompt = { '" + FixturePrompt + "' }; Set-PSReadLineOption -PredictionSource None -HistorySaveStyle SaveNothing"));
            Check(session, options.Succeeded == true, "Fixture disables predictions so rows stay exact.");
            var prompt = FixturePrompt;
            Check(session, prompt.Length is > 40 and <= 100 && prompt.Length % 40 != 0,
                $"Fixture prompt must wrap at 40 and fit at 120. It has {prompt.Length} cells.");

            session.Resize(120, 30); await Task.Delay(350);
            await Submit(session, prompt, "echo anchor-empty", "anchor-empty", 120, "Empty editor echo follows the reflowed prompt.");

            session.Resize(40, 30); await Task.Delay(350);
            await Submit(session, prompt, "echo anchor-at40", "anchor-at40", 40, "Narrowed echo follows the wrapped prompt.");
            session.Resize(72, 30);
            session.Resize(120, 30); await Task.Delay(350);
            await Submit(session, prompt, "echo anchor-double", "anchor-double", 120, "Echo after two keyless width changes follows the prompt.");

            var wrapped = new string('w', 50);
            session.Resize(40, 30); await Task.Delay(350);
            session.Send("echo " + wrapped);
            await Wait(session, () => Flat(session).EndsWith(prompt + "echo " + wrapped, StringComparison.Ordinal), "wrapped draft");
            session.Resize(120, 30); await Task.Delay(350);
            await Submit(session, prompt, "echo " + wrapped + "Z", wrapped + "Z", 120, "Wrapped draft continues at its reflowed end.", "Z");

            var middle = new string('m', 40);
            session.Resize(40, 30); await Task.Delay(350);
            session.Send("echo " + middle + "TAIL");
            await Wait(session, () => Flat(session).EndsWith(prompt + "echo " + middle + "TAIL", StringComparison.Ordinal), "mid-line draft");
            var endCursor = session.ReadFrame().Cursor;
            var targetCell = endCursor.Row * 40 + endCursor.Column - 4;
            session.Send("\u001b[D\u001b[D\u001b[D\u001b[D");
            await Wait(session, () => { var cursor = session.ReadFrame().Cursor; return cursor.Row * 40 + cursor.Column == targetCell; }, "mid-line cursor before resize");
            session.Resize(120, 30); await Task.Delay(350);
            await Submit(session, prompt, "echo " + middle + "-TAIL", middle + "-TAIL", 120, "Mid-line insertion lands at the reflowed cursor.", "-");

            session.Resize(40, 30); await Task.Delay(350);
            await Submit(session, prompt, "echo anchor-imm40", "anchor-imm40", 40, "Narrowed echo before immediate input follows the prompt.");
            session.Resize(120, 30);
            await Submit(session, prompt, "echo anchor-immediate", "anchor-immediate", 120, "Keys typed immediately after a width change land at the re-anchored origin.");

            session.Resize(40, 30); await Task.Delay(350);
            var custom = await session.ExecuteAsync(new("$global:__AgentTermPrompt = { \"ő anchor-top`n\" + 'PS-' + ('p' * 80) + '> ' }"));
            Check(session, custom.Succeeded == true, "Custom prompt installs.");
            await Ready(session);
            var customPrompt = "PS-" + new string('p', 80) + "> ";
            session.Resize(120, 30); await Task.Delay(350);
            await Submit(session, "ő anchor-top\n" + customPrompt, "echo anchor-custom", "anchor-custom", 120, "Custom wrapped prompt keeps its glyph and the echo follows it.");
            Check(session, !Text(session).Contains("? anchor-top"), "The prompt glyph is never re-emitted through a lossy encoding.");

            session.Resize(40, 30); await Task.Delay(350);
            session.Paste("echo anchor-ml-one\necho anchor-ml-two"); await Task.Delay(250);
            session.Resize(120, 30); await Task.Delay(350);
            session.Send("\r");
            await Wait(session, () => session.PromptState == ShellPromptState.Ready && Text(session).Contains("\nanchor-ml-two"), "multi-line draft");
            Console.WriteLine("CHARACTERIZATION multi-line draft, cursor on line 2, 40->120:\n" + string.Join("\n", Text(session).TrimEnd().Split('\n').TakeLast(8)));
            Console.WriteLine("Resize re-anchor: empty, keyless double, wrapped end, mid-line, immediate input and custom glyph prompt passed.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task Submit(TerminalSession session, string prompt, string command, string output, int width, string message, string? typed = null)
    {
        session.Send((typed ?? command) + "\r");
        var lastPromptLine = prompt[(prompt.LastIndexOf('\n') + 1)..];
        var head = prompt.Contains('\n') ? prompt[..(prompt.LastIndexOf('\n') + 1)] : "";
        var expected = head + Rows(lastPromptLine + command, width) + "\n" + Rows(output, width);
        await Wait(session, () => session.PromptState == ShellPromptState.Ready && Text(session).Contains(expected),
            message + " Expected:\n" + expected);
        await Ready(session);
    }

    private static string Rows(string line, int width) => string.Join("\n", line.Chunk(width).Select(row => new string(row)));

    private static string FixturePrompt => "PS " + new string('a', TargetPromptCells - 5) + "> ";

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agent-anchor-用户-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string Text(TerminalSession session) => session.ReadText().Replace("\r", "");
    private static string Flat(TerminalSession session) => Text(session).Replace("\n", "").TrimEnd();

    private static async Task Wait(TerminalSession session, Func<bool> condition, string message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { while (!condition()) await Task.Delay(25, timeout.Token); }
        catch (OperationCanceledException) { throw new Exception("Resize anchor timeout: " + message + "\n" + session.PromptState + "\n" + session.ReadText()); }
    }

    private static async Task Ready(TerminalSession session)
    {
        await Wait(session, () => session.PromptState == ShellPromptState.Ready, "readiness");
        await Task.Delay(250);
    }

    private static void Check(TerminalSession session, bool ok, string message)
    { if (!ok) throw new Exception(message + "\n" + session.ReadText()); }
}
