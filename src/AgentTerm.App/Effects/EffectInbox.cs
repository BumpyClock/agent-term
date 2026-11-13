using AgentTerm.Terminal;

namespace AgentTerm_App.Effects;

/// <summary>
/// Collects effect batches from the terminal reader thread until the UI thread takes them. Bounded, so a program that
/// floods requests while the UI is busy cannot grow it: the oldest bells, notifications, and clipboard notices are dropped
/// first. Held clipboard writes and the latest progress and resize request are kept. Thread safe.
/// </summary>
internal sealed class EffectInbox
{
    internal const int Capacity = 128;

    private readonly object _gate = new();
    private readonly List<TerminalEffect> _effects = [];

    /// <summary>Returns true when the inbox was empty, so the caller knows to schedule one drain.</summary>
    internal bool Add(IReadOnlyList<TerminalEffect> batch)
    {
        lock (_gate)
        {
            var wasEmpty = _effects.Count == 0;
            foreach (var effect in batch)
            {
                if (effect is ResetEffect) _effects.Clear();
                else if (effect is ProgressEffect) _effects.RemoveAll(existing => existing is ProgressEffect);
                else if (effect is ResizeRequestEffect) _effects.RemoveAll(existing => existing is ResizeRequestEffect);
                _effects.Add(effect);
            }
            while (_effects.Count > Capacity)
            {
                var victim = _effects.FindIndex(effect => effect is BellEffect or NotificationEffect or ClipboardEffect { Kind: not ClipboardNoticeKind.WriteRequested });
                if (victim < 0) victim = _effects.FindIndex(effect => effect is ClipboardEffect);
                if (victim < 0) break;
                _effects.RemoveAt(victim);
            }
            return wasEmpty;
        }
    }

    internal IReadOnlyList<TerminalEffect> Take()
    {
        lock (_gate)
        {
            if (_effects.Count == 0) return [];
            var taken = _effects.ToArray();
            _effects.Clear();
            return taken;
        }
    }

    internal void Clear()
    {
        lock (_gate) _effects.Clear();
    }
}
