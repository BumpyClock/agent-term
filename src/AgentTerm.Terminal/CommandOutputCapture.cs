using System.Text;
namespace AgentTerm.Terminal;

/// <summary>Ordered text fences are required because inbox ConPTY can reorder OSC relative to rendered text.</summary>
public sealed class CommandOutputCapture(string? commandId = null)
{
    private readonly string _start = "Command started [" + commandId + "]";
    private readonly string _end = "Command ended [" + commandId + "]";
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _fence = new();
    private bool _capturing;
    private int _matched;
    private int _replayed;
    private int _ansi;
    public bool Ended { get; private set; }
    public bool Truncated { get; private set; }
    public string Text => (_text.Length > 0 && char.IsHighSurrogate(_text[^1]) ? _text.ToString(0, _text.Length - 1) : _text.ToString()).TrimStart('\r', '\n');
    public void Marker(string message)
    {
        if (commandId is not null) return;
        if (message == "start") _capturing = true;
        if (message.StartsWith("end;", StringComparison.Ordinal)) { _capturing = false; Ended = true; }
    }
    public void Feed(byte value)
    {
        // Strip CSI, not screen contents. Cursor redraws are not reconstructed.
        if (_ansi == 1) { _ansi = value == '[' ? 2 : 0; return; }
        if (_ansi == 2) { if (value is >= 0x40 and <= 0x7e) _ansi = 0; return; }
        if (value == 27) { _ansi = 1; return; }
        Span<char> chars = stackalloc char[2];
        var count = _decoder.GetChars([value], chars, false);
        foreach (var ch in chars[..count])
        {
            if (Ended) return;
            if (commandId is null) { if (_capturing) Append(ch); continue; }
            var expected = _capturing ? _end : _start;
            if (_matched > 0 && _fence.Length < expected.Length * 3 && ch is '\r' or '\n') { _fence.Append(ch); continue; } // Preserve wrapped text unless the whole fence matches.
            if (ch == expected[_matched])
            {
                _fence.Append(ch);
                _matched++;
                if (_matched == expected.Length)
                {
                    _fence.Clear(); _matched = 0;
                    if (_capturing) { Ended = true; _capturing = false; }
                    else _capturing = true;
                }
            }
            else
            {
                if (_capturing) foreach (var pending in _fence.ToString()) Append(pending);
                _fence.Clear(); _matched = 0;
                if (ch == expected[0]) { _fence.Append(ch); _matched = 1; }
                else if (_capturing) Append(ch);
            }
        }
    }
    private void Append(char ch)
    {
        if (_text.Length < 64000) _text.Append(ch);
        else Truncated = true;
        // A resize repaints the screen, including this command's start fence. Text after the latest fence is current.
        if (commandId is null || (_replayed > 0 && ch < (char)32)) return;
        _replayed = ch == _start[_replayed] ? _replayed + 1 : ch == _start[0] ? 1 : 0;
        if (_replayed < _start.Length) return;
        _replayed = 0; _text.Clear(); Truncated = false;
    }
}
