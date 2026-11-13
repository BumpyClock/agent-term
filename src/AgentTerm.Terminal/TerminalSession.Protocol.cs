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
        stream.Output += FeedOwnedOutput;
        stream.ControlStringStarted += () => _owned?.Capture.EndEscape();
        _prompt.Marker += OwnedOutputMarker;
        return stream;
    }

    // The tracked first output cell of the owned command, set at its authenticated start marker.
    private (string Id, CommandOutputStart Start)? _ownedOutputStart;

    // Both handlers run inside _gate between native write segments, so the terminal is consistent and readable.
    private void FeedOwnedOutput(ReadOnlySpan<byte> bytes)
    {
        if (_owned is not { } owned) return;
        owned.Capture.Feed(bytes);
        if (owned.Capture.Ended && _prompt.ReadyCommandId == owned.Id) ResolveOwnedOutput(owned);
    }

    private void OwnedOutputMarker(string message)
    {
        if (_owned is not { } owned) return;
        if (message == "start;" + owned.Id)
        {
            ReleaseOwnedOutputStart();
            try { if (_terminal.TrackCommandOutputStart() is { } start) _ownedOutputStart = (owned.Id, start); }
            catch (InvalidOperationException) { }
        }
        // The marker arrives before ShellPromptProtocol records the ready id, so compare the message itself.
        else if (owned.Capture.Ended && message == "ready;" + owned.Id) ResolveOwnedOutput(owned);
    }

    /// <summary>
    /// Settles the owned result when both the visible end fence and the authenticated ready marker arrived, whichever
    /// came second under ConPTY reordering. By then the end fence and OSC 133 D are both in the terminal.
    /// </summary>
    private void ResolveOwnedOutput(OwnedCommand owned)
    {
        if (owned.Capture.Resolved) return;
        string? screen = null;
        if (_ownedOutputStart is { } tracked && tracked.Id == owned.Id)
        {
            try { screen = _terminal.ReadFencedCommandOutput(tracked.Start, owned.Capture.StartFence, owned.Capture.EndFence); }
            catch (InvalidOperationException) { }
        }
        ReleaseOwnedOutputStart();
        owned.Capture.Resolve(screen);
    }

    private void ReleaseOwnedOutputStart()
    {
        _ownedOutputStart?.Start.Dispose();
        _ownedOutputStart = null;
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
