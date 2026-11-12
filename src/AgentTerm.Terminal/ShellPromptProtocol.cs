using System.Text;
namespace AgentTerm.Terminal;
public enum ShellPromptState { Unknown, Initializing, Ready, Executing, Exited }
/// <summary>Readiness comes only from authenticated shell integration markers, never screen contents.</summary>
public sealed class ShellPromptProtocol(string token)
{
    public ShellPromptState State { get; internal set; }
    public bool AgentEntryAvailable { get; private set; }
    public string? ReadyCommandId { get; private set; }
    public bool? LastCommandSucceeded { get; private set; }
    public int? LastExitCode { get; private set; }
    private readonly Queue<string> _agentRequests = new();
    public string? TakeAgentRequest() => _agentRequests.TryDequeue(out var request) ? request : null;
    public event Action<string>? Marker;
    public event Action? PromptStarted;
    public void Accept(string sequence, int byteLength)
    {
        if (byteLength > 1_000_000) return;
        var prefix = "777;" + token + ";";
        if (!sequence.StartsWith(prefix, StringComparison.Ordinal)) return;
        var message = sequence[prefix.Length..];
        Marker?.Invoke(message);
        if (message.StartsWith("agent;", StringComparison.Ordinal))
        {
            try { _agentRequests.Enqueue(Encoding.UTF8.GetString(Convert.FromBase64String(message[6..]))); }
            catch (FormatException) { }
        }
        else if (message == "entry;1") AgentEntryAvailable = true;
        else if (message == "entry;0") AgentEntryAvailable = false;
        else if (message == "unavailable") { State = ShellPromptState.Unknown; AgentEntryAvailable = false; }
        else if (message == "ready" || message.StartsWith("ready;", StringComparison.Ordinal)) { State = ShellPromptState.Ready; ReadyCommandId = message.Length > 6 ? message[6..] : null; PromptStarted?.Invoke(); }
        else if (message == "start" || message.StartsWith("start;", StringComparison.Ordinal)) State = ShellPromptState.Executing;
        else if (message.StartsWith("end;") && State == ShellPromptState.Executing)
        {
            var parts = message.Split(';');
            LastCommandSucceeded = parts.ElementAtOrDefault(1) == "1";
            LastExitCode = int.TryParse(parts.ElementAtOrDefault(2), out var code) ? code : null;
        }
    }
}
