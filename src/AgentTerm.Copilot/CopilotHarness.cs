using System.Threading.Channels;
using Microsoft.Extensions.AI;
using System.ComponentModel;
using AgentTerm.Agents;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
#pragma warning disable GHCP001 // Published SDK permission decisions and managed policy are experimental.
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
    private sealed class Session(CopilotClient client) : IAgentSession
    {
        private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();
        private readonly Dictionary<Guid, TaskCompletionSource<AgentResponse>> _requests = [];
        private readonly CancellationTokenSource _lifetime = new();
        private CopilotSession? _native;
        private Guid? _turn;
        private IHostTerminal? _terminal;
        private readonly HashSet<Task<TerminalExecutionResult>> _executions = [];
        private CancellationTokenSource? _turnCancellation;
        private bool _interrupting;
        private TaskCompletionSource? _interrupted;
        private IDisposable? _subscription;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _toolNames = new();
        public async Task OpenAsync(AgentOptions options, CancellationToken cancellationToken)
        {
            _terminal = options.Terminal;
            _native = await client.CreateSessionAsync(new SessionConfig
            {
                WorkingDirectory = options.Location.Path, Model = options.Model, Streaming = true,
                EnableManagedSettings = true,
                AvailableTools = ["run_in_terminal", "ask_user"],
                Tools = [CopilotTool.DefineTool(
                    async ([Description("Exact PowerShell command to execute in the invoking pane, preserving shell state.")] string command) =>
                    {
                        if (_terminal is null) return new TerminalExecutionResult(TerminalExecutionStatus.Unsupported);
                        var id = Guid.NewGuid();
                        var reply = await RequestAsync(id, new ApprovalRequest(_turn ?? Guid.Empty, id, command));
                        if (!reply.Approved) return new TerminalExecutionResult(TerminalExecutionStatus.Denied);
                        Task<TerminalExecutionResult> execution;
                        lock (_executions)
                        {
                            execution = _terminal.ExecuteAsync(new(command), _turnCancellation?.Token ?? _lifetime.Token);
                            _executions.Add(execution);
                        }
                        try { return await execution; }
                        finally { lock (_executions) _executions.Remove(execution); }
                    },
                    toolOptions: new CopilotToolOptions { SkipPermission = true, Defer = CopilotToolDefer.Never },
                    factoryOptions: new AIFunctionFactoryOptions { Name = "run_in_terminal", Description = "Execute in the user's EXISTING pane PowerShell. Cwd returned by this tool is authoritative. Output is bounded ConPTY terminal text with ANSI stripped; cursor redraws are not reconstructed. Use this tool for all shell and file operations. No independent shell or native file tools are available. Interactive programs require deliberate user input handoff." })],
                OnPermissionRequest = async (request, invocation) =>
                {
                    var id = Guid.NewGuid();
                    var detail = request switch
                    {
                        PermissionRequestShell shell => shell.FullCommandText,
                        PermissionRequestWrite write => $"Write {write.FileName}\n{write.Diff}",
                        PermissionRequestRead read => $"Read {read.Path}",
                        PermissionRequestUrl url => $"Access URL {url.Url}",
                        PermissionRequestMcp mcp => $"{mcp.ServerDisplayName ?? mcp.ServerName} / {mcp.ToolName}\n{mcp.Args}",
                        PermissionRequestCustomTool tool => $"{tool.ToolName}\n{tool.ToolDescription}\n{tool.Args}",
                        _ => $"{request.Kind} permission. Review this tool activity before approving.",
                    };
                    var reply = await RequestAsync(id, new ApprovalRequest(_turn ?? Guid.Empty, id, detail));
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
        private void OnEvent(SessionEvent item)
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
            AgentEvent? normalized = item switch
            {
                AssistantMessageDeltaEvent text => new TextDelta(turn, text.Data.DeltaContent),
                SessionErrorEvent error => new TurnFailed(turn, error.Data.Message),
                SessionIdleEvent => _interrupting ? new TurnInterrupted(turn) : new TurnCompleted(turn),
                _ => null,
            };
            if (normalized is TurnCompleted or TurnInterrupted or TurnFailed)
            {
                _turn = null;
                _interrupted?.TrySetResult();
            }
            if (normalized is not null) _events.Writer.TryWrite(normalized);
        }
        public IAsyncEnumerable<AgentEvent> Events(CancellationToken cancellationToken = default) => _events.Reader.ReadAllAsync(cancellationToken);
        public async Task SubmitAsync(AgentPrompt prompt, CancellationToken cancellationToken = default)
        {
            _turnCancellation?.Dispose();
            _turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _toolNames.Clear();
            _turn = prompt.Turn; _interrupting = false;
            await _native!.SendAsync(new MessageOptions { Prompt = prompt.Text }, cancellationToken);
        }
        public Task RespondAsync(AgentResponse response, CancellationToken cancellationToken = default)
        {
            lock (_requests) if (_requests.TryGetValue(response.Request, out var request)) request.TrySetResult(response);
            return Task.CompletedTask;
        }
        public async Task InterruptAsync(Guid turn, CancellationToken cancellationToken = default)
        {
            if (_turn != turn) return;
            _interrupting = true;
            _interrupted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_requests) foreach (var request in _requests.Values) request.TrySetResult(new(Guid.Empty));
            _turnCancellation?.Cancel();
            Task<TerminalExecutionResult>[] executions;
            lock (_executions) executions = _executions.ToArray();
            if ((await Task.WhenAll(executions)).Any(result => result.Status == TerminalExecutionStatus.InterruptionFailed))
                throw new InvalidOperationException("Terminal interruption failed. Ownership retained until shell readiness.");
            await _native!.AbortAsync(cancellationToken);
            await _interrupted.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        public async ValueTask DisposeAsync()
        {
            _turn = null; _turnCancellation?.Cancel(); _lifetime.Cancel(); _subscription?.Dispose();
            Task<TerminalExecutionResult>[] executions;
            lock (_executions) executions = _executions.ToArray();
            try { await Task.WhenAll(executions); } catch (OperationCanceledException) { }
            if (_native is not null) await _native.DisposeAsync();
            await client.DisposeAsync(); _events.Writer.TryComplete(); _turnCancellation?.Dispose(); _lifetime.Dispose();
        }
    }
}
