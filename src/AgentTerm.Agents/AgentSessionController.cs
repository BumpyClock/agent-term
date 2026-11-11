namespace AgentTerm.Agents;

/// <summary>One pane's admission, normalized transcript and request lifecycle. Call from one UI context.</summary>
public sealed class AgentSessionController(IAgentHarness harness, Func<IHostTerminal?>? terminal = null) : IAsyncDisposable
{
    private IAgentSession? _session;
    private Task? _reader;
    private readonly CancellationTokenSource _lifetime = new();
    public AgentTimeline Timeline { get; } = new();
    private Guid? _turn;
    public AgentState State { get; private set; } = AgentState.Idle;
    public AgentLocation? Location { get; private set; }
    private readonly Queue<AgentEvent> _requests = new();
    public AgentEvent? Request => _requests.TryPeek(out var request) ? request : null;
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
        (CurrentTask ?? BeginTask()).Add(turn, text);
        State = AgentState.Starting;
        _turn = turn;
        Timeline.Message(turn, MessageRole.User, text);
        Changed?.Invoke();
        try
        {
            if (_session is not null && Location != location)
            {
                await _session.DisposeAsync();
                if (_reader is not null) await _reader;
                _session = null;
            }
            if (_session is null)
            {
                var description = await harness.DescribeAsync(location, _lifetime.Token);
                if (_lifetime.IsCancellationRequested) return false;
                if (!description.Available) throw new InvalidOperationException(description.Message);
                var opened = await harness.OpenAsync(new(location, model, terminal?.Invoke()), _lifetime.Token);
                if (_lifetime.IsCancellationRequested) { await opened.DisposeAsync(); return false; }
                _session = opened;
                Location = location;
                _reader = ReadAsync(_session);
            }
            State = AgentState.Running;
            Changed?.Invoke();
            await _session.SubmitAsync(new(turn, text), _lifetime.Token);
            return true;
        }
        catch (Exception) when (_lifetime.IsCancellationRequested) { return false; }
        catch (Exception ex) when (State != AgentState.Disposed)
        {
            Timeline.Settle(turn);
            _turn = null;
            State = AgentState.Failed;
            Timeline.Message(turn, MessageRole.Failure, ex.Message);
            Changed?.Invoke();
            return false;
        }
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
                    case TurnInterrupted: Timeline.Message(item.Turn, MessageRole.Notice, "Interrupted."); Finish(AgentState.Idle); break;
                }
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (State != AgentState.Disposed)
        {
            Timeline.Message(_turn ?? Guid.Empty, MessageRole.Failure, ex.Message); Finish(AgentState.Failed); Changed?.Invoke();
        }
    }
    private void Finish(AgentState state) { if (_turn is { } turn) Timeline.Settle(turn); _turn = null; _requests.Clear(); State = state; }
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
    public async Task InterruptAsync()
    {
        if (_turn is not { } turn || _session is null) return;
        State = AgentState.Interrupting; _requests.Clear(); Changed?.Invoke();
        try { await _session.InterruptAsync(turn, _lifetime.Token); }
        catch (Exception ex)
        {
            await _session.DisposeAsync();
            if (_reader is not null) await _reader;
            _session = null;
            Timeline.Message(turn, MessageRole.Failure, $"Interruption failed. {ex.Message}");
            Finish(AgentState.Failed); Changed?.Invoke();
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (State == AgentState.Disposed) return;
        Finish(AgentState.Disposed); _lifetime.Cancel();
        if (_session is not null) await _session.DisposeAsync();
        if (_reader is not null) await _reader;
        Changed?.Invoke();
    }
}
