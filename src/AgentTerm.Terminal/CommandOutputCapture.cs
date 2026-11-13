using System.Text;
namespace AgentTerm.Terminal;

/// <summary>
/// One owned command's output. The streamed transcript is lossless up to <see cref="Limit"/> characters: decoded text in
/// stream order, CSI and control strings removed, CR and other C0 controls kept. <see cref="Resolve"/> may replace it
/// with the final screen result, which applies CR rewrites and cursor movement as the terminal displayed them.
/// </summary>
public sealed class CommandOutputCapture(string? commandId = null)
{
    public const int Limit = 64000;
    private readonly string _start = "Command started [" + commandId + "]";
    private readonly string _end = "Command ended [" + commandId + "]";
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _fence = new();
    private const int ChunkBytes = 512;
    private bool _capturing;
    private int _matched;
    private int _replayed;
    private int _ansi;
    private string? _screen;
    private bool _screenTruncated;
    internal string StartFence => _start;
    internal string EndFence => _end;
    public bool Ended { get; private set; }
    /// <summary>True once <see cref="Resolve"/> ran; later output can no longer change the result.</summary>
    public bool Resolved { get; private set; }
    /// <summary>True when <see cref="Text"/> came from the screen rather than the streamed transcript.</summary>
    public bool FromScreen => _screen is not null;
    public bool Truncated => _screen is not null ? _screenTruncated : TranscriptTruncated;
    public bool TranscriptTruncated { get; private set; }
    /// <summary>The result: the screen text when resolved from the screen, otherwise the transcript.</summary>
    public string Text => _screen ?? Transcript;
    public string Transcript => (_text.Length > 0 && char.IsHighSurrogate(_text[^1]) ? _text.ToString(0, _text.Length - 1) : _text.ToString()).TrimStart('\r', '\n');

    /// <summary>
    /// Settles the result once the command ended. A screen result replaces the transcript only when the terminal could
    /// prove the region; null keeps the transcript. The screen result shares the transcript's limit and leading-newline rule.
    /// </summary>
    public void Resolve(string? screen)
    {
        if (Resolved) return;
        Resolved = true;
        if (screen is null) return;
        screen = screen.TrimStart('\r', '\n');
        _screenTruncated = screen.Length > Limit;
        if (_screenTruncated) screen = screen[..(char.IsHighSurrogate(screen[Limit - 1]) ? Limit - 1 : Limit)];
        _screen = screen;
    }
    public void Marker(string message)
    {
        if (commandId is not null) return;
        if (message == "start") _capturing = true;
        if (message.StartsWith("end;", StringComparison.Ordinal)) { _capturing = false; Ended = true; }
    }
    public void Feed(byte value) => Feed(new ReadOnlySpan<byte>(in value));
    /// <summary>A control string cut short any escape sequence still open in the output stream.</summary>
    public void EndEscape() => _ansi = 0;
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        Span<char> chars = stackalloc char[ChunkBytes + 1];
        var index = 0;
        while (index < bytes.Length)
        {
            if (_ansi == 1) { _ansi = bytes[index++] switch { (byte)'[' => 2, 27 => 1, _ => 0 }; continue; }
            if (_ansi == 2) { if (bytes[index++] is >= 0x40 and <= 0x7e) _ansi = 0; continue; }
            var run = bytes[index..];
            var escape = run.IndexOf((byte)27);
            if (escape >= 0) run = run[..escape];
            index += run.Length;
            if (escape >= 0) { index++; _ansi = 1; }
            while (run.Length > 0)
            {
                var take = Math.Min(run.Length, ChunkBytes);
                var count = _decoder.GetChars(run[..take], chars, false);
                run = run[take..];
                for (var i = 0; i < count; i++)
                {
                    if (Ended) return;
                    Accept(chars[i]);
                }
            }
        }
    }
    private void Accept(char ch)
    {
        if (commandId is null) { if (_capturing) Append(ch); return; }
        var expected = _capturing ? _end : _start;
        if (_matched > 0 && _fence.Length < expected.Length * 3 && ch is '\r' or '\n') { _fence.Append(ch); return; }
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
    private void Append(char ch)
    {
        if (_text.Length < Limit) _text.Append(ch);
        else TranscriptTruncated = true;
        if (commandId is null || (_replayed > 0 && ch < (char)32)) return;
        _replayed = ch == _start[_replayed] ? _replayed + 1 : ch == _start[0] ? 1 : 0;
        if (_replayed < _start.Length) return;
        _replayed = 0; _text.Clear(); TranscriptTruncated = false;
    }
}
