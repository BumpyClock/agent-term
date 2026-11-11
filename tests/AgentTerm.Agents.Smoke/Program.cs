using System.Threading.Channels;
using AgentTerm.Agents;
using AgentTerm.Terminal;
static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
static string Display(AgentSessionController controller) => string.Join("\n", controller.Timeline.Entries.OfType<TimelineMessage>().Select(m => m.Text));
static async Task Wait(Func<bool> condition) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, timeout.Token); }
TimelineChecks.Run();
foreach (var input in new[] { "git status", "/agentx do it", " /agent padded", "/Agent caps", "agent help", "$x = 1", "" }) Check(PromptRouter.Parse(input) == new ShellIntent(input), "Shell text stays shell: " + input);
Check(PromptRouter.Parse("/agent") == new AgentIntent(""), "Bare /agent opens the surface");
Check(PromptRouter.Parse("/agent   size of downloads  ") == new AgentIntent("size of downloads"), "Agent request is trimmed");
Check(PromptRouter.Parse("/agent" + (char)10 + "two lines") == new AgentIntent("two lines"), "Any whitespace ends the slash token");
var protocol = new ShellPromptProtocol("test");
var wire = "\u001b]777;other;ready\a\u001b]777;test;raw\a\u001b]777;test;start\a\u001b]777;test;end;0;7\a\u001b]777;test;commands;" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("myAlias\nmyFunction")) + "\a\u001b]777;test;ready\a";
foreach (var b in System.Text.Encoding.UTF8.GetBytes(wire)) protocol.Feed([b]);
Check(protocol.State == ShellPromptState.Ready && protocol.LastCommandSucceeded == false && protocol.LastExitCode == 7, "Fragmented lifecycle"); Check(protocol.Commands.Contains("myAlias"), "Metadata");
protocol.Feed(System.Text.Encoding.UTF8.GetBytes("\u001b]777;test;start\u001b\\"));
Check(protocol.State == ShellPromptState.Executing, "ST termination");
var captureProtocol = new ShellPromptProtocol("capture");
var capture = new CommandOutputCapture();
captureProtocol.OutputByte += capture.Feed; captureProtocol.Marker += capture.Marker;
var captureWire = "prior echo\u001b]777;capture;start\ahello 🦉終\u001b]7;file://localhost/C:/\a\u001b]777;capture;end;1;0\aprompt\u001b]777;capture;commands;YQ==\a";
foreach (var b in System.Text.Encoding.UTF8.GetBytes(captureWire)) captureProtocol.Feed([b]);
Check(capture.Text == "hello 🦉終", "Capture excludes echo/metadata/prompt with byte-fragmented UTF-8.");
var orderedProtocol = new ShellPromptProtocol("ordered");
var orderedCapture = new CommandOutputCapture("a:fixture");
orderedProtocol.OutputByte += orderedCapture.Feed; orderedProtocol.Marker += orderedCapture.Marker;
var orderedWire = "echo __AgentTermStart; command\r\nAgent command [a:fixture]\r\nfirst 🦉\r\nA\r\nnext\r\n\u001b]777;ordered;end;1;0;a:fixture\a\u001b]777;ordered;ready;a:fixture\afinal line\r\nAgent completed [a:fixture]\r\nprompt";
foreach (var b in System.Text.Encoding.UTF8.GetBytes(orderedWire)) orderedProtocol.Feed([b]);
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
session.Emit(new TurnCompleted(turn)); await Wait(() => controller.State == AgentState.Idle); Check(await controller.SubmitAsync("next", location), "Second turn"); session.Emit(new TurnCompleted(turn)); await Task.Delay(30); Check(controller.State == AgentState.Running, "Late completion ignored");
session.Emit(new TurnFailed(session.Turn, "fixture failure")); await Wait(() => controller.State == AgentState.Failed); Check(Display(controller).Contains("fixture failure"), "Visible failure");
await controller.SubmitAsync("cancel", location);
session.Emit(new ToolStarted(session.Turn, "interrupted", "terminal", ToolCategory.Command, "sleep", "sleep"));
await Wait(() => controller.Timeline.Entries.OfType<TimelineWorkGroup>().Last().Active);
var interruptedGroup = controller.Timeline.Entries.OfType<TimelineWorkGroup>().Last();
await controller.InterruptAsync(); await Wait(() => controller.State == AgentState.Idle);
Check(other.State == AgentState.Running, "Interrupt isolation");
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
sealed class FakeHarness : IAgentHarness
{
    public FakeSession Session { get; } = new();
    public Task<HarnessDescription> DescribeAsync(AgentLocation location, CancellationToken cancellationToken = default) => Task.FromResult(new HarnessDescription(true, "test", []));
    public Task<IAgentSession> OpenAsync(AgentOptions options, CancellationToken cancellationToken = default) => Task.FromResult<IAgentSession>(Session);
}
sealed class FakeSession : IAgentSession
{
    public bool FailResponse;
    private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>(); public Guid Turn; public AgentResponse? Response; public bool Disposed;
    public void Emit(AgentEvent item) => _events.Writer.TryWrite(item);
    public IAsyncEnumerable<AgentEvent> Events(CancellationToken cancellationToken = default) => _events.Reader.ReadAllAsync(cancellationToken);
    public Task SubmitAsync(AgentPrompt prompt, CancellationToken cancellationToken = default) { Turn = prompt.Turn; return Task.CompletedTask; }
    public Task RespondAsync(AgentResponse response, CancellationToken cancellationToken = default)
    {
        if (FailResponse) throw new IOException("Fixture response transport failed.");
        Response = response;
        return Task.CompletedTask;
    }
    public Task InterruptAsync(Guid turn, CancellationToken cancellationToken = default) { Emit(new TurnInterrupted(turn)); return Task.CompletedTask; }
    public ValueTask DisposeAsync() { Disposed = true; _events.Writer.TryComplete(); return ValueTask.CompletedTask; }
}
