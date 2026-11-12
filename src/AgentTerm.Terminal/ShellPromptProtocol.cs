using System.Text;
namespace AgentTerm.Terminal;
public enum ShellPromptState { Unknown, Raw, Ready, Executing, Exited }
/// <summary>Readiness comes only from authenticated shell integration markers, never screen contents.</summary>
public sealed class ShellPromptProtocol(string token)
{
    public ShellPromptState State { get; internal set; }
    public string? ReadyCommandId { get; private set; }
    public bool? LastCommandSucceeded { get; private set; }
    public int? LastExitCode { get; private set; }
    public IReadOnlySet<string> Commands { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public event Action<string>? Marker;
    public event Action? PromptStarted;
    public void Accept(string sequence, int byteLength)
    {
        if (byteLength > 1_000_000) return;
        var prefix = "777;" + token + ";";
        if (!sequence.StartsWith(prefix, StringComparison.Ordinal)) return;
        var message = sequence[prefix.Length..];
        Marker?.Invoke(message);
        if (message == "raw") { State = ShellPromptState.Raw; PromptStarted?.Invoke(); }
        else if (message == "ready" || message.StartsWith("ready;", StringComparison.Ordinal)) { State = ShellPromptState.Ready; ReadyCommandId = message.Length > 6 ? message[6..] : null; PromptStarted?.Invoke(); }
        else if (message == "start" || message.StartsWith("start;", StringComparison.Ordinal)) State = ShellPromptState.Executing;
        else if (message.StartsWith("end;"))
        {
            var parts = message.Split(';');
            LastCommandSucceeded = parts.ElementAtOrDefault(1) == "1";
            LastExitCode = int.TryParse(parts.ElementAtOrDefault(2), out var code) ? code : null;
        }
        else if (message.StartsWith("commands;"))
        {
            try { Commands = new HashSet<string>(Encoding.UTF8.GetString(Convert.FromBase64String(message[9..])).Split('\n', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase); }
            catch (FormatException) { Commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        }
    }
}
