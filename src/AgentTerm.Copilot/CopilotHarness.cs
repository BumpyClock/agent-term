using System.Threading.Channels;
using Microsoft.Extensions.AI;
using System.ComponentModel;
using AgentTerm.Agents;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
#pragma warning disable GHCP001 // Published SDK permission decisions and managed policy are experimental.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AgentTerm.Copilot.Smoke")]
namespace AgentTerm.Copilot;

public sealed class CopilotHarness : IAgentHarness
{
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
            if (!auth.IsAuthenticated) return new(false, "Sign in with Copilot CLI before using Agent mode. Dashboard gh sign-in is separate.", []);
            var models = await client.ListModelsAsync(cancellationToken);
            return new(true, "Copilot ready", models.Select(m => m.Id).ToArray());
        }
        catch (Exception ex) { return new(false, $"Copilot unavailable. {ex.Message}", []); }
    }
    public async Task<IAgentSession> OpenAsync(AgentOptions options, CancellationToken cancellationToken = default)
    {
        Validate(options.Location);
        var client = Create(options.Location);
        try
        {
            await client.StartAsync(cancellationToken);
            var adapter = new Session(client);
            await adapter.OpenAsync(options, cancellationToken);
            return adapter;
        }
        catch { await client.DisposeAsync(); throw; }
    }
    internal sealed class Session(CopilotClient? client,
        Func<AgentPrompt, CancellationToken, Task>? send = null,
        Func<CancellationToken, Task>? abort = null,
        TimeSpan? drainTimeout = null) : IAgentSession
    {
        private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();
        private readonly Dictionary<Guid, TaskCompletionSource<AgentResponse>> _requests = [];
        private readonly CancellationTokenSource _lifetime = new();
        private CopilotSession? _native;
        private readonly object _gate = new();
        private Task? _dispose;
        private bool _unusable;
        private Turn? _active;
        private Guid? _turn => _active?.Id;
        private sealed class Turn(Guid id, CancellationToken lifetime)
        {
            public Guid Id { get; } = id;
            public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            public TaskCompletionSource Idle { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task? Stop;
            public string? Failure;
            public Dictionary<string, string> Messages { get; } = [];
        }
        private IHostTerminal? _terminal;
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
                    async ([Description("Exact PowerShell command to execute in the invoking pane, preserving shell state.")] string command, ToolInvocation invocation) =>
                    {
                        if (_terminal is null) return new TerminalExecutionResult(TerminalExecutionStatus.Unsupported);
                        var id = Guid.NewGuid();
                        var turn = _turn ?? Guid.Empty;
                        // The SDK binds ToolInvocation outside the model's schema. Its call id matches ToolExecutionStartEvent.
                        var reply = await RequestAsync(id, new ApprovalRequest(turn, id, command, invocation.ToolCallId));
                        if (!reply.Approved) return new TerminalExecutionResult(TerminalExecutionStatus.Denied);
                        Task<TerminalExecutionResult> execution;
                        lock (_executions)
                        {
                            execution = _terminal.ExecuteAsync(new(command), _active?.Cancellation.Token ?? _lifetime.Token);
                            _executions.Add(execution);
                        }
                        _events.Writer.TryWrite(new ToolExecuting(turn, invocation.ToolCallId));
                        try { return await execution; }
                        finally { lock (_executions) _executions.Remove(execution); }
                    },
                    toolOptions: new CopilotToolOptions { SkipPermission = true, Defer = CopilotToolDefer.Never },
                    factoryOptions: new AIFunctionFactoryOptions { Name = "run_in_terminal", Description = "Execute in the user's EXISTING pane PowerShell. Cwd returned by this tool is authoritative. Output is bounded ConPTY terminal text with ANSI stripped; cursor redraws are not reconstructed. Use this tool for all shell and file operations. No independent shell or native file tools are available. Interactive programs require deliberate user input handoff." })],
                OnPermissionRequest = async (request, invocation) =>
                {
                    var id = Guid.NewGuid();
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
                    // The runtime executes after approval without reporting a start, so such a row stays "pending", never "running".
                    var reply = await RequestAsync(id, new ApprovalRequest(_turn ?? Guid.Empty, id, gate.Detail, gate.CallId));
                    return reply.Approved ? PermissionDecision.ApproveOnce() : PermissionDecision.Reject("User declined.");
                },
                OnUserInputRequest = async (request, invocation) =>
                {
                    var id = Guid.NewGuid();
                    var choices = request.Choices?.ToArray() ?? [];
                    var reply = await RequestAsync(id, new QuestionRequest(_turn ?? Guid.Empty, id, request.Question, choices, request.AllowFreeform ?? true));
                    return new UserInputResponse { Answer = reply.Answer ?? "", WasFreeform = !choices.Contains(reply.Answer) };
                },
            }, cancellationToken);
            _subscription = _native.On<SessionEvent>(OnEvent);
        }
        private async Task<AgentResponse> RequestAsync(Guid id, AgentEvent item)
        {
            var completion = new TaskCompletionSource<AgentResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_requests) _requests.Add(id, completion);
            _events.Writer.TryWrite(item);
            try { return await completion.Task.WaitAsync(_lifetime.Token); }
            finally { lock (_requests) _requests.Remove(id); }
        }
        internal void OnEvent(SessionEvent item)
        {
            lock (_gate) OnEventCore(item);
        }
        private void OnEventCore(SessionEvent item)
        {
            if (_turn is not { } turn) return;
            if (item is ToolExecutionStartEvent started)
            {
                _toolNames[started.Data.ToolCallId] = started.Data.ToolName;
                _events.Writer.TryWrite(CopilotToolPresentation.Start(turn, started.Data.ToolCallId, started.Data.ToolName, started.Data.Arguments));
                return;
            }
            if (item is ToolExecutionCompleteEvent completed)
            {
                _toolNames.TryRemove(completed.Data.ToolCallId, out var name);
                var presentation = CopilotToolPresentation.Complete(name ?? "", completed.Data.Result?.Content, completed.Data.Success);
                _events.Writer.TryWrite(new ToolOutput(turn, completed.Data.ToolCallId, presentation.Text));
                _events.Writer.TryWrite(new ToolCompleted(turn, completed.Data.ToolCallId, presentation.Status));
                return;
            }
            var owner = _active!;
            if (item is SessionErrorEvent error)
            {
                owner.Failure ??= error.Data.Message;
                // Error is not native idle. Drain before exposing a retry to the controller.
                owner.Stop ??= Task.Run(() => StopAsync(owner, default));
                return;
            }
            if (item is AssistantMessageDeltaEvent delta)
            {
                var id = delta.Data.MessageId;
                owner.Messages.TryGetValue(id, out var prior);
                owner.Messages[id] = (prior ?? "") + delta.Data.DeltaContent;
                _events.Writer.TryWrite(new TextDelta(turn, delta.Data.DeltaContent));
                return;
            }
            if (item is AssistantMessageEvent message)
            {
                var id = message.Data.MessageId;
                var content = message.Data.Content;
                owner.Messages.TryGetValue(id, out var prior);
                if (prior is null) _events.Writer.TryWrite(new TextDelta(turn, content));
                else if (content.StartsWith(prior, StringComparison.Ordinal) && content.Length > prior.Length)
                    _events.Writer.TryWrite(new TextDelta(turn, content[prior.Length..]));
                owner.Messages[id] = content;
                return;
            }
            AgentEvent? normalized = item switch
            {
                SessionIdleEvent => owner.Failure is { } failure ? new TurnFailed(turn, failure) : owner.Stop is not null ? new TurnInterrupted(turn) : new TurnCompleted(turn),
                _ => null,
            };
            if (normalized is TurnCompleted or TurnInterrupted or TurnFailed)
            {
                _active = null;
                owner.Idle.TrySetResult();
                owner.Cancellation.Dispose();
            }
            if (normalized is not null) _events.Writer.TryWrite(normalized);
        }
        public IAsyncEnumerable<AgentEvent> Events(CancellationToken cancellationToken = default) => _events.Reader.ReadAllAsync(cancellationToken);
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
                // A failed send can have reached the provider. Do not reuse an ambiguous session.
                lock (_gate) _unusable = true;
                _events.Writer.TryComplete(new IOException("Copilot submission failed. Reopen the session."));
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
                lock (_gate)
                {
                    if (!ReferenceEquals(_active, owner)) return;
                    lock (_requests) foreach (var request in _requests.Values) request.TrySetResult(new(Guid.Empty));
                    owner.Cancellation.Cancel();
                }
                Task<TerminalExecutionResult>[] executions;
                lock (_executions) executions = _executions.ToArray();
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
                    // Closing the stream makes the controller discard this unusable session before retry.
                    var message = owner.Failure is { } failure
                        ? $"Copilot failed. {failure} Native idle was not confirmed. Reopen the session."
                        : "Copilot did not become idle. Reopen the session.";
                    _events.Writer.TryComplete(new IOException(message, ex));
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
                _active?.Cancellation.Dispose();
                _active = null;
            }
            _lifetime.Cancel(); _subscription?.Dispose();
            Task<TerminalExecutionResult>[] executions;
            lock (_executions) executions = _executions.ToArray();
            try { await Task.WhenAll(executions); } catch (OperationCanceledException) { }
            if (_native is not null) await _native.DisposeAsync();
            if (client is not null) await client.DisposeAsync();
            _events.Writer.TryComplete(); _lifetime.Dispose();
        }
    }
}
