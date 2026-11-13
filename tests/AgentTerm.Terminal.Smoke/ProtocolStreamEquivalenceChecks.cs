using System.Text;
using AgentTerm.Terminal;

/// <summary>
/// Compares the native-boundary protocol stream against a frozen copy of the per-byte managed scanner it replaced.
/// The 7-bit corpus covers cases where both parsers agree. Random noise is checked for chunking invariance and for
/// terminal state identical to a plain write, because the native parser is authoritative where they differ.
/// </summary>
internal static class ProtocolStreamEquivalenceChecks
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class LegacyScanner
    {
        private enum State { Ground, Escape, Osc, OscEscape, String }
        private State _state;
        private readonly StringBuilder _sequence = new();
        private int _length;
        public event Action<byte>? OutputByte;
        public event Action<string, int>? Sequence;
        public event Action? Reset;
        public event Action? StringStarted;

        public void Feed(ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes)
            {
                if (value == 27)
                {
                    _state = _state == State.Osc ? State.OscEscape : State.Escape;
                    continue;
                }
                if (_state == State.OscEscape && value == '\\')
                {
                    Finish(2);
                    continue;
                }
                if (_state is State.Escape or State.OscEscape)
                {
                    _sequence.Clear();
                    _length = 0;
                    _state = value switch
                    {
                        (byte)']' => State.Osc,
                        (byte)'P' or (byte)'X' or (byte)'^' or (byte)'_' => State.String,
                        _ => State.Ground
                    };
                    if (value == 'c') Reset?.Invoke();
                    if (_state is State.Osc or State.String) StringStarted?.Invoke();
                    if (_state == State.Ground) { OutputByte?.Invoke(27); OutputByte?.Invoke(value); }
                    continue;
                }
                if (_state is State.Osc or State.String)
                {
                    if (value is 24 or 26) { _state = State.Ground; _sequence.Clear(); }
                    else if (_state == State.Osc)
                    {
                        if (value == 7) Finish(1);
                        else
                        {
                            _length = Math.Min(1_000_001, _length + 1);
                            if (_length <= 1_000_000) _sequence.Append((char)value);
                        }
                    }
                    continue;
                }
                OutputByte?.Invoke(value);
            }
        }

        private void Finish(int terminatorLength)
        {
            if (_length <= 1_000_000) Sequence?.Invoke(_sequence.ToString(), _length + 2 + terminatorLength);
            _sequence.Clear();
            _state = State.Ground;
        }
    }

    private sealed class LegacyCapture(string? commandId = null)
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
        public void EndEscape() => _ansi = 0;
        public void Marker(string message)
        {
            if (commandId is not null) return;
            if (message == "start") _capturing = true;
            if (message.StartsWith("end;", StringComparison.Ordinal)) { _capturing = false; Ended = true; }
        }
        public void Feed(byte value)
        {
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
                if (_matched > 0 && _fence.Length < expected.Length * 3 && ch is '\r' or '\n') { _fence.Append(ch); continue; }
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
            if (commandId is null || (_replayed > 0 && ch < (char)32)) return;
            _replayed = ch == _start[_replayed] ? _replayed + 1 : ch == _start[0] ? 1 : 0;
            if (_replayed < _start.Length) return;
            _replayed = 0; _text.Clear(); Truncated = false;
        }
    }

    private sealed record Result(List<string> Events, string Plain, string Fenced, bool PlainEnded, bool FencedEnded, bool PlainTruncated, bool FencedTruncated)
    {
        public string Text = "";
    }

    private static Result RunLegacy(byte[] data, int[] cuts)
    {
        var scanner = new LegacyScanner();
        var plain = new LegacyCapture();
        var fenced = new LegacyCapture("x");
        var events = new List<string>();
        scanner.OutputByte += b => { plain.Feed(b); fenced.Feed(b); };
        scanner.Sequence += (text, length) => { events.Add("s" + text + ":" + length); if (text == "start" || text.StartsWith("end;", StringComparison.Ordinal)) plain.Marker(text); };
        scanner.Reset += () => events.Add("r");
        // The one intentional fix: a control string ends a CSI left open in output, so the next text is not eaten.
        scanner.StringStarted += () => { plain.EndEscape(); fenced.EndEscape(); };
        Slice(data, cuts, chunk => scanner.Feed(chunk));
        return new(events, plain.Text, fenced.Text, plain.Ended, fenced.Ended, plain.Truncated, fenced.Truncated);
    }

    private static Result RunCurrent(byte[] data, int[] cuts)
    {
        using var terminal = new GhosttyTerminal(80, 8);
        var stream = new TerminalProtocolStream(terminal);
        var plain = new CommandOutputCapture();
        var fenced = new CommandOutputCapture("x");
        var events = new List<string>();
        stream.Output += bytes => { plain.Feed(bytes); fenced.Feed(bytes); };
        stream.ControlStringStarted += () => { plain.EndEscape(); fenced.EndEscape(); };
        stream.Sequence += (text, length) => { events.Add("s" + text + ":" + length); if (text == "start" || text.StartsWith("end;", StringComparison.Ordinal)) plain.Marker(text); };
        stream.ProtocolEvent += item => { if (item is TerminalReset) events.Add("r"); };
        Slice(data, cuts, chunk => stream.Write(chunk));
        return new(events, plain.Text, fenced.Text, plain.Ended, fenced.Ended, plain.Truncated, fenced.Truncated) { Text = terminal.ReadText() };
    }

    private static Result RunNative(byte[] data, int[] cuts)
    {
        using var terminal = new GhosttyTerminal(80, 8);
        var stream = new TerminalProtocolStream(terminal);
        var plain = new CommandOutputCapture();
        var output = new List<string>();
        var events = new List<string>();
        stream.Output += bytes => { plain.Feed(bytes); output.Add(Convert.ToHexString(bytes)); };
        stream.ControlStringStarted += () => { plain.EndEscape(); events.Add("c"); };
        stream.Sequence += (text, length) => { events.Add("s" + text + ":" + length); if (text == "start" || text.StartsWith("end;", StringComparison.Ordinal)) plain.Marker(text); };
        stream.ProtocolEvent += item => events.Add(item.ToString());
        Slice(data, cuts, chunk => stream.Write(chunk));
        return new(events, plain.Text, string.Concat(output), plain.Ended, false, plain.Truncated, false) { Text = terminal.ReadText() + "|" + terminal.ReadTitle() };
    }

    private static string PlainWrite(byte[] data)
    {
        using var terminal = new GhosttyTerminal(80, 8);
        terminal.Write(data);
        return terminal.ReadText() + "|" + terminal.ReadTitle();
    }

    private static void CompareNoise(byte[] data, int[] cuts, string label)
    {
        var whole = RunNative(data, []);
        var split = RunNative(data, cuts);
        var where = label + " cuts=" + string.Join(",", cuts.Take(8));
        var first = Enumerable.Range(0, Math.Min(whole.Events.Count, split.Events.Count)).FirstOrDefault(i => whole.Events[i] != split.Events[i], -1);
        Check(whole.Events.SequenceEqual(split.Events), "Native events and sequences must not depend on chunking: " + where + $" first difference {first}: whole <" +
            Visible(string.Join(" | ", whole.Events.Skip(Math.Max(first, 0)).Take(3))) + "> split <" + Visible(string.Join(" | ", split.Events.Skip(Math.Max(first, 0)).Take(3))) + ">");
        Check(whole.Fenced == split.Fenced && whole.Plain == split.Plain && whole.PlainEnded == split.PlainEnded, "Output must not depend on chunking: " + where);
        Check(whole.Text == split.Text && whole.Text == PlainWrite(data), "Boundary writes must leave the same terminal state as one plain write: " + where);
    }

    private static void Slice(byte[] data, int[] cuts, Action<byte[]> feed)
    {
        var start = 0;
        foreach (var cut in cuts.Append(data.Length))
        {
            var end = Math.Clamp(cut, start, data.Length);
            feed(data[start..end]);
            start = end;
        }
    }

    private static string Visible(string text) => string.Concat(text.Select(c => c < ' ' || c is > '~' and < ' ' ? $"\\x{(int)c:x2}" : c.ToString()));

    private static void Compare(byte[] data, int[] cuts, string label)
    {
        var a = RunLegacy(data, cuts);
        var b = RunCurrent(data, cuts);
        var where = label + " cuts=" + string.Join(",", cuts.Take(8));
        Check(a.Events.SequenceEqual(b.Events), "Sequences and resets must match the per-byte scanner: " + where + " legacy [" + string.Join(" | ", a.Events) + "] native [" + string.Join(" | ", b.Events) + "]");
        Check(a.Plain == b.Plain && a.Fenced == b.Fenced, "Captured text must match the per-byte capture: " + where +
            " data <" + Visible(Encoding.Latin1.GetString(data)) + "> legacy <" + Visible(a.Plain) + "|" + Visible(a.Fenced) + "> native <" + Visible(b.Plain) + "|" + Visible(b.Fenced) + ">");
        Check(a.PlainEnded == b.PlainEnded && a.FencedEnded == b.FencedEnded && a.PlainTruncated == b.PlainTruncated && a.FencedTruncated == b.FencedTruncated, "Capture flags must match: " + where);
    }

    public static void Run()
    {
        var pieces = new[]
        {
            "plain text line\r\n", "\u001b]0;title\a", "\u001b]777;x;y\u001b\\", "\u001b]7501;state=working\u001b\\", "\u001b]start\a", "\u001b]end;0\a",
            "\u001b[31mred\u001b[0m", "\u001b[", "\u001bc", "\u001b7", "\u001b\\", "\u001bP1;2|data\u001b\\", "\u001b_apc\a\u001b\\", "\u001bXsos\u001b\\", "\u001b^pm\u001b\\",
            "\u001b]123\u0018after", "\u001bPdcs\u001aafter", "\u001b]5\u001b]6\a", "\u001b\u001b]7\a", "\u001b]8;;http://x\u001b",
            "Command started [x]\r\n", "Command ended [x]\r\n", "Command ended [x", "café 終 \U0001f989 end\r\n", "\u0018", "\u001a", "\a", "\\"
        };
        var random = new Random(7);
        for (var round = 0; round < 300; round++)
        {
            var text = new StringBuilder();
            for (var i = random.Next(1, 12); i > 0; i--) text.Append(pieces[random.Next(pieces.Length)]);
            var data = Encoding.UTF8.GetBytes(text.ToString());
            Compare(data, [], "whole " + round);
            for (var cut = 0; cut <= data.Length; cut++) Compare(data, [cut], "split " + round + "@" + cut);
            Compare(data, Enumerable.Range(1, data.Length).ToArray(), "bytewise " + round);
            Compare(data, Enumerable.Range(0, 6).Select(_ => random.Next(data.Length + 1)).Order().ToArray(), "random " + round);
        }
        var noise = new byte[4096];
        var specials = new byte[] { 7, 24, 26, 93, 92, 99, 80, 91 };
        for (var round = 0; round < 200; round++)
        {
            random.NextBytes(noise);
            for (var i = 0; i < noise.Length; i += 7) noise[i] = i % 3 == 0 ? (byte)27 : specials[random.Next(specials.Length)];
            CompareNoise(noise, Enumerable.Range(0, 5).Select(_ => random.Next(noise.Length + 1)).Order().ToArray(), "noise " + round);
        }
        var longOsc = new List<byte> { 27, (byte)']' };
        longOsc.AddRange(Enumerable.Repeat((byte)'a', 1_000_010));
        longOsc.Add(7);
        longOsc.AddRange(Encoding.UTF8.GetBytes("after"));
        longOsc.AddRange([27, (byte)']', (byte)'b', 27, (byte)'\\']);
        foreach (var cuts in new[] { Array.Empty<int>(), [500_000], [1, 1_000_002, 1_000_003], [1_000_012] })
            Compare(longOsc.ToArray(), cuts, "long");
        var capped = new List<byte> { 27, (byte)']' };
        capped.AddRange(Enumerable.Repeat((byte)'a', 1_000_000));
        capped.Add(7);
        Compare(capped.ToArray(), [], "cap exact");
        var big = Encoding.UTF8.GetBytes("\u001b]start\a" + string.Concat(Enumerable.Repeat("x終\U0001f989y", 30000)) + "\u001b]end;0\a");
        Compare(big, [], "truncate");
        Compare(big, [1001, 70001, 70002], "truncate split");
    }
}
