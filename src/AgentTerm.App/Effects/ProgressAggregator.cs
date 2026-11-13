using AgentTerm.Terminal;

namespace AgentTerm_App.Effects;

/// <summary>
/// Chooses what a window's single taskbar button shows when several panes report progress. An error outranks a pause, a pause
/// outranks determinate progress, and determinate progress outranks indeterminate. The most recent report wins within a tier.
/// The host is called only when the choice changes. UI thread only.
/// </summary>
internal sealed class ProgressAggregator(IEffectHost host)
{
    private sealed record Entry(object Source, nint Window, TerminalProgressState State, int Percent, long Order);

    private readonly List<Entry> _entries = [];
    private readonly Dictionary<nint, TaskbarProgress> _shown = [];
    private long _order;

    /// <summary>Records a pane's progress. A Remove state or a null state clears it. A percent of -1 keeps the pane's previous one.</summary>
    internal void Update(object source, nint window, TerminalProgressState? state, int percent)
    {
        var index = _entries.FindIndex(entry => ReferenceEquals(entry.Source, source));
        var previous = index >= 0 ? _entries[index] : null;
        if (index >= 0) _entries.RemoveAt(index);
        var oldWindow = previous?.Window ?? window;
        if (state is { } value and not TerminalProgressState.Remove)
        {
            var kept = percent < 0 ? previous?.Percent ?? (value == TerminalProgressState.Error ? 100 : 0) : percent;
            _entries.Add(new Entry(source, window, value, Math.Clamp(kept, 0, 100), ++_order));
        }
        Refresh(oldWindow);
        if (window != oldWindow) Refresh(window);
    }

    internal void Remove(object source) => Update(source, 0, null, 0);

    /// <summary>What the host was last told for a window.</summary>
    internal TaskbarProgress Shown(nint window) => _shown.GetValueOrDefault(window, TaskbarProgress.None);

    private void Refresh(nint window)
    {
        var best = _entries.Where(entry => entry.Window == window)
            .OrderByDescending(entry => Rank(entry.State)).ThenByDescending(entry => entry.Order).FirstOrDefault();
        var next = best is null ? TaskbarProgress.None : best.State switch
        {
            TerminalProgressState.Error => new TaskbarProgress(TaskbarProgressKind.Error, best.Percent),
            TerminalProgressState.Pause => new TaskbarProgress(TaskbarProgressKind.Paused, best.Percent),
            TerminalProgressState.Set => new TaskbarProgress(TaskbarProgressKind.Normal, best.Percent),
            _ => new TaskbarProgress(TaskbarProgressKind.Indeterminate, 0),
        };
        if (_shown.GetValueOrDefault(window, TaskbarProgress.None) == next) return;
        if (next.Kind == TaskbarProgressKind.None) _shown.Remove(window);
        else _shown[window] = next;
        host.SetTaskbarProgress(window, next);
    }

    private static int Rank(TerminalProgressState state) => state switch
    {
        TerminalProgressState.Error => 3,
        TerminalProgressState.Pause => 2,
        TerminalProgressState.Set => 1,
        _ => 0,
    };
}
