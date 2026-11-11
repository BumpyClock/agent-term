using System.Text;
namespace AgentTerm.Terminal;
public enum ShellPromptState { Unknown, Raw, Ready, Executing, Exited }
/// <summary>Incremental OSC parser. Readiness comes only from the shell integration, never screen contents.</summary>
public sealed class ShellPromptProtocol(string token)
{
    private readonly StringBuilder _sequence = new();
    private bool _escape;
    private bool _osc;
    public ShellPromptState State { get; internal set; }
    public string? ReadyCommandId { get; private set; }
    public bool? LastCommandSucceeded { get; private set; }
    public int? LastExitCode { get; private set; }
    public IReadOnlySet<string> Commands { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public event Action<byte>? OutputByte;
    public event Action<string>? Marker;
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (_osc)
            {
                if (_escape && b == '\\') { Accept(_sequence.ToString()); _sequence.Clear(); _osc = false; _escape = false; continue; }
                _escape = b == 27;
                if (_escape) continue;
                if (b == 7) { Accept(_sequence.ToString()); _sequence.Clear(); _osc = false; }
                else if (_sequence.Length < 1000000) _sequence.Append((char)b);
                else { _sequence.Clear(); _osc = false; }
            }
            else if (_escape) { _escape = false; if (b == ']') { _sequence.Clear(); _osc = true; } else { OutputByte?.Invoke(27); OutputByte?.Invoke(b); } }
            else if (b == 27) _escape = true;
            else OutputByte?.Invoke(b);
        }
    }
    private void Accept(string sequence)
    {
        var prefix = "777;" + token + ";";
        if (!sequence.StartsWith(prefix, StringComparison.Ordinal)) return;
        var message = sequence[prefix.Length..];
        Marker?.Invoke(message);
        if (message == "raw") State = ShellPromptState.Raw;
        else if (message == "ready" || message.StartsWith("ready;", StringComparison.Ordinal)) { State = ShellPromptState.Ready; ReadyCommandId = message.Length > 6 ? message[6..] : null; }
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
