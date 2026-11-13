using System.Text;

namespace AgentTerm.Terminal;

public delegate void OutputSpanHandler(ReadOnlySpan<byte> bytes);

public sealed class OscScanner
{
    private enum State { Ground, Escape, Osc, OscEscape, String }
    private State _state;
    private readonly StringBuilder _sequence = new();
    private readonly byte[] _pair = new byte[2];
    private int _length;
    public event OutputSpanHandler? Output;
    public event Action<string, int>? Sequence;
    public event Action? Reset;

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        var index = 0;
        while (index < bytes.Length)
        {
            if (_state == State.Ground)
            {
                var rest = bytes[index..];
                var escape = rest.IndexOf((byte)27);
                if (escape < 0) { Output?.Invoke(rest); return; }
                if (escape > 0) Output?.Invoke(rest[..escape]);
                index += escape;
            }
            else if (_state == State.String)
            {
                var skip = bytes[index..].IndexOfAny((byte)24, (byte)26, (byte)27);
                if (skip < 0) return;
                index += skip;
            }
            var value = bytes[index++];
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
                if (_state == State.Ground) { _pair[0] = 27; _pair[1] = value; Output?.Invoke(_pair); }
                continue;
            }
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
        }
    }

    private void Finish(int terminatorLength)
    {
        if (_length <= 1_000_000) Sequence?.Invoke(_sequence.ToString(), _length + 2 + terminatorLength);
        _sequence.Clear();
        _state = State.Ground;
    }
}
