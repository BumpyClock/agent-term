using System.Globalization;
using System.Text;

namespace AgentTerm.Terminal;

public enum ProgramState { Idle, Working, Done, Blocked, Error }

public sealed record ProgramStatus(string Id, ProgramState State, string? Kind, int? Progress,
    string? App, string? EffectiveApp, string? Title, string? Message, DateTimeOffset UpdatedAt);

/// <summary>Observes OSC 7501 without modifying terminal output. Access is serialized by the session.</summary>
public sealed class ProgramStatusProtocol
{
    private readonly Dictionary<string, ProgramStatus> _records = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recency = new();
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public event Action? SupportRequested;
    public static ReadOnlySpan<byte> SupportReply => "\u001b]7501;?:states=idle,working,done,blocked,error:kinds=permission,question,auth\u001b\\"u8;

    public IReadOnlyList<ProgramStatus> ReadStatuses() => _records.Values
        .Select(record => record with { EffectiveApp = EffectiveApp(record.Id) })
        .ToArray();

    private string? EffectiveApp(string id)
    {
        while (true)
        {
            if (_records.TryGetValue(id, out var record) && record.App is not null) return record.App;
            if (id.Length == 0) return null;
            var slash = id.LastIndexOf('/');
            id = slash < 0 ? "" : id[..slash];
        }
    }

    public void EndCommand()
    {
        foreach (var record in _records.Values.Where(r => r.State is ProgramState.Working or ProgramState.Blocked).ToArray())
            Remove(record.Id);
    }

    public void Reset() { _records.Clear(); _recency.Clear(); }

    public void Accept(string sequence, int byteLength)
    {
        if (sequence == "133;A" || sequence.StartsWith("133;A;", StringComparison.Ordinal)) { EndCommand(); return; }
        if (byteLength > 4096) return;
        if (!sequence.StartsWith("7501;", StringComparison.Ordinal)) return;
        var body = sequence[5..];
        if (body == "?") { SupportRequested?.Invoke(); return; }
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in body.Split(':'))
        {
            var equals = pair.IndexOf('=');
            if (equals < 0) continue;
            var key = pair[..equals].Trim();
            var value = pair[(equals + 1)..].Trim();
            if (key.Length > 16) return;
            if ((key == "title" && value.Length > 256) || (key == "msg" && value.Length > 2732)) return;
            if (key.Length == 0 || key.Any(c => c is < 'a' or > 'z')) continue;
            // Validate every occurrence before applying anything, including overwritten duplicate fields.
            if ((key == "app" && value.Length > 32) || (key == "id" && !ValidId(value))) return;
            if (value.Any(c => !IsValueCharacter(c))) continue;
            if (key is "title" or "msg")
            {
                if (!TryText(value, key == "title" ? 256 : 2732, key == "title" ? 192 : 2048, out var text)) return;
                value = text;
            }
            pairs[key] = value;
        }
        var id = pairs.GetValueOrDefault("id") ?? "";
        var stateText = pairs.GetValueOrDefault("state");
        if (stateText == "clear")
        {
            foreach (var key in _records.Keys.Where(key => id.Length == 0 || key == id || key.StartsWith(id + "/", StringComparison.Ordinal)).ToArray()) Remove(key);
            return;
        }
        ProgramState? state = stateText switch
        {
            "idle" => ProgramState.Idle, "working" => ProgramState.Working, "done" => ProgramState.Done,
            "blocked" => ProgramState.Blocked, "error" => ProgramState.Error, _ => null
        };
        if (state is null) return;
        var app = pairs.GetValueOrDefault("app");
        if (app is not null && !ValidSegment(app)) app = null;
        var kind = pairs.GetValueOrDefault("kind");
        if (state != ProgramState.Blocked || kind is not ("permission" or "question" or "auth")) kind = null;
        int? progress = null;
        if (state is ProgramState.Working or ProgramState.Blocked &&
            int.TryParse(pairs.GetValueOrDefault("progress"), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number <= 100)
            progress = number;
        if (!_records.ContainsKey(id) && _records.Count == 256) Remove(_recency.First!.Value);
        _recency.Remove(id);
        _recency.AddLast(id);
        _records[id] = new(id, state.Value, kind, progress, app, app,
            pairs.GetValueOrDefault("title"), pairs.GetValueOrDefault("msg"), DateTimeOffset.UtcNow);
    }

    private void Remove(string id) { _records.Remove(id); _recency.Remove(id); }
    private static bool IsValueCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ',' or '+' or '/' or '=' or '-';
    private static bool ValidSegment(string value) => value.Length is >= 1 and <= 32 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '+' or '-');
    private static bool ValidId(string value) => value.Length <= 128 && value.Split('/') is var segments && segments.Length <= 8 && segments.All(ValidSegment);

    private static bool TryText(string value, int encodedLimit, int decodedLimit, out string text)
    {
        text = "";
        if (value.Length > encodedLimit) return false;
        try
        {
            var bytes = Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '='));
            if (bytes.Length > decodedLimit) return false;
            text = Utf8.GetString(bytes);
            return !text.Any(char.IsControl);
        }
        catch (Exception error) when (error is FormatException or DecoderFallbackException) { return false; }
    }
}
