using AgentTerm.Agents;
using AgentTerm.Terminal;

internal static class ResizeInputChecks
{
    internal static async Task RunAsync()
    {
        await using var session = new TerminalSession(120, 30, TestShell.Profile);
        session.Start();
        async Task Ready()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try { while (session.PromptState != ShellPromptState.Ready) await Task.Delay(25, timeout.Token); }
            catch (OperationCanceledException) { throw new Exception("Resize input timeout.\n" + session.ReadText()); }
        }
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message + "\n" + session.ReadText());
        }
        string Text() => session.ReadText().Replace("\r", "");

        await Ready();
        var longCommand = "# " + new string('x', 6000) + "\nWrite-Output ('long-' + 'transport-done')";
        session.SubmitCommand(longCommand);
        await Ready();
        Check(Text().Contains("long-transport-done"), "Long multiline input must execute.");
        var history = new System.Text.StringBuilder();
        var totalRows = session.ReadFrame().Viewport.TotalRows;
        for (long row = 0; row < totalRows; row += 30)
        {
            session.ScrollTo(row);
            var frame = session.ReadFrame();
            foreach (var cell in frame.Cells) history.Append(cell.Text);
        }
        session.ScrollToBottom();
        Check(history.ToString().Contains(new string('x', 120)), "Long command echo must remain in scrollback.");
        Check(!history.ToString().Contains(string.Concat(Enumerable.Repeat("eHh4", 20))), "Encoded transport must not enter terminal history.");
        session.SubmitCommand("echo resize-input-short");
        await Ready();
        session.SubmitCommand("Write-Output ('blank-' + 'before'); Write-Output ''; Write-Output ''; Write-Output ('blank-' + 'after')");
        await Ready();
        void CheckHistory()
        {
            var text = Text();
            var unwrapped = text.Contains("echo resize-input-short\nresize-input-short");
            var wrapped = text.Contains("echo resize-in\nput-short\nresize-input-short");
            Check(unwrapped || wrapped, "Short command and output must remain adjacent after long transport, including its 40-column wrap.");
            Check(Text().Contains("blank-before\n\n\nblank-after"), "Two intentional blank output rows must survive unchanged.");
        }
        CheckHistory();
        foreach (var width in new ushort[] { 72, 40, 120 })
        {
            session.Resize(width, 30);
            CheckHistory();
            await Task.Delay(350);
            CheckHistory();
        }

        var unicode = await session.ExecuteAsync(new("using namespace System.Collections.Generic\n$value = [List[string]]::new()\n$value.Add('界🦉 café')\n$value[0]\n'capture-before'; ''; ''; 'capture-after'"));
        Check(unicode.Status == TerminalExecutionStatus.Completed && unicode.Succeeded == true, "Unicode multiline input with a first-statement directive must complete.");
        var output = unicode.Output.Replace("\r", "");
        Check(output.Contains("界🦉 café") && output.Contains("capture-before\n\n\ncapture-after"), "Capture must preserve Unicode and intentional interior blank rows.");
        Check(!output.Contains("using namespace") && !output.Contains("Agent command ["), "Capture markers must exclude command echo and bookkeeping.");

        session.SubmitCommand("Write-Output ('history-' + (++$global:resizeInputHistory))");
        await Ready();
        session.Send("\u001b[A\r");
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
            while (!Text().Contains("history-2") || session.PromptState != ShellPromptState.Ready) await Task.Delay(25, timeout.Token);
        Check(!Text().Contains("not recognized"), "Legacy history must recall the real command, not its transport.");
        Console.WriteLine("Long transport, immediate/delayed resize adjacency, blank output, Unicode/multiline capture and Legacy history passed.");
    }
}
