namespace AgentTerm.Agents;

/// <summary>One pane's admission, normalized transcript and request lifecycle. Call from one UI context.</summary>
public sealed class AgentSessionController(IAgentHarness harness, Func<IHostTerminal?>? terminal = null) : IAsyncDisposable
{
    private IAgentSession? _session;
    private Task? _reader;
    private readonly CancellationTokenSource _lifetime = new();
    public AgentTimeline Timeline { get; } = new();
    private Guid? _turn => _operation?.Id;
    private IHostTerminal? _boundTerminal;
    private TurnOperation? _operation;
    private sealed class TurnOperation(Guid id, CancellationToken lifetime)
    {
        public Guid Id { get; } = id;
        public long Started { get; } = System.Diagnostics.Stopwatch.GetTimestamp();
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        public TaskCompletionSource Submitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Stop;
    }
    public AgentState State { get; private set; } = AgentState.Idle;
    public AgentLocation? Location { get; private set; }
    private readonly Queue<AgentEvent> _requests = new();
    public AgentEvent? Request => _requests.TryPeek(out var request) ? request : null;
    /// <summary>Any queued approval, not only the shown one, names this call. A row must not claim it is running while it waits.</summary>
    public bool AwaitsApproval(string callId) => _requests.Any(r => r is ApprovalRequest { CallId: { } id } && id == callId);
    public bool HasActiveTurn => _operation is not null;
    public bool CanInterrupt => _operation is { Stop: null } && (State == AgentState.Starting || _session is not null) && State != AgentState.Disposed;
    public TimeSpan TurnElapsed => _operation is { } operation ? System.Diagnostics.Stopwatch.GetElapsedTime(operation.Started) : TimeSpan.Zero;
    public bool CanSubmit => State is AgentState.Idle or AgentState.Failed;
    public event Action? Changed;
    public const int TaskLimit = 32;
    private readonly List<AgentTask> _tasks = [];
    /// <summary>Bounded, oldest first. Tasks share this pane's harness session and therefore its conversation context.</summary>
    public IReadOnlyList<AgentTask> Tasks => _tasks;
    /// <summary>Receives follow-up turns. Null until the first task.</summary>
    public AgentTask? CurrentTask { get; private set; }
    public AgentTask? RunningTask => _turn is { } turn ? _tasks.LastOrDefault(t => t.Contains(turn)) : null;
    public AgentTask BeginTask()
    {
        var task = new AgentTask();
        _tasks.Add(task);
        while (_tasks.Count > TaskLimit) _tasks.RemoveAt(0);
        CurrentTask = task;
        Changed?.Invoke();
        return task;
    }
    public void ActivateTask(AgentTask task)
    {
        if (!_tasks.Contains(task) || CurrentTask == task) return;
        CurrentTask = task;
        Changed?.Invoke();
    }
    public async Task<bool> SubmitAsync(string text, AgentLocation location, string? model = null)
    {
        if (!CanSubmit || string.IsNullOrWhiteSpace(text)) return false;
        var turn = Guid.NewGuid();
        var operation = _operation = new TurnOperation(turn, _lifetime.Token);
        var token = operation.Cancellation.Token;
        var invokingTerminal = terminal?.Invoke();
        (CurrentTask ?? BeginTask()).Add(turn, text);
        State = AgentState.Starting;
        Timeline.Message(turn, MessageRole.User, text);
        Changed?.Invoke();
        try
        {
            if (_session is not null && (Location != location || !ReferenceEquals(_boundTerminal, invokingTerminal)))
            {
                await _session.DisposeAsync();
                if (_reader is not null) await _reader;
                _session = null;
            }
            if (_session is null)
            {
                var description = await harness.DescribeAsync(location, token);
                token.ThrowIfCancellationRequested();
                if (!description.Available) throw new InvalidOperationException(description.Message);
                var opened = await harness.OpenAsync(new(location, model, invokingTerminal), token);
                if (token.IsCancellationRequested) { await opened.DisposeAsync(); token.ThrowIfCancellationRequested(); }
                _session = opened;
                Location = location;
                _boundTerminal = invokingTerminal;
                _reader = ReadAsync(_session);
            }
            token.ThrowIfCancellationRequested();
            State = AgentState.Running;
            Changed?.Invoke();
            await _session.SubmitAsync(new(turn, text), token);
            return true;
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            if (State != AgentState.Disposed)
            {
                RecordStopped(turn);
                Finish(AgentState.Idle, WorkStatus.Cancelled); Changed?.Invoke();
            }
            return false;
        }
        catch (Exception ex) when (State != AgentState.Disposed)
        {
            if (_turn != turn) return false;
            var failedSession = _session;
            _session = null;
            if (failedSession is not null) await failedSession.DisposeAsync();
            if (_reader is not null) await _reader;
            Finish(AgentState.Failed);
            Timeline.Message(turn, MessageRole.Failure, ex.Message);
            Changed?.Invoke();
            return false;
        }
        finally { operation.Submitted.TrySetResult(); }
    }
    private async Task ReadAsync(IAgentSession session)
    {
        try
        {
            await foreach (var item in session.Events(_lifetime.Token))
            {
                if (State == AgentState.Disposed || item.Turn != _turn) continue;
                Timeline.Apply(item);
                switch (item)
                {
                    case ApprovalRequest approval: _requests.Enqueue(approval); UpdateRequestState(); break;
                    case QuestionRequest question: _requests.Enqueue(question); UpdateRequestState(); break;
                    case TurnFailed failed: Timeline.Message(item.Turn, MessageRole.Failure, failed.Message); Finish(AgentState.Failed); break;
                    case TurnCompleted: Finish(AgentState.Idle); break;
                    case TurnInterrupted: RecordStopped(item.Turn); Finish(AgentState.Idle, WorkStatus.Cancelled); break;
                }
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (State != AgentState.Disposed)
        {
            if (!ReferenceEquals(_session, session)) return;
            _session = null;
            await session.DisposeAsync();
            Timeline.Message(_turn ?? Guid.Empty, MessageRole.Failure, ex.Message); Finish(AgentState.Failed); Changed?.Invoke();
        }
    }
    private void RecordStopped(Guid turn)
    {
        var partial = Timeline.Entries.OfType<TimelineMessage>().Any(m => m.Turn == turn && m.Role == MessageRole.Assistant && m.Text.Length > 0);
        Timeline.Message(turn, MessageRole.Cancelled, partial ? "Stopped. Partial reply kept." : "Stopped before any reply.");
    }

    private void Finish(AgentState state, WorkStatus? outcome = null)
    {
        if (RunningTask is { } task) task.LastOutcome = outcome ?? (state == AgentState.Failed ? WorkStatus.Failed : WorkStatus.Succeeded);
        if (_turn is { } turn) Timeline.Settle(turn);
        _operation?.Cancellation.Dispose(); _operation = null;
        _requests.Clear(); State = state;
    }
    private void UpdateRequestState() => State = Request switch
    {
        ApprovalRequest => AgentState.WaitingForApproval,
        QuestionRequest => AgentState.WaitingForInput,
        _ => AgentState.Running,
    };
    public async Task<bool> RespondAsync(AgentResponse response)
    {
        var id = Request switch { ApprovalRequest a => a.Request, QuestionRequest q => q.Request, _ => (Guid?)null };
        if (id != response.Request || _session is null) return false;
        var pending = Request;
        try
        {
            await _session.RespondAsync(response, _lifetime.Token);
            if (Request == pending)
            {
                // Pending requests render beside the prompt. An approval's outcome shows on its tool call; an answer is recorded here.
                if (pending is QuestionRequest question) Timeline.Message(question.Turn, MessageRole.Notice, question.Question + "\n" + response.Answer);
                _requests.Dequeue();
                UpdateRequestState();
                Changed?.Invoke();
            }
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            if (State != AgentState.Disposed)
            {
                Timeline.Message(_turn ?? Guid.Empty, MessageRole.Failure, $"Could not send response. {ex.Message}");
                Changed?.Invoke();
            }
            return false;
        }
    }
    public Task InterruptAsync()
    {
        if (_turn is not { } turn || _operation is not { } operation) return Task.CompletedTask;
        if (operation.Stop is { } pending) return pending.Task;
        var completion = operation.Stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = CompleteStopAsync();
        return completion.Task;
        async Task CompleteStopAsync()
        {
            try { await StopAsync(turn, operation); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }
    }
    private async Task StopAsync(Guid turn, TurnOperation operation)
    {
        var starting = State == AgentState.Starting;
        State = AgentState.Interrupting; _requests.Clear(); Changed?.Invoke();
        if (starting)
        {
            operation.Cancellation.Cancel();
            await operation.Submitted.Task;
            return;
        }
        if (_session is not { } session) return;
        try { await session.InterruptAsync(turn, _lifetime.Token); }
        catch (Exception ex)
        {
            // An old Stop must never recover a session after its turn has finished.
            if (_turn != turn || State == AgentState.Disposed) return;
            await session.DisposeAsync();
            if (_reader is not null) await _reader;
            if (ReferenceEquals(_session, session)) _session = null;
            Timeline.Message(turn, MessageRole.Failure, $"Interruption failed. {ex.Message}");
            Finish(AgentState.Failed); Changed?.Invoke();
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (State == AgentState.Disposed) return;
        _lifetime.Cancel(); Finish(AgentState.Disposed);
        if (_session is not null) await _session.DisposeAsync();
        if (_reader is not null) await _reader;
        Changed?.Invoke();
    }
}
