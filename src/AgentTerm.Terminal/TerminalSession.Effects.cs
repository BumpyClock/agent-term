namespace AgentTerm.Terminal;

// Effects programs request through the terminal: clipboard, bell, desktop notifications, progress, and window resize.
// The terminal queues them while it parses output. This file delivers them after the terminal lock is released.
public sealed partial class TerminalSession
{
    /// <summary>
    /// Raised on the reader thread, outside every session lock, with the effects a chunk of output requested in order.
    /// Handlers must return promptly and marshal to the UI themselves. An exception from a handler is ignored.
    /// </summary>
    public event Action<IReadOnlyList<TerminalEffect>>? EffectsAvailable;

    /// <summary>Preferences, clipboard adapter, and pending approvals for this session.</summary>
    public TerminalEffects Effects => _terminal.Effects;

    /// <summary>Applies host preferences. Takes effect for the next request. Pass the clipboard once; null keeps the current one.</summary>
    public void ConfigureEffects(TerminalEffectOptions options, ITerminalClipboard? clipboard = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _terminal.Effects.Options = options;
        if (clipboard is not null) _terminal.Effects.Clipboard = clipboard;
    }

    /// <summary>Allows or denies a clipboard write the user was asked about. Returns the outcome of the copy.</summary>
    public ClipboardOutcome ResolveClipboardWrite(long requestId, bool allow) => _terminal.Effects.ResolveWrite(requestId, allow);

    private void FlushEffects()
    {
        var effects = _terminal.Effects;
        if (!effects.HasPending) return;
        var batch = effects.TakePending();
        if (batch.Count == 0) return;
        try { EffectsAvailable?.Invoke(batch); }
        catch (Exception) { effects.RecordFault(); }
    }

    /// <summary>The shell exited. A program that cannot clear its own progress or approvals no longer can.</summary>
    private void EndEffects()
    {
        _terminal.Effects.Reset();
        FlushEffects();
    }
}
