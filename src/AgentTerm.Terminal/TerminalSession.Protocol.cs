namespace AgentTerm.Terminal;

public sealed partial class TerminalSession
{
    private readonly TerminalProtocolStream _stream;
    private readonly List<TerminalProtocolEvent> _protocolDispatch = [];

    /// <summary>
    /// Raised on the reader thread, outside the session lock and before <see cref="OutputAvailable"/>, once per native
    /// protocol event in stream order: titles, working directories, OSC 133 steps, OSC 7501 reports and full resets.
    /// Session state such as <see cref="Title"/> and <see cref="ReadProgramStatuses"/> already reflects the whole chunk.
    /// A reset clears the title and directory without separate change events. Handlers must return promptly.
    /// </summary>
    public event Action<TerminalProtocolEvent>? ProtocolEvent;

    // Only authenticated private markers are buffered. Every other OSC is located by native boundaries and skipped.
    private TerminalProtocolStream ConfigureProtocol()
    {
        var stream = new TerminalProtocolStream(_terminal, "777;" + _promptToken + ";");
        stream.Sequence += _prompt.Accept;
        stream.ProtocolEvent += ApplyProtocolEvent;
        stream.Output += bytes => _owned?.Capture.Feed(bytes);
        stream.ControlStringStarted += () => _owned?.Capture.EndEscape();
        return stream;
    }

    // Runs inside _gate during a write. It only updates session state and queues a copy for dispatch after the lock.
    private void ApplyProtocolEvent(TerminalProtocolEvent item)
    {
        switch (item)
        {
            case TerminalProgramStatusReport report: _programStatus.Apply(report); break;
            case TerminalSemanticPrompt { StartsNewPrompt: true }: _programStatus.EndCommand(); break;
            case TerminalTitleChanged title: _title = title.Title; break;
            case TerminalReset:
                _programStatus.Reset();
                ClearTitle();
                break;
        }
        if (ProtocolEvent is not null) _protocolDispatch.Add(item);
    }

    private TerminalProtocolEvent[]? TakeProtocolDispatch()
    {
        if (_protocolDispatch.Count == 0) return null;
        var events = _protocolDispatch.ToArray();
        _protocolDispatch.Clear();
        return events;
    }

    private void DispatchProtocolEvents(TerminalProtocolEvent[]? events)
    {
        if (events is null) return;
        foreach (var item in events) ProtocolEvent?.Invoke(item);
    }
}
