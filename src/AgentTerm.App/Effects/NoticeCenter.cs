namespace AgentTerm_App.Effects;

/// <summary>
/// The cards one pane shows. Bounded, ordered oldest to newest, and merged by key so a program that repeats a request
/// updates one card with a count instead of stacking cards. Not thread safe: UI thread only.
/// </summary>
internal sealed class NoticeCenter
{
    internal const int MaxVisible = 4;

    private readonly List<Notice> _items = [];
    private Notice[] _snapshot = [];
    private long _nextId;

    internal event Action? Changed;

    internal IReadOnlyList<Notice> Items => _snapshot;

    /// <summary>Shows a notice, replacing one with the same key. Returns the stored notice.</summary>
    internal Notice Show(string key, NoticeSeverity severity, string title, string message, long expiresAt,
        IReadOnlyList<NoticeAction>? actions = null, long requestId = 0, bool fromProgram = false)
    {
        var repeats = 1;
        var index = _items.FindIndex(item => item.Key == key);
        if (index >= 0)
        {
            repeats = _items[index].Repeats + 1;
            _items.RemoveAt(index);
        }
        var notice = new Notice(++_nextId, key, severity, title, message, repeats, expiresAt, actions ?? [NoticeAction.Dismiss], requestId, fromProgram);
        _items.Add(notice);
        while (_items.Count > MaxVisible)
        {
            // Drop what the user can ignore before what waits for a decision.
            var victim = _items.FindIndex(item => item.ExpiresAt != 0);
            _items.RemoveAt(victim < 0 ? 0 : victim);
        }
        Publish();
        return notice;
    }

    internal Notice? Find(long id) => _items.FirstOrDefault(item => item.Id == id);

    internal bool Dismiss(long id)
    {
        var index = _items.FindIndex(item => item.Id == id);
        if (index < 0) return false;
        _items.RemoveAt(index);
        Publish();
        return true;
    }

    /// <summary>Counts repeats the program made before the notice was shown, so the card can say how many.</summary>
    internal void AddRepeats(long id, int extra)
    {
        var index = _items.FindIndex(item => item.Id == id);
        if (index < 0 || extra <= 0) return;
        _items[index] = _items[index] with { Repeats = _items[index].Repeats + extra };
        Publish();
    }

    /// <summary>Starts the clock on matching notices that waited for the user to look.</summary>
    internal void SetExpiry(Func<Notice, bool> match, long expiresAt)
    {
        var changed = false;
        for (var index = 0; index < _items.Count; index++)
        {
            if (!match(_items[index])) continue;
            _items[index] = _items[index] with { ExpiresAt = expiresAt };
            changed = true;
        }
        if (changed) Publish();
    }

    internal void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Publish();
    }

    /// <summary>Removes notices whose time has passed. Returns true when any were removed.</summary>
    internal bool Expire(long now)
    {
        var removed = _items.RemoveAll(item => item.ExpiresAt != 0 && item.ExpiresAt <= now);
        if (removed != 0) Publish();
        return removed != 0;
    }

    /// <summary>The earliest expiry, so the view can sleep until then instead of polling. Null when nothing expires.</summary>
    internal long? NextExpiry
    {
        get
        {
            long? next = null;
            foreach (var item in _items)
                if (item.ExpiresAt != 0 && (next is null || item.ExpiresAt < next)) next = item.ExpiresAt;
            return next;
        }
    }

    private void Publish()
    {
        _snapshot = [.. _items];
        Changed?.Invoke();
    }
}
