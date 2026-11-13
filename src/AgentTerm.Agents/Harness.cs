namespace AgentTerm.Agents;

public enum AgentState { Starting, Idle, Running, WaitingForApproval, WaitingForInput, Interrupting, Failed, Disposed }
public sealed record AgentLocation(string Path, string? WslDistribution = null);
public sealed record HarnessDescription(bool Available, string Message, IReadOnlyList<string> Models, bool Approvals = true, bool Questions = true);
public sealed record AgentOptions(AgentLocation Location, string? Model = null, IHostTerminal? Terminal = null);
public sealed record AgentPrompt(Guid Turn, string Text);
public sealed record AgentResponse(Guid Request, bool Approved = false, string? Answer = null);
public abstract record AgentEvent(Guid Turn);
public sealed record TextDelta(Guid Turn, string Text) : AgentEvent(Turn);
public sealed record ToolStarted(Guid Turn, string CallId, string Name, ToolCategory Category, string Action, string Detail) : AgentEvent(Turn);
public sealed record ToolOutput(Guid Turn, string CallId, string Text) : AgentEvent(Turn);
/// <summary>The host began executing an approved call. Until then a started call is only requested or in flight.</summary>
public sealed record ToolExecuting(Guid Turn, string CallId) : AgentEvent(Turn);
public sealed record ToolCompleted(Guid Turn, string CallId, WorkStatus Status) : AgentEvent(Turn);
public sealed record ReasoningSummary(Guid Turn, string Text) : AgentEvent(Turn);
/// <param name="CallId">The tool call this approval gates, when the harness can attribute it.</param>
public sealed record ApprovalRequest(Guid Turn, Guid Request, string Description, string? CallId = null) : AgentEvent(Turn);
public sealed record QuestionRequest(Guid Turn, Guid Request, string Question, IReadOnlyList<string> Choices, bool AllowFreeform) : AgentEvent(Turn);
public sealed record TurnCompleted(Guid Turn) : AgentEvent(Turn);
public sealed record TurnFailed(Guid Turn, string Message) : AgentEvent(Turn);
public sealed record TurnInterrupted(Guid Turn) : AgentEvent(Turn);
public interface IAgentHarness
{
    /// <summary>Explicit capability/model discovery. Submission opens directly without this preflight.</summary>
    Task<HarnessDescription> DescribeAsync(AgentLocation location, CancellationToken cancellationToken = default);
    /// <summary>Opens an authenticated session, or throws an actionable availability error. Owns startup cleanup.</summary>
    Task<IAgentSession> OpenAsync(AgentOptions options, CancellationToken cancellationToken = default);
}
public interface IAgentSession : IAsyncDisposable
{
    IAsyncEnumerable<AgentEvent> Events(CancellationToken cancellationToken = default);
    Task SubmitAsync(AgentPrompt prompt, CancellationToken cancellationToken = default);
    Task RespondAsync(AgentResponse response, CancellationToken cancellationToken = default);
    Task InterruptAsync(Guid turn, CancellationToken cancellationToken = default);
}
