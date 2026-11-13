using AgentTerm.Agents;

static class LifecycleChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task RunAsync(Func<Func<bool>, Task> wait)
    {
        var location = new AgentLocation(Path.GetTempPath());
        var harness = new LifecycleHarness();
        IHostTerminal terminal = new Terminal();
        await using (var controller = new AgentSessionController(harness, () => terminal))
        {
            var thread = Environment.CurrentManagedThreadId;
            controller.Changed += () => Check(Environment.CurrentManagedThreadId == thread, "Controller mutations must stay on the UI context.");
            await controller.SubmitAsync("first", location);
            var first = harness.Sessions.Single();
            await Task.Run(() => first.Emit(new TurnCompleted(first.Turn)));
            await wait(() => controller.CanSubmit);
            await controller.SubmitAsync("reuse", location);
            Check(harness.Sessions.Count == 1, "Same terminal and cwd reuse the conversation.");
            first.Emit(new TurnCompleted(first.Turn));
            await wait(() => controller.CanSubmit);
            terminal = new Terminal();
            await controller.SubmitAsync("replacement", location);
            Check(first.Disposed && harness.Sessions.Count == 2 && ReferenceEquals(harness.Binding, terminal), "Same-cwd terminal replacement must rebind tools.");
            var second = harness.Sessions.Last();
            second.StopRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? reentrantStop = null;
            void OnChanged()
            {
                if (controller.State == AgentState.Interrupting) reentrantStop = controller.InterruptAsync();
            }
            controller.Changed += OnChanged;
            var stop = controller.InterruptAsync();
            controller.Changed -= OnChanged;
            var repeated = controller.InterruptAsync();
            Check(ReferenceEquals(stop, reentrantStop), "Stop is published before Changed can reenter it.");
            Check(ReferenceEquals(stop, repeated) && second.InterruptCalls == 1, "Running Stop is single-flight.");
            second.Emit(new TurnInterrupted(second.Turn));
            await wait(() => controller.CanSubmit);
            await controller.SubmitAsync("after stop", location);
            second.StopRelease.SetResult();
            await Task.WhenAll(stop, repeated);
            Check(!second.Disposed && controller.State == AgentState.Running, "Late Stop failure must not dispose or finish the new turn.");
            second.Emit(new TurnCompleted(second.Turn));
            await wait(() => controller.CanSubmit);
            await controller.SubmitAsync("lost stream", location);
            second.Fail(new IOException("Native idle drain expired."));
            await wait(() => controller.State == AgentState.Failed);
            Check(second.Disposed, "Unusable native session must be discarded before retry admission.");
            await controller.SubmitAsync("fresh session", location);
            Check(harness.Sessions.Count == 3, "Retry after stream failure opens a fresh provider session.");
        }
        foreach (var blockOpen in new[] { false, true })
        {
            harness = new LifecycleHarness { BlockOpen = blockOpen, BlockDescribe = !blockOpen };
            await using var controller = new AgentSessionController(harness);
            var submission = controller.SubmitAsync("must not send", location);
            await harness.Entered.Task;
            Check(controller.HasActiveTurn && controller.CanInterrupt, "Startup owns a turn and offers real cancellation before provider open completes.");
            var stop = controller.InterruptAsync();
            Check(controller.HasActiveTurn && !controller.CanInterrupt, "Stopping retains turn ownership without offering a duplicate Stop.");
            Check(!controller.CanSubmit && !await controller.SubmitAsync("too soon", location), "Stop retains admission until startup unwinds.");
            var concurrentStop = controller.InterruptAsync();
            Check(ReferenceEquals(stop, concurrentStop), "Concurrent startup Stop shares one operation.");
            Check(harness.StartupToken.IsCancellationRequested, "Stop cancels the active startup token.");
            harness.Release.SetResult();
            await Task.WhenAll(stop, concurrentStop);
            Check(!await submission && controller.State == AgentState.Idle, "Canceled startup returns idle without sending.");
            Check(harness.Sessions.All(s => s.Disposed && s.Turn == Guid.Empty), "Late opened sessions are discarded without submitting.");
        }
        foreach (var failSubmit in new[] { false, true })
        {
            harness = new LifecycleHarness();
            await using var controller = new AgentSessionController(harness);
            await controller.SubmitAsync("first", location);
            var session = harness.Sessions.Single();
            if (failSubmit)
            {
                session.Emit(new TurnCompleted(session.Turn));
                await wait(() => controller.CanSubmit);
            }
            session.DisposeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool>? submission = null;
            if (failSubmit) { session.FailSubmit = true; submission = controller.SubmitAsync("failed send", location); }
            else session.Fail(new IOException("Fixture stream failed."));
            await wait(() => session.Disposing);
            try
            {
                await controller.InterruptAsync();
                Check(!controller.CanSubmit && session.InterruptCalls == 0, "Stop during failure cleanup neither interrupts a detached session nor admits retry.");
            }
            finally { session.DisposeRelease.TrySetResult(); }
            if (submission is not null) Check(!await submission, "Failed submission remains failed after cleanup.");
            await wait(() => controller.State == AgentState.Failed);
            Check(session.Disposed, "Failure cleanup completes after Stop.");
            Check(await controller.SubmitAsync("retry", location) && harness.Sessions.Count == 2, "Retry after failure cleanup opens a fresh session.");
        }
        var late = new DeclineHarness();
        await using (var controller = new AgentSessionController(late))
        {
            var states = new List<(AgentState State, bool Request)>();
            controller.Changed += () => states.Add((controller.State, controller.Request is not null));
            await controller.SubmitAsync("stop me", location);
            var session = late.Session;
            var stop = controller.InterruptAsync();
            Check(controller.State == AgentState.Interrupting, "Stop publishes Interrupting before the provider drains.");
            var approval = Guid.NewGuid();
            var question = Guid.NewGuid();
            session.Emit(new TextDelta(session.Turn, "kept text"));
            session.Emit(new ApprovalRequest(session.Turn, approval, "late approval", "call"));
            session.Emit(new QuestionRequest(session.Turn, question, "late question", [], true));
            await wait(() => session.Responses.Count == 2 || controller.State != AgentState.Interrupting);
            Check(controller.State == AgentState.Interrupting && controller.Request is null && !controller.CanSubmit && !controller.CanInterrupt, "Late requests must not replace Interrupting or become actionable.");
            Check(!controller.AwaitsApproval("call"), "A declined late approval must not make its tool row claim it awaits approval.");
            Check(session.Responses.Count(r => r.Request == approval) == 1 && session.Responses.Count(r => r.Request == question) == 1, "Each late request is declined exactly once.");
            Check(session.Responses.All(r => !r.Approved && r.Answer is null), "Late requests are declined, never approved or answered.");
            Check(controller.Timeline.Entries.OfType<TimelineMessage>().Any(m => m.Role == MessageRole.Assistant && m.Text == "kept text"), "Output delivered while stopping is retained.");
            session.Emit(new TurnInterrupted(session.Turn));
            await wait(() => controller.CanSubmit);
            session.StopRelease.SetResult();
            await stop;
            Check(controller.State == AgentState.Idle && controller.Request is null && controller.CurrentTask?.LastOutcome == WorkStatus.Cancelled, "Stopped turn settles idle and cancelled.");
            Check(controller.Timeline.Entries.OfType<TimelineMessage>().Count(m => m.Role == MessageRole.Cancelled) == 1, "Stop records exactly one stopped notice.");
            Check(states.All(s => s.State is not (AgentState.WaitingForApproval or AgentState.WaitingForInput) && !s.Request), "No observer may see a late request while stopping.");
            Check(session.Responses.Count == 2, "Settling does not decline anything further.");
        }
        Console.WriteLine("Controller lifecycle and UI-context checks passed.");
    }
    private sealed class DeclineHarness : IAgentHarness
    {
        public DeclineSession Session { get; } = new();
        public Task<HarnessDescription> DescribeAsync(AgentLocation location, CancellationToken cancellationToken = default) => Task.FromResult(new HarnessDescription(true, "fixture", []));
        public Task<IAgentSession> OpenAsync(AgentOptions options, CancellationToken cancellationToken = default) => Task.FromResult<IAgentSession>(Session);
    }
    /// <summary>Holds native Stop open like a provider draining a turn, and records every response it receives.</summary>
    private sealed class DeclineSession : IAgentSession
    {
        private readonly System.Threading.Channels.Channel<AgentEvent> _events = System.Threading.Channels.Channel.CreateUnbounded<AgentEvent>();
        public Guid Turn;
        public TaskCompletionSource StopRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<AgentResponse> Responses { get; } = [];
        public void Emit(AgentEvent item) => _events.Writer.TryWrite(item);
        public IAsyncEnumerable<AgentEvent> Events(CancellationToken cancellationToken = default) => _events.Reader.ReadAllAsync(cancellationToken);
        public Task SubmitAsync(AgentPrompt prompt, CancellationToken cancellationToken = default) { Turn = prompt.Turn; return Task.CompletedTask; }
        public Task RespondAsync(AgentResponse response, CancellationToken cancellationToken = default) { lock (Responses) Responses.Add(response); return Task.CompletedTask; }
        public Task InterruptAsync(Guid turn, CancellationToken cancellationToken = default) => StopRelease.Task;
        public ValueTask DisposeAsync() { _events.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
    private sealed class Terminal : IHostTerminal
    {
        public Task<TerminalExecutionResult> ExecuteAsync(TerminalExecutionRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new TerminalExecutionResult(TerminalExecutionStatus.Completed));
    }
    private sealed class LifecycleHarness : IAgentHarness
    {
        public bool BlockDescribe, BlockOpen;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken StartupToken;
        public List<FakeSession> Sessions { get; } = [];
        public IHostTerminal? Binding;
        public async Task<HarnessDescription> DescribeAsync(AgentLocation location, CancellationToken cancellationToken = default)
        {
            if (BlockDescribe) { StartupToken = cancellationToken; Entered.SetResult(); await Release.Task; }
            return new(true, "fixture", []);
        }
        public async Task<IAgentSession> OpenAsync(AgentOptions options, CancellationToken cancellationToken = default)
        {
            if (BlockOpen) { StartupToken = cancellationToken; Entered.SetResult(); await Release.Task; }
            Binding = options.Terminal;
            var session = new FakeSession(); Sessions.Add(session); return session;
        }
    }
}
