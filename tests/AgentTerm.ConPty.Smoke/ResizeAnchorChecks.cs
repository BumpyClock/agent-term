using AgentTerm.Terminal;

/// <summary>
/// PSReadLine 2.x recomputes its input origin as X mod width after a resize (Render.cs RecomputeInitialCoords).
/// A prompt that wrapped at the old width reflows to another column, so that origin paints the echo into the prompt.
/// Each hooked case first renders at 40 columns, where a 91-cell prompt ends at column 11, then widens to 120,
/// where it ends at column 91. Expected rows are computed, never matched loosely.
/// </summary>
internal static class ResizeAnchorChecks
{
    private const int TargetPromptCells = 91;

    /// <summary>Plain PowerShell, no AgentTerm hooks. Passes only when the upstream modulo origin reproduces.</summary>
    internal static async Task AttributionAsync()
    {
        var directory = CreateDirectory();
        try
        {
            var prompt = "PS " + directory + "> ";
            var profile = TestShell.Profile with
            {
                // Any flag besides -NoLogo and -NoProfile disables integration, so this is stock PSReadLine.
                Arguments = "-NoLogo -NoProfile -NoExit -Command Set-PSReadLineOption -PredictionSource None -HistorySaveStyle SaveNothing",
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
            var options = await session.ExecuteAsync(new("Set-PSReadLineOption -PredictionSource None -HistorySaveStyle SaveNothing"));
            Check(session, options.Succeeded == true, "Fixture disables predictions so rows stay exact.");
            var location = (await session.ExecuteAsync(new("$PWD.Path"))).Output.Replace("\r", "").Trim();
            var prompt = "PS " + location + "> ";
            Check(session, prompt.Length is > 40 and <= 100 && prompt.Length % 40 != 0,
                $"Fixture prompt must wrap at 40 and fit at 120. It has {prompt.Length} cells.");

            // Empty editor. Stock PSReadLine paints at column 11 of the unwrapped prompt.
            session.Resize(120, 30); await Task.Delay(350);
            await Submit(session, prompt, "echo anchor-empty", "anchor-empty", 120, "Empty editor echo follows the reflowed prompt.");

            // Two width changes with no key between them, after a render at 40.
            session.Resize(40, 30); await Task.Delay(350);
            await Submit(session, prompt, "echo anchor-at40", "anchor-at40", 40, "Narrowed echo follows the wrapped prompt.");
            session.Resize(72, 30);
            session.Resize(120, 30); await Task.Delay(350);
            await Submit(session, prompt, "echo anchor-double", "anchor-double", 120, "Echo after two keyless width changes follows the prompt.");

            // Wrapped single-line draft with the cursor at its end.
            var wrapped = new string('w', 50);
            session.Resize(40, 30); await Task.Delay(350);
            session.Send("echo " + wrapped);
            await Wait(session, () => Flat(session).EndsWith(prompt + "echo " + wrapped, StringComparison.Ordinal), "wrapped draft");
            session.Resize(120, 30); await Task.Delay(350);
            await Submit(session, prompt, "echo " + wrapped + "Z", wrapped + "Z", 120, "Wrapped draft continues at its reflowed end.", "Z");

            // Wrapped draft edited mid-line after the resize.
            var middle = new string('m', 40);
            session.Resize(40, 30); await Task.Delay(350);
            session.Send("echo " + middle + "TAIL");
            await Wait(session, () => Flat(session).EndsWith(prompt + "echo " + middle + "TAIL", StringComparison.Ordinal), "mid-line draft");
            session.Send("\u001b[D\u001b[D\u001b[D\u001b[D"); await Task.Delay(150);
            session.Resize(120, 30); await Task.Delay(350);
            await Submit(session, prompt, "echo " + middle + "-TAIL", middle + "-TAIL", 120, "Mid-line insertion lands at the reflowed cursor.", "-");

            // Keys sent at once after the resize. The chord precedes them in the input pipe.
            session.Resize(40, 30); await Task.Delay(350);
            await Submit(session, prompt, "echo anchor-imm40", "anchor-imm40", 40, "Narrowed echo before immediate input follows the prompt.");
            session.Resize(120, 30);
            await Submit(session, prompt, "echo anchor-immediate", "anchor-immediate", 120, "Keys typed immediately after a width change land at the re-anchored origin.");

            // Custom two-line prompt with a long last line and a glyph outside the OEM code page. It is set through the
            // integration's saved prompt, since redefining prompt would remove the integration wrapper.
            session.Resize(40, 30); await Task.Delay(350);
            var custom = await session.ExecuteAsync(new("$global:__AgentTermPrompt = { \"ő anchor-top`n\" + 'PS-' + ('p' * 80) + '> ' }"));
            Check(session, custom.Succeeded == true, "Custom prompt installs.");
            await Ready(session);
            var customPrompt = "PS-" + new string('p', 80) + "> ";
            session.Resize(120, 30); await Task.Delay(350);
            await Submit(session, "ő anchor-top\n" + customPrompt, "echo anchor-custom", "anchor-custom", 120, "Custom wrapped prompt keeps its glyph and the echo follows it.");
            Check(session, !Text(session).Contains("? anchor-top"), "The prompt glyph is never re-emitted through a lossy encoding.");

            // Characterization only. The handler abstains when the cursor is on a later logical line.
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

    /// <summary>Sends the rest of a command and requires its exact computed rows, then readiness.</summary>
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

    private static string CreateDirectory()
    {
        var root = Path.GetTempPath();
        var prefix = "agent-anchor-" + Guid.NewGuid().ToString("N")[..8] + "-";
        var padding = TargetPromptCells - "PS ".Length - "> ".Length - root.Length - prefix.Length;
        if (padding < 1) throw new Exception($"Temp path {root} is too long for a {TargetPromptCells}-cell prompt fixture.");
        var directory = Path.Combine(root, prefix + new string('a', padding));
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
