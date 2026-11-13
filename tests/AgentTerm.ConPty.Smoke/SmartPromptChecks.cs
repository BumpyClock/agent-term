using AgentTerm.Terminal;
using AgentTerm.Agents;
internal static class SmartPromptChecks
{
    public static async Task RunAsync()
    {
        await using var session = new TerminalSession(100, 35); session.Start();
        async Task Wait(Func<bool> condition) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25)); while (!condition()) await Task.Delay(25, timeout.Token); }
        await Wait(() => session.PromptState == ShellPromptState.Ready);
        var boundaries = await session.ExecuteAsync(new("Write-Output ('capture-' + 'first'); Write-Output ''; Write-Output ''; Write-Output ('capture-' + 'last')")).WaitAsync(TimeSpan.FromSeconds(25));
        if (boundaries.Status != TerminalExecutionStatus.Completed || boundaries.Succeeded != true || boundaries.Output.Replace("\r", "") != "capture-first\n\n\ncapture-last\n")
            throw new Exception("Protocol capture must exclude echo and prompt without losing first/final output or blank rows: " + System.Text.Json.JsonSerializer.Serialize(boundaries));
        session.SubmitCommand("$smartValue = 41; $env:AGENT_TERM_HOST_FIXTURE = 'persisted-env'\nfunction SmartAnswer { $smartValue + 1 }; Set-Alias smartAlias SmartAnswer"); await Wait(() => session.PromptState == ShellPromptState.Ready);
        if (!(await session.ExecuteAsync(new("smartAlias"))).Output.Contains("42")) throw new Exception("Native alias must execute in the same shell.");
        var fixture = Path.Combine(Path.GetTempPath(), "agent-term-prompt-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        try
        {
            session.SubmitCommand("Set-Location -LiteralPath '" + fixture.Replace("'", "''") + "'"); await Wait(() => session.PromptState == ShellPromptState.Ready);
            Console.WriteLine("Host fixture ready.");
            var pid = session.ProcessId;
            var captured = await session.ExecuteAsync(new("smartAlias; Write-Output $env:AGENT_TERM_HOST_FIXTURE; 1..200 | ForEach-Object { Write-Output ('capture-line-' + $_) }; Write-Output '終わり🦉'"));
            if (captured.Status != TerminalExecutionStatus.Completed || captured.Succeeded != true || captured.CurrentDirectory != fixture || !captured.Output.Contains("42") || !captured.Output.Contains("persisted-env") || !captured.Output.Contains("capture-line-1") || !captured.Output.Contains("capture-line-200") || !captured.Output.Contains("終わり🦉") || captured.Output.Contains("ForEach-Object")) throw new Exception("Capture failed: " + System.Text.Json.JsonSerializer.Serialize(captured));
            Console.WriteLine("Host long capture passed.");
            await ResizeDuringCaptureAsync(session);
            await CardAnchorAsync(session, pid);
            var failed = await session.ExecuteAsync(new("cmd /c exit 7"));
            if (failed.Succeeded != false || failed.ExitCode != 7) throw new Exception("Native exit missing.");
            var syntax = await session.ExecuteAsync(new("if (")).WaitAsync(TimeSpan.FromSeconds(15));
            if (syntax.Succeeded != false || string.IsNullOrWhiteSpace(syntax.Output)) throw new Exception("Syntax failure did not complete capture.");
            var topLevel = await session.ExecuteAsync(new("using namespace System")).WaitAsync(TimeSpan.FromSeconds(15));
            if (topLevel.Status != TerminalExecutionStatus.Completed || topLevel.Succeeded != syntax.Succeeded || !string.IsNullOrWhiteSpace(topLevel.Output) || topLevel.ExitCode != 7) throw new Exception("Standalone using directive changed status, output or native exit code: " + System.Text.Json.JsonSerializer.Serialize(topLevel));
            var directiveUse = await session.ExecuteAsync(new("using namespace System.Collections.Generic\n[List[int]]::new().GetType().Name")).WaitAsync(TimeSpan.FromSeconds(15));
            if (directiveUse.Status != TerminalExecutionStatus.Completed || directiveUse.Succeeded != true || !directiveUse.Output.Contains("List`1")) throw new Exception("First-statement using directive did not execute: " + System.Text.Json.JsonSerializer.Serialize(directiveUse));
            Console.WriteLine("Host exit and syntax failure passed.");
            var bounded = await session.ExecuteAsync(new("Write-Output ('x' * 70000)"));
            if (!bounded.Truncated || bounded.Output.Length > 64000) throw new Exception("Output bound missing.");
            Console.WriteLine("Host bound passed.");
            using var cancel = new CancellationTokenSource();
            var sleeping = session.ExecuteAsync(new("Start-Sleep 60"), cancel.Token);
            if ((await session.ExecuteAsync(new("Write-Output 'competing'"))).Status != TerminalExecutionStatus.Busy) throw new Exception("Busy tool queued.");
            session.Send("Write-Output 'accidental-input'\r");
            try { session.SubmitCommand("Write-Output 'competing'"); throw new Exception("User input admitted."); } catch (InvalidOperationException) { }
            await Task.Delay(500); cancel.Cancel();
            var interrupted = await sleeping;
            if (interrupted.Status != TerminalExecutionStatus.Interrupted || session.AgentOwnsTerminal || session.PromptState != ShellPromptState.Ready)
                throw new Exception($"Cancellation recovery failed (owns={session.AgentOwnsTerminal}, prompt={session.PromptState}): " + System.Text.Json.JsonSerializer.Serialize(interrupted) + "\nScreen:\n" + session.ReadText());
            if (session.ReadText().Contains("Agent completed [")) throw new Exception("Cancellation must not print a shell-generated successful agent-turn outcome.");
            Console.WriteLine("Host cancellation passed.");
            await Task.WhenAll(CancelThenExitAsync(), CancelTimeoutAsync());
            var recovered = await session.ExecuteAsync(new("smartAlias")).WaitAsync(TimeSpan.FromSeconds(15));
            if (!recovered.Output.Contains("42") || session.ProcessId != pid) throw new Exception("Shell identity lost: " + System.Text.Json.JsonSerializer.Serialize(recovered));
            await using (var second = new TerminalSession())
            {
                second.Start(); await Wait(() => second.PromptState == ShellPromptState.Ready);
                var isolated = await second.ExecuteAsync(new("Write-Output ('isolated-' + [string]$smartValue)"));
                if (isolated.Output.Contains("41") || second.ProcessId == pid) throw new Exception("Pane isolation failed.");
            }
            var interactive = session.ExecuteAsync(new("Read-Host 'owned fixture'"));
            await Wait(() => session.ReadText().Contains("owned fixture:"));
            session.AllowAgentInteractiveInput(); session.Send("owned-answer\r");
            if (!(await interactive).Output.Contains("owned-answer")) throw new Exception("Deliberate owned interactive input failed.");
            session.SubmitCommand("Set-Location $env:TEMP"); await Wait(() => session.PromptState == ShellPromptState.Ready);
            session.SubmitCommand("Read-Host 'fixture input'"); await Wait(() => session.ReadText().Contains("fixture input:")); session.Send("interactive-answer\r"); await Wait(() => session.PromptState == ShellPromptState.Ready);
            session.Send("Write-Output ('raw-value-' + $smartValue)\r"); await Wait(() => session.ReadText().Contains("raw-value-41"));
            await session.StopAsync();
        }
        finally { await session.DisposeAsync(); try { Directory.Delete(fixture); } catch (IOException) { Console.WriteLine("Fixture still held by closing shell: " + fixture); } }
        Console.WriteLine("Host capture > viewport, UTF-8, truncation, cwd/state/alias, exit 7, busy/input exclusion, cancellation recovery, pane isolation and raw fallback passed.");
    }
    // Ctrl+C reaches PowerShell as input, so the command outlives cancellation; ResistCtrlCAsync proves that before each case.
    private const string ResistantCommand = "[Console]::TreatControlCAsInput = $true; Write-Output ('cancel-' + 'partial'); Start-Sleep 60";
    private static async Task<Task<TerminalExecutionResult>> ResistCtrlCAsync(TerminalSession session, CancellationTokenSource cancel)
    {
        session.Start();
        using (var ready = new CancellationTokenSource(TimeSpan.FromSeconds(25))) while (session.PromptState != ShellPromptState.Ready) await Task.Delay(25, ready.Token);
        var running = session.ExecuteAsync(new(ResistantCommand), cancel.Token);
        using (var seen = new CancellationTokenSource(TimeSpan.FromSeconds(25))) while (!session.ReadText().Contains("cancel-partial")) await Task.Delay(25, seen.Token);
        cancel.Cancel();
        await Task.Delay(2000);
        if (running.IsCompleted || !session.AgentOwnsTerminal || session.Completion.IsCompleted)
            throw new Exception("Fixture must survive Ctrl+C with the agent still owning a live shell: " + (running.IsCompleted ? System.Text.Json.JsonSerializer.Serialize(await running) : "shell ended"));
        return running;
    }
    private static async Task CancelThenExitAsync()
    {
        await using var session = new TerminalSession(100, 35);
        using var cancel = new CancellationTokenSource();
        var running = await ResistCtrlCAsync(session, cancel);
        await session.StopAsync();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
        if (result.Status != TerminalExecutionStatus.Exited || result.Output.Split("cancel-partial").Length != 2 || session.AgentOwnsTerminal || session.PromptState != ShellPromptState.Exited)
            throw new Exception($"Shell exit during Ctrl+C recovery must promptly return Exited with partial output and release ownership (owns={session.AgentOwnsTerminal}): " + System.Text.Json.JsonSerializer.Serialize(result));
        Console.WriteLine("Host cancel then shell exit passed.");
    }
    private static async Task CancelTimeoutAsync()
    {
        await using var session = new TerminalSession(100, 35);
        using var cancel = new CancellationTokenSource();
        var running = await ResistCtrlCAsync(session, cancel);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(25));
        if (result.Status != TerminalExecutionStatus.InterruptionFailed || waited.Elapsed < TimeSpan.FromSeconds(15) || !result.Output.Contains("cancel-partial") ||
            !session.AgentOwnsTerminal || session.Completion.IsCompleted || (await session.ExecuteAsync(new("Write-Output 'competing'"))).Status != TerminalExecutionStatus.Busy)
            throw new Exception($"Genuine Ctrl+C timeout must keep the running command agent-owned with partial output (after {waited.Elapsed.TotalSeconds:F1}s): " + System.Text.Json.JsonSerializer.Serialize(result));
        Console.WriteLine("Host cancellation timeout kept ownership.");
    }
    private static async Task ResizeDuringCaptureAsync(TerminalSession session)
    {
        session.SubmitCommand("Write-Output ('resize-' + 'earlier')");
        using (var ready = new CancellationTokenSource(TimeSpan.FromSeconds(25))) while (session.PromptState != ShellPromptState.Ready) await Task.Delay(25, ready.Token);
        var running = session.ExecuteAsync(new("1..1 | ForEach-Object { 'resize-' + 'line-' + $_ }; Start-Sleep -Milliseconds 1500; Write-Output ('resize-' + 'after')"));
        using (var seen = new CancellationTokenSource(TimeSpan.FromSeconds(25))) while (!session.ReadText().Contains("resize-line-1")) await Task.Delay(25, seen.Token);
        session.Resize(80, 20); await Task.Delay(300); session.Resize(100, 35);
        var result = await running.WaitAsync(TimeSpan.FromSeconds(20));
        var output = result.Output;
        var lineCount = output.Split("resize-line-1" + (char)13).Length - 1;
        if (result.Status != TerminalExecutionStatus.Completed || lineCount != 1 || !output.Contains("resize-after") || output.Contains("resize-earlier") || output.Contains("Command started [") || output.Contains("Command ended ["))
            throw new Exception("Resize replay leaked into capture: " + System.Text.Json.JsonSerializer.Serialize(output));
        Console.WriteLine("Host resize-during-capture passed.");
    }
    private static async Task CardAnchorAsync(TerminalSession session, int pid)
    {
        async Task Ready() { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25)); while (session.PromptState != ShellPromptState.Ready) await Task.Delay(25, timeout.Token); }
        string Row(long screenRow)
        {
            session.ScrollTo(screenRow);
            var frame = session.ReadFrame();
            var row = (int)(screenRow - frame.Viewport.Offset);
            var text = string.Concat(Enumerable.Range(0, frame.Columns).Select(c => frame.Cells[row * frame.Columns + c].Text)).TrimEnd();
            session.ScrollToBottom();
            return text;
        }
        void Check(bool ok, string message) { if (!ok) throw new Exception("Card anchor: " + message); }
        session.SubmitCommand("Write-Output ('visible-' + 'card')"); await Ready();
        var visible = session.ReserveRows() ?? throw new Exception("Card anchor: reservation refused at a ready prompt.");
        await Ready();
        foreach (var (columns, rows) in new[] { ((ushort)60, (ushort)20), ((ushort)100, (ushort)35) })
        {
            session.Resize(columns, rows);
            for (var i = 0; i < 40; i++) { Check(session.ReadAnchor(visible) is not null, $"an on-screen card survives the resize repaint ({columns}x{rows}, poll {i})"); await Task.Delay(25); }
        }
        var visibleRow = session.ReadAnchor(visible)!.Value;
        Check(Row(visibleRow - 1) == "visible-card" && Row(visibleRow) == "" && Row(visibleRow + 1) == "", "an on-screen card keeps its blank rows through a resize");
        session.SubmitCommand("Write-Output ('before-' + 'card')"); await Ready();
        var anchor = session.ReserveRows() ?? throw new Exception("Card anchor: reservation refused at a ready prompt.");
        await Ready();
        var row = session.ReadAnchor(anchor) ?? throw new Exception("Card anchor: anchor expired immediately.");
        Check(Row(row - 1) == "before-card" && Row(row) == "" && Row(row + 1) == "", $"two blank rows follow the invoking output: <{Row(row - 1)}> <{Row(row)}> <{Row(row + 1)}>");
        session.SubmitCommand("$cardValue = 7; 1..60 | ForEach-Object { 'after-' + $_ }"); await Ready();
        var moved = session.ReadAnchor(anchor);
        Check(moved == row && Row(row - 1) == "before-card", "output after the card leaves its screen row in place");
        session.Resize(60, 20); await Task.Delay(500); session.Resize(100, 35); await Task.Delay(500);
        var reflowed = session.ReadAnchor(anchor) ?? throw new Exception("Card anchor: resize expired the anchor.");
        Check(Row(reflowed - 1) == "before-card" && Row(reflowed) == "", "resize keeps the anchor on its reserved row");
        Check(session.ProcessId == pid && (await session.ExecuteAsync(new("Write-Output ('card-' + $cardValue)"))).Output.Contains("card-7"), "the reservation keeps the same shell and its state");
        session.SubmitCommand("1..10100 | ForEach-Object { 'trim-' + $_ }"); await Ready();
        Check(session.ReadAnchor(anchor) is null && anchor.Expired, "history trimming expires the anchor");
        var cleared = session.ReserveRows() ?? throw new Exception("Card anchor: second reservation refused.");
        await Ready();
        session.SubmitCommand("Clear-Host"); await Ready();
        Check(session.ReadAnchor(cleared) is null, "clearing the screen expires a card in the cleared rows");
        Console.WriteLine("Card anchor reservation, output, resize, shell continuity, trim and clear passed.");
    }
}
