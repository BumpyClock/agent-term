using System.Runtime.InteropServices;
using System.Text;

namespace AgentTerm.Terminal;

public delegate void OutputSpanHandler(ReadOnlySpan<byte> bytes);

/// <summary>
/// Feeds program output through the terminal's single native parse and reports, in exact stream order, the plain output
/// between control strings, completed OSC strings that match a retained prefix, and native protocol events.
/// Native boundaries locate every APC, DCS, SOS, PM and OSC string, so no managed escape state machine runs.
/// Callers serialize this with every other use of the terminal. Handlers run inside that serialization and must not
/// write to the terminal or throw.
/// </summary>
public sealed class TerminalProtocolStream
{
    private const byte Escape = 27;
    private static readonly byte[] Escapes = [.. Enumerable.Repeat(Escape, 16)];
    private readonly GhosttyTerminal _terminal;
    private readonly byte[] _prefix;
    private readonly int _limit;
    private readonly List<TerminalProtocolEvent> _events = [];
    private readonly List<byte> _content = [];
    private TerminalStringKind? _inside;
    private int _heldEscapes;
    private bool _awaitingTerminator;
    private bool _retained;
    private bool _markerPending;
    private int _length;

    /// <param name="retainPrefix">Only OSC content starting with these bytes is buffered and reported. Empty retains all.</param>
    /// <param name="retainLimit">Longest OSC content reported; longer strings are never buffered past this size.</param>
    public TerminalProtocolStream(GhosttyTerminal terminal, string retainPrefix = "", int retainLimit = 1_000_000)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentOutOfRangeException.ThrowIfNegative(retainLimit);
        _terminal = terminal;
        _prefix = Encoding.Latin1.GetBytes(retainPrefix);
        _limit = retainLimit;
        terminal.EnableProtocolEvents();
    }

    /// <summary>Bytes outside every control string, excluding string introducers and terminators.</summary>
    public event OutputSpanHandler? Output;

    /// <summary>A control string started. Any escape sequence still open in <see cref="Output"/> was cut short.</summary>
    public event Action? ControlStringStarted;

    /// <summary>
    /// A retained OSC completed with BEL or a full ESC \ terminator. Content is Latin-1 decoded bytes after ESC ],
    /// and the length counts introducer and terminator bytes. Cancelled or improperly terminated strings never arrive.
    /// </summary>
    public event Action<string, int>? Sequence;

    /// <summary>A native title, directory, semantic prompt, program status or reset event, at its stream position.</summary>
    public event Action<TerminalProtocolEvent>? ProtocolEvent;

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        while (!bytes.IsEmpty)
        {
            var consumed = _terminal.WriteUntilStringBoundary(bytes, out var stop);
            Consume(bytes[..consumed], stop);
            bytes = bytes[consumed..];
        }
        _terminal.FinishWrite();
    }

    private void Consume(ReadOnlySpan<byte> segment, TerminalWriteStop stop)
    {
        if (_awaitingTerminator && !segment.IsEmpty)
        {
            // The previous string ended on ESC. A backslash completes ST; anything else is a new escape sequence.
            _awaitingTerminator = false;
            if (segment[0] == '\\')
            {
                segment = segment[1..];
                if (_markerPending) Dispatch(2);
            }
            else _heldEscapes = 1;
            _markerPending = false;
        }
        var ends = (stop.Reached & TerminalBoundaries.Ends) != 0;
        var starts = (stop.Reached & TerminalBoundaries.Starts) != 0;
        var dispatchBel = false;
        if (ends)
        {
            if (_inside == TerminalStringKind.Osc) Collect(segment[..^1]);
            dispatchBel = End(stop);
        }
        else if (_inside == TerminalStringKind.Osc) Collect(starts ? segment[..^1] : segment);
        else if (_inside is null && starts) EmitBeforeString(segment[..^1]);
        else if (_inside is null) EmitOutput(segment);
        // Events from this segment, such as a reset in its plain run, precede the string that ends it.
        DrainEvents();
        if (dispatchBel) Dispatch(1);
        if (starts) Begin(stop.Reached);
    }

    private void Begin(TerminalBoundaries reached)
    {
        _inside = (reached & TerminalBoundaries.OscStart) != 0 ? TerminalStringKind.Osc
            : (reached & TerminalBoundaries.DcsStart) != 0 ? TerminalStringKind.Dcs : TerminalStringKind.Apc;
        _heldEscapes = 0;
        if (_inside == TerminalStringKind.Osc)
        {
            _content.Clear();
            _length = 0;
            _retained = true;
        }
        ControlStringStarted?.Invoke();
    }

    /// <summary>Returns true when a retained OSC ended with BEL and is ready to dispatch.</summary>
    private bool End(TerminalWriteStop stop)
    {
        var osc = _inside == TerminalStringKind.Osc;
        _inside = null;
        if (stop.End == TerminalStringEnd.StEsc)
        {
            _awaitingTerminator = true;
            _markerPending = osc && _retained;
            return false;
        }
        return osc && _retained && stop.End == TerminalStringEnd.Bel;
    }

    private void Collect(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty) return;
        _length = (int)Math.Min(_limit + 1L, (long)_length + content.Length);
        if (!_retained) return;
        if (content.Length > _limit - _content.Count) { Discard(); return; }
        var verified = _content.Count;
        _content.AddRange(content);
        var prefix = Math.Min(_content.Count, _prefix.Length);
        if (verified < prefix && !CollectionsMarshal.AsSpan(_content)[verified..prefix].SequenceEqual(_prefix.AsSpan(verified, prefix - verified)))
            Discard();
    }

    private void Discard()
    {
        _retained = false;
        _content.Clear();
    }

    private void Dispatch(int terminatorLength)
    {
        var content = CollectionsMarshal.AsSpan(_content);
        if (content.Length < _prefix.Length) return;
        Sequence?.Invoke(Encoding.Latin1.GetString(content), _length + 2 + terminatorLength);
    }

    // An ESC run directly before a string introducer belongs to that introducer, or was cut short by a C1 introducer.
    private void EmitBeforeString(ReadOnlySpan<byte> body)
    {
        body = body.TrimEnd(Escape);
        if (!body.IsEmpty) EmitOutput(body);
        _heldEscapes = 0;
    }

    // A trailing ESC run is held because the next chunk may turn it into a string introducer that output must not see.
    // Held bytes are released verbatim, so output never depends on where chunks split.
    private void EmitOutput(ReadOnlySpan<byte> body)
    {
        var text = body.TrimEnd(Escape);
        var trailing = body.Length - text.Length;
        if (text.IsEmpty)
        {
            _heldEscapes += trailing;
            return;
        }
        for (; _heldEscapes > 0; _heldEscapes -= Math.Min(_heldEscapes, Escapes.Length))
            Output?.Invoke(Escapes.AsSpan(0, Math.Min(_heldEscapes, Escapes.Length)));
        Output?.Invoke(text);
        _heldEscapes = trailing;
    }

    private void DrainEvents()
    {
        _terminal.TakeProtocolEvents(_events);
        if (_events.Count == 0) return;
        try
        {
            foreach (var item in _events) ProtocolEvent?.Invoke(item);
        }
        finally { _events.Clear(); }
    }
}
