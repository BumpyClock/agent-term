using System.Threading.Channels;
using AgentTerm.Agents;
using AgentTerm.Terminal;
static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
static string Display(AgentSessionController controller) => string.Join("\n", controller.Timeline.Entries.OfType<TimelineMessage>().Select(m => m.Text));
static async Task Wait(Func<bool> condition) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, timeout.Token); }
UiContext.Run(async () =>
{
    TimelineChecks.Run();
    CoalescingChecks.Run();
    await LifecycleChecks.RunAsync(Wait);
    foreach (var input in new[] { "git status", "/agentx do it", " /agent padded", "/Agent caps", "agent help", "$x = 1", "" }) Check(PromptRouter.Parse(input) == new ShellIntent(input), "Shell text stays shell: " + input);
    Check(PromptRouter.Parse("/agent") == new AgentIntent(""), "Bare /agent opens the surface");
    Check(PromptRouter.Parse("/agent   size of downloads  ") == new AgentIntent("size of downloads"), "Agent request is trimmed");
    Check(PromptRouter.Parse("/agent" + (char)10 + "two lines") == new AgentIntent("two lines"), "Any whitespace ends the slash token");
    var protocol = new ShellPromptProtocol("test");
    using var protocolTerminal = new GhosttyTerminal(80, 6);
    var protocolScanner = new TerminalProtocolStream(protocolTerminal);
    protocolScanner.Sequence += protocol.Accept;
    var wire = "\u001b]777;other;ready\a\u001b]777;test;ready\a\u001b]777;test;start\a\u001b]777;test;end;0;7\a\u001b]777;test;ready\a";
    foreach (var b in System.Text.Encoding.UTF8.GetBytes(wire)) protocolScanner.Write([b]);
    Check(protocol.State == ShellPromptState.Ready && protocol.LastCommandSucceeded == false && protocol.LastExitCode == 7, "Fragmented lifecycle");
    protocolScanner.Write(System.Text.Encoding.UTF8.GetBytes("\u001b]777;test;start\u001b\\"));
    Check(protocol.State == ShellPromptState.Executing, "ST termination");
    protocolScanner.Write(System.Text.Encoding.UTF8.GetBytes("\u001b]777;other;agent;aGVsbG8=\a\u001b]777;test;agent;!invalid!\a"));
    Check(protocol.TakeAgentRequest() is null, "Untrusted and malformed native requests are ignored");
    var nativeWire = "\u001b]777;test;ready\a\u001b]777;test;agent;" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("native 界")) + "\a\u001b]777;test;agent;\a";
    foreach (var b in System.Text.Encoding.UTF8.GetBytes(nativeWire)) protocolScanner.Write([b]);
    Check(protocol.TakeAgentRequest() == "native 界" && protocol.TakeAgentRequest() == "" && protocol.TakeAgentRequest() is null, "Native requests retain Unicode, order and bare entry exactly once");
    Check(protocol.State == ShellPromptState.Ready && protocol.LastCommandSucceeded == false, "Native entry must not alter readiness or last command status");
    var captureProtocol = new ShellPromptProtocol("capture");
    using var captureTerminal = new GhosttyTerminal(80, 6);
    var captureProtocolScanner = new TerminalProtocolStream(captureTerminal);
    captureProtocolScanner.Sequence += captureProtocol.Accept;
    var capture = new CommandOutputCapture();
    captureProtocolScanner.Output += capture.Feed; captureProtocol.Marker += capture.Marker;
    var captureWire = "prior echo\u001b]777;capture;start\ahello 🦉終\u001b]7;file://localhost/C:/\a\u001b]777;capture;end;1;0\aprompt\u001b]777;capture;ready\a";
    foreach (var b in System.Text.Encoding.UTF8.GetBytes(captureWire)) captureProtocolScanner.Write([b]);
    Check(capture.Text == "hello 🦉終", "Capture excludes echo/metadata/prompt with byte-fragmented UTF-8.");
    var orderedProtocol = new ShellPromptProtocol("ordered");
    using var orderedTerminal = new GhosttyTerminal(80, 6);
    var orderedProtocolScanner = new TerminalProtocolStream(orderedTerminal);
    orderedProtocolScanner.Sequence += orderedProtocol.Accept;
    var orderedCapture = new CommandOutputCapture("a:fixture");
    orderedProtocolScanner.Output += orderedCapture.Feed; orderedProtocol.Marker += orderedCapture.Marker;
    var orderedWire = "echo __AgentTermStart; command\r\nCommand started [a:fixture]\r\nfirst 🦉\r\nA\r\nnext\r\n\u001b]777;ordered;end;1;0;a:fixture\a\u001b]777;ordered;ready;a:fixture\afinal line\r\nCommand ended [a:fixture]\r\nprompt";
    foreach (var b in System.Text.Encoding.UTF8.GetBytes(orderedWire)) orderedProtocolScanner.Write([b]);
    Check(orderedCapture.Ended && orderedCapture.Text == "first 🦉\r\nA\r\nnext\r\nfinal line\r\n", "Reordered OSC must not cut off output or capture fences/echo/prompt.");
    var harness = new FakeHarness(); await using var controller = new AgentSessionController(harness); var otherHarness = new FakeHarness(); await using var other = new AgentSessionController(otherHarness);
    var location = new AgentLocation(Path.GetTempPath());
    await TaskChecks.RunAsync(() => new AgentSessionController(new FakeHarness()), location, Wait);
    Check(await controller.SubmitAsync("hello", location), "Admission"); Check(!await controller.SubmitAsync("duplicate", location), "Double submission");
    var session = harness.Session; var turn = session.Turn;
    session.Emit(new TextDelta(turn, "hello ")); session.Emit(new TextDelta(turn, "world")); await Wait(() => Display(controller).Contains("hello world")); Check(Display(other) == "", "Independent panes");
    Check(await other.SubmitAsync("independent", location), "Second pane submission");
    otherHarness.Session.Emit(new TextDelta(otherHarness.Session.Turn, "other pane"));
    await Wait(() => Display(other).Contains("other pane"));
    Check(!Display(controller).Contains("other pane"), "Transcript isolation");

    session.Emit(new ToolStarted(turn, "one", "terminal", ToolCategory.Command, "boat.exe", "boat.exe --help"));
    session.Emit(new ToolStarted(turn, "two", "terminal", ToolCategory.Command, "Get-Content", "Get-Content file.txt"));
    await Wait(() => controller.Timeline.Entries.OfType<TimelineWorkGroup>().Any(g => g.Tools.Count == 2));
    var group = controller.Timeline.Entries.OfType<TimelineWorkGroup>().Last();
    group.Expanded = true;
    session.Emit(new ToolOutput(turn, "two", "second output"));
    session.Emit(new ToolCompleted(turn, "two", WorkStatus.Succeeded));
    session.Emit(new ToolOutput(turn, "one", "first output"));
    session.Emit(new ToolCompleted(turn, "one", WorkStatus.Succeeded));
    await Wait(() => !group.Active);
    Check(group.Summary == "Ran 2 commands", "Honest command count, no guessed file reads");
    Check(group.Tools[0].Output == "first output" && group.Tools[1].Output == "second output", "Overlapping out-of-order calls retain identity");
    session.Emit(new ApprovalRequest(turn, Guid.NewGuid(), "third command"));
    await Wait(() => controller.Request is ApprovalRequest);
    await controller.RespondAsync(new(((ApprovalRequest)controller.Request!).Request, true));
    session.Emit(new ToolStarted(turn, "three", "terminal", ToolCategory.Command, "third", "third"));
    session.Emit(new ToolOutput(turn, "three", "visible failure"));
    session.Emit(new ToolCompleted(turn, "three", WorkStatus.Failed));
    await Wait(() => Display(controller).Contains("visible failure"));
    Check(group.Tools.Count == 3 && group.Expanded, "Sequential call across approval preserves group and expansion");
    Check(controller.Timeline.Entries.OfType<TimelineMessage>().Any(m => m.Role == MessageRole.Failure && m.Text.Contains("visible failure")), "Failure outside collapsed group");
    group.Expanded = false;
    session.Emit(new TextDelta(turn, "after tools"));
    await Wait(() => Display(controller).Contains("after tools"));
    Check(!group.Expanded, "Collapsed user choice survives unrelated stream append");

    session.Emit(new ToolStarted(turn, "gated", "terminal", ToolCategory.Command, "gated", "gated"));
    session.Emit(new ToolStarted(turn, "parallel", "terminal", ToolCategory.Command, "parallel", "parallel"));
    var shownApproval = Guid.NewGuid(); var queuedApproval = Guid.NewGuid();
    session.Emit(new ApprovalRequest(turn, shownApproval, "parallel", "parallel"));
    session.Emit(new ApprovalRequest(turn, queuedApproval, "gated", "gated"));
    await Wait(() => controller.AwaitsApproval("gated"));
    var parallelGroup = controller.Timeline.Entries.OfType<TimelineWorkGroup>().Last();
    Check(controller.AwaitsApproval("parallel") && !controller.AwaitsApproval("one"), "Every queued approval names only its own call.");
    Check(parallelGroup.Summary == "Pending parallel", "A call with no reported execution must not read as running.");
    await controller.RespondAsync(new(shownApproval, true));
    Check(!controller.AwaitsApproval("parallel") && controller.AwaitsApproval("gated"), "Answering one approval leaves the other call gated.");
    session.Emit(new ToolExecuting(turn, "parallel"));
    await Wait(() => parallelGroup.Tools.Single(t => t.CallId == "parallel").Executing);
    Check(parallelGroup.Summary == "Running parallel" && !parallelGroup.Tools.Single(t => t.CallId == "gated").Executing, "Execution is reported per call.");
    await controller.RespondAsync(new(queuedApproval));
    session.Emit(new ToolCompleted(turn, "gated", WorkStatus.Cancelled));
    session.Emit(new ToolCompleted(turn, "parallel", WorkStatus.Succeeded));
    await Wait(() => !parallelGroup.Active);
    Check(!controller.AwaitsApproval("gated") && controller.State == AgentState.Running, "Both approvals settle.");

    foreach (var approved in new[] { false, true })
    {
        var id = Guid.NewGuid(); session.Emit(new ApprovalRequest(turn, id, "Write fixture")); await Wait(() => controller.State == AgentState.WaitingForApproval); await controller.RespondAsync(new(id, approved)); Check(session.Response?.Approved == approved && controller.State == AgentState.Running, "Permission decision");
    }
    var request = Guid.NewGuid(); session.Emit(new QuestionRequest(turn, request, "Which?", ["one", "two"], false)); await Wait(() => controller.State == AgentState.WaitingForInput);
    session.FailResponse = true;
    Check(!await controller.RespondAsync(new(request, Answer: "two")), "Failed response is reported");
    Check(controller.Request is QuestionRequest && controller.State == AgentState.WaitingForInput, "Failed response preserves pending question");
    Check(Display(controller).Contains("Could not send response"), "Failed response is visible");
    session.FailResponse = false;
    Check(await controller.RespondAsync(new(request, Answer: "two")), "Response retry succeeds");
    Check(session.Response?.Answer == "two" && controller.Request is null, "Question reply");
    session.Emit(new TurnCompleted(turn)); await Wait(() => controller.State == AgentState.Idle);
    Check(controller.CurrentTask?.LastOutcome == WorkStatus.Succeeded, "Completed task retains its outcome for the surface and history card.");
    Check(await controller.SubmitAsync("next", location), "Second turn");
    Check(controller.CurrentTask?.LastOutcome is null, "Follow-up clears the previous completion marker.");
    session.Emit(new TurnCompleted(turn)); await Task.Delay(30); Check(controller.State == AgentState.Running, "Late completion ignored");
    session.Emit(new TurnFailed(session.Turn, "fixture failure")); await Wait(() => controller.State == AgentState.Failed); Check(Display(controller).Contains("fixture failure"), "Visible failure");
    Check(controller.CurrentTask?.LastOutcome == WorkStatus.Failed, "Failed task must not look completed.");
    await controller.SubmitAsync("cancel", location);
    session.Emit(new ToolStarted(session.Turn, "interrupted", "terminal", ToolCategory.Command, "sleep", "sleep"));
    await Wait(() => controller.Timeline.Entries.OfType<TimelineWorkGroup>().Last().Active);
    var interruptedGroup = controller.Timeline.Entries.OfType<TimelineWorkGroup>().Last();
    await controller.InterruptAsync(); await Wait(() => controller.State == AgentState.Idle);
    Check(controller.CurrentTask?.LastOutcome == WorkStatus.Cancelled, "Interrupted task must retain Stopped rather than Done.");
    Check(controller.Timeline.Entries.OfType<TimelineMessage>().Any(m => m.Turn == session.Turn && m.Role == MessageRole.Cancelled && m.Text == "Stopped before any reply."), "Cancellation must retain a typed, readable outcome rather than a generic duplicate notice.");
    Check(other.State == AgentState.Running, "Interrupt isolation");
    await controller.SubmitAsync("keep partial reply", location);
    session.Emit(new TextDelta(session.Turn, "Retained partial answer"));
    await Wait(() => Display(controller).Contains("Retained partial answer"));
    await controller.InterruptAsync(); await Wait(() => !controller.HasActiveTurn);
    Check(Display(controller).Contains("Retained partial answer") && Display(controller).Contains("Stopped. Partial reply kept."), "Stop must preserve and explain partial output.");

    Check(!interruptedGroup.Active && interruptedGroup.Tools[0].Status == WorkStatus.Cancelled, "Interrupted group settled");
    await controller.SubmitAsync("bounded", location); session.Emit(new TextDelta(session.Turn, new string('x', 70000))); await Wait(() => controller.Timeline.Entries.OfType<TimelineMessage>().Last().Text.Length <= AgentTimeline.TextLimit && Display(controller).Contains("Earlier text omitted"));
    session.Emit(new TurnCompleted(session.Turn)); await Wait(() => controller.State == AgentState.Idle);
    for (var i = 0; i < 140; i++)
    {
        Check(await controller.SubmitAsync("bounded turn " + i, location), "Subsequent turn admission");
        session.Emit(new TextDelta(session.Turn, "retained " + i));
        session.Emit(new TurnCompleted(session.Turn)); await Wait(() => controller.State == AgentState.Idle);
    }
    Check(controller.Timeline.Entries.Count <= AgentTimeline.EntryLimit && Display(controller).Contains("retained 139"), "Bounded retention and subsequent turns");
    await controller.DisposeAsync(); Check(session.Disposed && controller.State == AgentState.Disposed, "Disposal"); Console.WriteLine("Smart prompt routing, protocol and shared harness behavior passed.");
});
sealed class FakeHarness : IAgentHarness
{
    public FakeSession Session { get; } = new();
    public Task<HarnessDescription> DescribeAsync(AgentLocation location, CancellationToken cancellationToken = default) => Task.FromResult(new HarnessDescription(true, "test", []));
    public Task<IAgentSession> OpenAsync(AgentOptions options, CancellationToken cancellationToken = default) => Task.FromResult<IAgentSession>(Session);
}
sealed class FakeSession : IAgentSession
{
    public bool FailResponse;
    public bool FailSubmit, Disposing;
    public TaskCompletionSource? DisposeRelease;
    public TaskCompletionSource? StopRelease;
    public int InterruptCalls;
    private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>(); public Guid Turn; public AgentResponse? Response; public bool Disposed;
    public void Emit(AgentEvent item) => _events.Writer.TryWrite(item);
    public void Fail(Exception error) => _events.Writer.TryComplete(error);
    public IAsyncEnumerable<AgentEvent> Events(CancellationToken cancellationToken = default) => _events.Reader.ReadAllAsync(cancellationToken);
    public Task SubmitAsync(AgentPrompt prompt, CancellationToken cancellationToken = default)
    {
        if (FailSubmit) throw new IOException("Fixture submission failed.");
        Turn = prompt.Turn; return Task.CompletedTask;
    }
    public Task RespondAsync(AgentResponse response, CancellationToken cancellationToken = default)
    {
        if (FailResponse) throw new IOException("Fixture response transport failed.");
        Response = response;
        return Task.CompletedTask;
    }
    public async Task InterruptAsync(Guid turn, CancellationToken cancellationToken = default)
    {
        InterruptCalls++;
        if (StopRelease is not null) { await StopRelease.Task; throw new IOException("Old interruption failed."); }
        Emit(new TurnInterrupted(turn));
    }
    public async ValueTask DisposeAsync()
    {
        Disposing = true;
        if (DisposeRelease is not null) await DisposeRelease.Task;
        Disposed = true; _events.Writer.TryComplete();
    }
}
