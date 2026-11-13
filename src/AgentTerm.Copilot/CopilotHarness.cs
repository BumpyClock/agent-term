using Microsoft.Extensions.AI;
using System.ComponentModel;
using AgentTerm.Agents;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
#pragma warning disable GHCP001
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AgentTerm.Copilot.Smoke")]
namespace AgentTerm.Copilot;

public sealed class CopilotHarness : IAgentHarness
{
    private const string SignInMessage = "Sign in with Copilot CLI before using Agent mode. Dashboard gh sign-in is separate.";
    private static void Validate(AgentLocation location)
    {
        if (location.WslDistribution is not null) throw new NotSupportedException("Agent tools cannot run in WSL yet. Open a local Windows pane.");
        if (!Path.IsPathFullyQualified(location.Path) || !Directory.Exists(location.Path))
            throw new InvalidOperationException("A reported, existing shell directory is required.");
    }
    private static CopilotClient Create(AgentLocation location) => new(new CopilotClientOptions
    {
        WorkingDirectory = location.Path,
        LogLevel = CopilotLogLevel.None,
        UseLoggedInUser = true,
    });
    public async Task<HarnessDescription> DescribeAsync(AgentLocation location, CancellationToken cancellationToken = default)
    {
        try
        {
            Validate(location);
            await using var client = Create(location);
            await client.StartAsync(cancellationToken);
            var auth = await client.GetAuthStatusAsync(cancellationToken);
            if (!auth.IsAuthenticated) return new(false, SignInMessage, []);
            var models = await client.ListModelsAsync(cancellationToken);
            return new(true, "Copilot ready", models.Select(m => m.Id).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(false, $"Copilot unavailable. {ex.Message}", []); }
    }
    public async Task<IAgentSession> OpenAsync(AgentOptions options, CancellationToken cancellationToken = default)
    {
        Validate(options.Location);
        var client = Create(options.Location);
        try
        {
            await client.StartAsync(cancellationToken);
            var auth = await client.GetAuthStatusAsync(cancellationToken);
            if (!auth.IsAuthenticated) throw new InvalidOperationException(SignInMessage);
            var adapter = new Session(client);
            await adapter.OpenAsync(options, cancellationToken);
            return adapter;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { await client.DisposeAsync(); throw; }
        catch (Exception ex)
        {
            await client.DisposeAsync();
            throw new InvalidOperationException($"Copilot unavailable. {ex.Message}", ex);
        }
    }
    internal sealed class Session(CopilotClient? client,
        Func<AgentPrompt, CancellationToken, Task>? send = null,
        Func<CancellationToken, Task>? abort = null,
        TimeSpan? drainTimeout = null,
        IHostTerminal? terminal = null,
        Func<Task>? approvalContinuation = null) : IAgentSession
    {
        private readonly AgentEventMailbox _events = new();
        internal (int Events, int Characters) PendingEvents => _events.Backlog;
        internal const int ReconciliationLimit = 1024;
        private readonly Dictionary<Guid, TaskCompletionSource<AgentResponse>> _requests = [];
        private readonly CancellationTokenSource _lifetime = new();
        private CopilotSession? _native;
        private readonly object _gate = new();
        private Task? _dispose;
        private bool _unusable;
        private Turn? _active;
        private Guid? _turn => _active?.Id;
        private sealed class Turn(Guid id, CancellationToken lifetime) : IDisposable
        {
            public Guid Id { get; } = id;
            public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            public TaskCompletionSource Idle { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task? Stop;
            public string? Failure;
            public Dictionary<string, ResponseReconciliation> Messages { get; } = [];
            public int MessageIdCharacters;
            public void Dispose()
            {
                foreach (var response in Messages.Values) response.Dispose();
                Messages.Clear();
                Cancellation.Dispose();
            }
        }
        private IHostTerminal? _terminal = terminal;
        private readonly HashSet<Task<TerminalExecutionResult>> _executions = [];
        private IDisposable? _subscription;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _toolNames = new();
        public async Task OpenAsync(AgentOptions options, CancellationToken cancellationToken)
        {
            _terminal = options.Terminal;
            _native = await client!.CreateSessionAsync(new SessionConfig
            {
                WorkingDirectory = options.Location.Path,
                Model = options.Model,
                Streaming = true,
                EnableManagedSettings = true,
                AvailableTools = ["run_in_terminal", "ask_user"],
                Tools = [CopilotTool.DefineTool(
                    ([Description("Exact PowerShell command to execute in the invoking pane, preserving shell state.")] string command, ToolInvocation invocation) => RunInTerminalAsync(command, invocation.ToolCallId),
                    toolOptions: new CopilotToolOptions { SkipPermission = true, Defer = CopilotToolDefer.Never },
                    factoryOptions: new AIFunctionFactoryOptions { Name = "run_in_terminal", Description = "Execute in the user's EXISTING pane PowerShell. Cwd returned by this tool is authoritative. Output is bounded terminal screen text when authenticated command boundaries remain intact, with a streamed transcript fallback when they do not. Use this tool for all shell and file operations. No independent shell or native file tools are available. Interactive programs require deliberate user input handoff." })],
                OnPermissionRequest = async (request, invocation) =>
                {
                    (string Detail, string? CallId) gate = request switch
                    {
                        PermissionRequestShell shell => (shell.FullCommandText, shell.ToolCallId),
                        PermissionRequestWrite write => ($"Write {write.FileName}\n{write.Diff}", write.ToolCallId),
                        PermissionRequestRead read => ($"Read {read.Path}", read.ToolCallId),
                        PermissionRequestUrl url => ($"Access URL {url.Url}", url.ToolCallId),
                        PermissionRequestMcp mcp => ($"{mcp.ServerDisplayName ?? mcp.ServerName} / {mcp.ToolName}\n{mcp.Args}", mcp.ToolCallId),
                        PermissionRequestCustomTool tool => ($"{tool.ToolName}\n{tool.ToolDescription}\n{tool.Args}", tool.ToolCallId),
                        _ => ($"{request.Kind} permission. Review this tool activity before approving.", null),
                    };
                    return await RequestPermissionAsync(gate.Detail, gate.CallId) ? PermissionDecision.ApproveOnce() : PermissionDecision.Reject("User declined.");
                },
                OnUserInputRequest = async (request, invocation) =>
                {
                    var choices = request.Choices?.ToArray() ?? [];
                    var reply = await RequestInputAsync(request.Question, choices, request.AllowFreeform ?? true);
                    return new UserInputResponse { Answer = reply.Answer ?? "", WasFreeform = !choices.Contains(reply.Answer) };
                },
            }, cancellationToken);
            _subscription = _native.On<SessionEvent>(OnEvent);
        }
        internal async Task<bool> RequestPermissionAsync(string detail, string? callId) =>
            (await RequestAsync((turn, id) => new ApprovalRequest(turn, id, detail, callId))).Approved;
        internal Task<AgentResponse> RequestInputAsync(string question, string[] choices, bool allowFreeform) =>
            RequestAsync((turn, id) => new QuestionRequest(turn, id, question, choices, allowFreeform));
        internal async Task<TerminalExecutionResult> RunInTerminalAsync(string command, string callId)
        {
            Turn owner;
            Task<AgentResponse> approval;
            lock (_gate)
            {
                if (_terminal is null) return new(TerminalExecutionStatus.Unsupported);
                if (!Admits(out var active)) return new(TerminalExecutionStatus.Denied);
                owner = active;
                approval = RequestAsync((turn, id) => new ApprovalRequest(turn, id, command, callId));
            }
            var reply = await approval;
            if (!reply.Approved) return new(TerminalExecutionStatus.Denied);
            if (approvalContinuation is not null) await approvalContinuation();
            Task<TerminalExecutionResult> execution;
            lock (_gate)
            {
                if (_dispose is not null || _unusable || !ReferenceEquals(_active, owner) || owner.Stop is not null || owner.Cancellation.IsCancellationRequested)
                    return new(TerminalExecutionStatus.Denied);
                execution = _terminal.ExecuteAsync(new(command), owner.Cancellation.Token);
                _executions.Add(execution);
                Emit(new ToolExecuting(owner.Id, callId));
            }
            try { return await execution; }
            finally { lock (_gate) _executions.Remove(execution); }
        }
        private bool Admits([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Turn? owner)
        {
            owner = _active;
            return _dispose is null && !_unusable && owner is { Stop: null } && !owner.Cancellation.IsCancellationRequested;
        }
        private Task<AgentResponse> RequestAsync(Func<Guid, Guid, AgentEvent> create)
        {
            lock (_gate)
            {
                if (!Admits(out var owner)) return Task.FromResult(new AgentResponse(Guid.Empty));
                var id = Guid.NewGuid();
                var completion = new TaskCompletionSource<AgentResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_requests) _requests.Add(id, completion);
                Emit(create(owner.Id, id));
                return AwaitAsync(id, completion.Task);
            }
        }
        private async Task<AgentResponse> AwaitAsync(Guid id, Task<AgentResponse> reply)
        {
            try { return await reply.WaitAsync(_lifetime.Token); }
            finally { lock (_requests) _requests.Remove(id); }
        }
        private void DeclineRequests()
        {
            lock (_requests) foreach (var request in _requests.Values) request.TrySetResult(new(Guid.Empty));
        }
        private void Emit(AgentEvent item)
        {
            if (!_events.TryWrite(item) && !_unusable)
                FailDelivery("Copilot output exceeded the pending event budget. The session was stopped; reopen it to continue.");
        }
        private void FailDelivery(string message)
        {
            _unusable = true;
            DeclineRequests();
            if (_active is { } owner)
            {
                owner.Failure ??= message;
                owner.Stop ??= Task.Run(() => StopAsync(owner, default));
            }
            _events.Complete(new IOException(message));
        }
        private ResponseReconciliation? Response(Turn owner, string id)
        {
            if (owner.Messages.TryGetValue(id, out var response)) return response;
            if (owner.Messages.Count == ReconciliationLimit || id.Length > 64 * 1024 - owner.MessageIdCharacters)
            {
                FailDelivery("Copilot exceeded the response count budget for one turn. The session was stopped; reopen it to continue.");
                return null;
            }
            response = new ResponseReconciliation();
            owner.Messages.Add(id, response);
            owner.MessageIdCharacters += id.Length;
            return response;
        }
        internal void OnEvent(SessionEvent item)
        {
            lock (_gate) OnEventCore(item);
        }
        private void OnEventCore(SessionEvent item)
        {
            if (_turn is not { } turn) return;
            // An overloaded stream must still observe idle so Stop can release native ownership.
            if (_unusable && item is not SessionIdleEvent) return;
            if (item is ToolExecutionStartEvent started)
            {
                _toolNames[started.Data.ToolCallId] = started.Data.ToolName;
                Emit(CopilotToolPresentation.Start(turn, started.Data.ToolCallId, started.Data.ToolName, started.Data.Arguments));
                return;
            }
            if (item is ToolExecutionCompleteEvent completed)
            {
                _toolNames.TryRemove(completed.Data.ToolCallId, out var name);
                var presentation = CopilotToolPresentation.Complete(name ?? "", completed.Data.Result?.Content, completed.Data.Success);
                Emit(new ToolOutput(turn, completed.Data.ToolCallId, presentation.Text));
                Emit(new ToolCompleted(turn, completed.Data.ToolCallId, presentation.Status));
                return;
            }
            var owner = _active!;
            if (item is SessionErrorEvent error)
            {
                owner.Failure ??= error.Data.Message;
                owner.Stop ??= Task.Run(() => StopAsync(owner, default));
                return;
            }
            if (item is AssistantMessageDeltaEvent delta)
            {
                var id = delta.Data.MessageId;
                var prior = Response(owner, id);
                if (prior is null) return;
                prior.Append(delta.Data.DeltaContent);
                Emit(new TextDelta(turn, delta.Data.DeltaContent));
                return;
            }
            if (item is AssistantMessageEvent message)
            {
                var id = message.Data.MessageId;
                var content = message.Data.Content;
                var prior = Response(owner, id);
                if (prior is null) return;
                if (prior.IsPrefixOf(content))
                {
                    if (content.Length > prior.Length) Emit(new TextDelta(turn, content[prior.Length..]));
                }
                else Emit(new TextDelta(turn, "\n[Final response differs from streamed text]\n" + content));
                prior.Replace(content);
                return;
            }
            AgentEvent? normalized = item switch
            {
                SessionIdleEvent => owner.Failure is { } failure ? new TurnFailed(turn, failure) : owner.Stop is not null ? new TurnInterrupted(turn) : new TurnCompleted(turn),
                _ => null,
            };
            if (normalized is TurnCompleted or TurnInterrupted or TurnFailed)
            {
                DeclineRequests();
                _active = null;
                owner.Idle.TrySetResult();
                owner.Dispose();
            }
            if (normalized is not null) Emit(normalized);
        }
        public IAsyncEnumerable<AgentEvent> Events(CancellationToken cancellationToken = default) => _events.ReadAllAsync(cancellationToken);
        public async Task SubmitAsync(AgentPrompt prompt, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                if (_unusable) throw new InvalidOperationException("Reopen the unusable Copilot session.");
                if (_active is not null) throw new InvalidOperationException("A native turn is still active.");
                _active = new(prompt.Turn, _lifetime.Token);
                _toolNames.Clear();
            }
            try
            {
                if (send is not null) await send(prompt, cancellationToken);
                else await _native!.SendAsync(new MessageOptions { Prompt = prompt.Text }, cancellationToken);
            }
            catch
            {
                lock (_gate) _unusable = true;
                _events.Complete(new IOException("Copilot submission failed. Reopen the session."));
                throw;
            }
        }
        public Task RespondAsync(AgentResponse response, CancellationToken cancellationToken = default)
        {
            lock (_requests) if (_requests.TryGetValue(response.Request, out var request)) request.TrySetResult(response);
            return Task.CompletedTask;
        }
        public Task InterruptAsync(Guid turn, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_active is not { } owner || owner.Id != turn) return Task.CompletedTask;
                return owner.Stop ??= Task.Run(() => StopAsync(owner, cancellationToken));
            }
        }
        private async Task StopAsync(Turn owner, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(drainTimeout ?? TimeSpan.FromSeconds(30));
            try
            {
                Task<TerminalExecutionResult>[] executions;
                lock (_gate)
                {
                    if (!ReferenceEquals(_active, owner)) return;
                    DeclineRequests();
                    owner.Cancellation.Cancel();
                    executions = _executions.ToArray();
                }
                if ((await Task.WhenAll(executions).WaitAsync(timeout.Token)).Any(result => result.Status == TerminalExecutionStatus.InterruptionFailed))
                    throw new InvalidOperationException("Terminal interruption failed. Ownership retained until shell readiness.");
                Task aborting;
                lock (_gate)
                {
                    if (!ReferenceEquals(_active, owner)) return;
                    aborting = abort is not null ? abort(timeout.Token) : _native!.AbortAsync(timeout.Token);
                }
                await aborting.WaitAsync(timeout.Token);
                await owner.Idle.Task.WaitAsync(timeout.Token);
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(_active, owner)) return;
                    _unusable = true;
                    var message = owner.Failure is { } failure
                        ? $"Copilot failed. {failure} Native idle was not confirmed. Reopen the session."
                        : "Copilot did not become idle. Reopen the session.";
                    _events.Complete(new IOException(message, ex));
                }
            }
        }
        public ValueTask DisposeAsync()
        {
            lock (_gate) return new(_dispose ??= DisposeCoreAsync());
        }
        private async Task DisposeCoreAsync()
        {
            lock (_gate)
            {
                _active?.Cancellation.Cancel();
                _active?.Idle.TrySetCanceled();
                _active?.Dispose();
                _active = null;
            }
            _lifetime.Cancel(); _subscription?.Dispose();
            Task<TerminalExecutionResult>[] executions;
            lock (_gate) executions = _executions.ToArray();
            try { await Task.WhenAll(executions); } catch (OperationCanceledException) { }
            if (_native is not null) await _native.DisposeAsync();
            if (client is not null) await client.DisposeAsync();
            _events.Complete(); _lifetime.Dispose();
        }
    }
}
