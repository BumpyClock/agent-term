namespace AgentTerm.Terminal;

// Effects programs request through the terminal: clipboard, bell, desktop notifications, progress, and window resize.
// The terminal queues them while it parses output. This file signals availability after the terminal lock is released.
public sealed partial class TerminalSession
{
    /// <summary>
    /// Raised on the reader thread, outside every session lock, when effects are pending. The owner pulls Effects.TakePending on its delivery thread.
    /// Handlers must return promptly and marshal to the UI themselves. An exception from a handler is ignored.
    /// </summary>
    public event Action? EffectsAvailable;

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
        if (EffectsAvailable is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception) { effects.RecordFault(); }
        }
    }

    /// <summary>The shell exited. A program that cannot clear its own progress or approvals no longer can.</summary>
    private void EndEffects()
    {
        _terminal.Effects.Reset(clearHost: true);
        FlushEffects();
    }
}
