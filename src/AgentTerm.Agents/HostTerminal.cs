namespace AgentTerm.Agents;

public enum TerminalExecutionStatus { Completed, Busy, Unsupported, Exited, Interrupted, InterruptionFailed, Denied }
public sealed record TerminalExecutionRequest(string Command);
public sealed record TerminalExecutionResult(TerminalExecutionStatus Status, string Output = "", bool? Succeeded = null,
    int? ExitCode = null, string? CurrentDirectory = null, bool Truncated = false,
    string OutputFormat = "ConPTY terminal text; ANSI/OSC stripped; cursor redraws not reconstructed", string? Message = null);

/// <summary>Pane-local exclusive execution. Cancellation interrupts the command, not the shell.</summary>
public interface IHostTerminal
{
    Task<TerminalExecutionResult> ExecuteAsync(TerminalExecutionRequest request, CancellationToken cancellationToken = default);
}
