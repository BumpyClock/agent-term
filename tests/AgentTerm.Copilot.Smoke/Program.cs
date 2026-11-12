using AgentTerm.Agents;
using AgentTerm.Copilot;
using AgentTerm.Terminal;
await LifecycleChecks.RunAsync();
var normalizedStart = CopilotToolPresentation.Start(Guid.NewGuid(), "call-id", "run_in_terminal", new { command = "boat.exe --help", schema = "private" });
if (normalizedStart.CallId != "call-id" || normalizedStart.Detail != "boat.exe --help" || normalizedStart.Category != ToolCategory.Command) throw new Exception("Typed tool start normalization failed.");
foreach (var (result, success, expected) in new[] {
    (new TerminalExecutionResult(TerminalExecutionStatus.Completed, "hello", true, 0, "C:/fixture"), true, WorkStatus.Succeeded),
    (new TerminalExecutionResult(TerminalExecutionStatus.Completed, "failed output", false, 1), true, WorkStatus.Failed),
    (new TerminalExecutionResult(TerminalExecutionStatus.Interrupted), false, WorkStatus.Cancelled),
    (new TerminalExecutionResult(TerminalExecutionStatus.Denied), true, WorkStatus.Cancelled) })
{
    var display = CopilotToolPresentation.Complete("run_in_terminal", System.Text.Json.JsonSerializer.Serialize(result), success);
    if (display.Status != expected || display.Text.Contains("outputFormat", StringComparison.OrdinalIgnoreCase) || display.Text.Contains("schema", StringComparison.OrdinalIgnoreCase) || display.Text.Contains("\"status\"", StringComparison.OrdinalIgnoreCase)) throw new Exception("Tool result leaked protocol or lost status.");
    if (result.Output.Length > 0 && !display.Text.Contains(result.Output)) throw new Exception("Output missing.");
    if (result.CurrentDirectory is not null && !display.Text.Contains(result.CurrentDirectory)) throw new Exception("Directory missing.");
}
if (CopilotToolPresentation.Complete("run_in_terminal", "{schema:secret}", true).Text.Contains("secret")) throw new Exception("Malformed result leaked.");
Console.WriteLine("Copilot tool normalization checks passed.");
if (args.Contains("--normalization-only")) return;
var fixture = Path.Combine(Path.GetTempPath(), "agent-term-copilot-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    await using var terminal = new TerminalSession(100, 30); terminal.Start();
    async Task Wait(Func<bool> condition) { while (!condition()) await Task.Delay(25, timeout.Token); }
    await Wait(() => terminal.PromptState == ShellPromptState.Ready);
    var secretMarker = Guid.NewGuid().ToString("N");
    terminal.SubmitCommand("$paneOnly = '" + secretMarker + "'; function PaneProof { Write-Output $paneOnly }; Set-Alias paneProofAlias PaneProof; Set-Location -LiteralPath '" + fixture.Replace("'", "''") + "'");
    await Wait(() => terminal.PromptState == ShellPromptState.Ready);
    var pid = terminal.ProcessId;
    var harness = new CopilotHarness(); var location = new AgentLocation(fixture);
    var description = await harness.DescribeAsync(location, timeout.Token);
    if (!description.Available) throw new Exception(description.Message);
    var proof = new RecordingTerminal(terminal);
    await using var session = await harness.OpenAsync(new(location, Terminal: proof), timeout.Token);
    const string command = "paneProofAlias; (Get-Location).Path; $PID";
    await session.SubmitAsync(new(Guid.NewGuid(), "Call run_in_terminal exactly once with this exact command and report its output. Do not guess the value. Command is " + command), timeout.Token);
    var invoked = false; var returned = false; var assistantText = new System.Text.StringBuilder();
    string? startedCall = null, approvedCall = null, executedCall = null;
    await foreach (var item in session.Events(timeout.Token))
    {
        if (item is TextDelta text) assistantText.Append(text.Text);
        if (item is ToolStarted tool && tool.Name == "run_in_terminal") { invoked = true; startedCall = tool.CallId; Console.WriteLine("SDK ToolStarted run_in_terminal " + tool.Detail); }
        if (item is ApprovalRequest request) { approvedCall = request.CallId; await session.RespondAsync(new(request.Request, request.Description == command), timeout.Token); }
        if (item is ToolExecuting executing) executedCall = executing.CallId;
        if (item is ToolOutput output) { Console.WriteLine("SDK result " + output.Text); if (output.Text.Contains(secretMarker)) returned = true; }
        if (item is TurnFailed failed) throw new Exception(failed.Message);
        if (item is TurnCompleted) break;
    }
    if (assistantText.Length == 0) throw new Exception("Real Copilot produced no assistant text.");
    Console.WriteLine("Real Copilot produced assistant text (content omitted).");
    // The UI labels a row "awaiting approval" and later "running" by these ids. ToolInvocation binding must yield the SDK's own call id.
    if (startedCall is null || approvedCall != startedCall || executedCall != startedCall)
        throw new Exception($"Approval and execution must name the started call. started={startedCall} approval={approvedCall} executing={executedCall}");
    if (!invoked || !returned || proof.Calls != 1 || proof.Result is not { Status: TerminalExecutionStatus.Completed } result || !result.Output.Contains(secretMarker) || !result.Output.Contains(fixture) || !result.Output.Contains(pid.ToString()) || terminal.ProcessId != pid) throw new Exception("Actual tool proof missing. Calls=" + proof.Calls + " result=" + System.Text.Json.JsonSerializer.Serialize(proof.Result));
    Console.WriteLine("Actual host tool result " + System.Text.Json.JsonSerializer.Serialize(proof.Result));
    Console.WriteLine($"Real Copilot custom tool invocation, shell-only variable/alias/cwd and unchanged ProcessId {pid} passed.");
    var stopTurn = Guid.NewGuid();
    await session.SubmitAsync(new(stopTurn, "Call run_in_terminal with exactly Start-Sleep 60. This is an interruption test."), timeout.Token);
    await foreach (var item in session.Events(timeout.Token))
    {
        if (item is ApprovalRequest request) { await session.RespondAsync(new(request.Request, request.Description == "Start-Sleep 60"), timeout.Token); break; }
        if (item is TurnFailed failed) throw new Exception(failed.Message);
    }
    await Wait(() => terminal.AgentOwnsTerminal);
    await Task.Delay(500, timeout.Token);
    await session.InterruptAsync(stopTurn, timeout.Token);
    var recovered = await terminal.ExecuteAsync(new("paneProofAlias"), timeout.Token);
    if (terminal.AgentOwnsTerminal || terminal.ProcessId != pid || !recovered.Output.Contains(secretMarker)) throw new Exception("Real Copilot interrupt failed to recover original shell.");
    Console.WriteLine("Real Copilot stop interrupted owned Start-Sleep 60 and recovered the same shell and alias.");
    await terminal.StopAsync();
}
finally { Directory.Delete(fixture, true); }
sealed class RecordingTerminal(IHostTerminal inner) : IHostTerminal
{
    public int Calls; public TerminalExecutionResult? Result;
    public async Task<TerminalExecutionResult> ExecuteAsync(TerminalExecutionRequest request, CancellationToken cancellationToken = default)
    { Calls++; return Result = await inner.ExecuteAsync(request, cancellationToken); }
}
