using System.Diagnostics;
using AgentTerm.Terminal;

namespace AgentTerm_App.PaneStatus;

/// <summary>Shares one Git query and result per repository. Watchers mark results stale; a slow interval covers missed changes.</summary>
internal sealed class GitStatusHub
{
    internal static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan ChangeInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan WatchedInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan UnwatchedInterval = TimeSpan.FromSeconds(5);
    internal static GitStatusHub Shared { get; } = new(new GitStatusProvider().QueryAsync);

    private readonly Func<ShellDirectory, CancellationToken, Task<GitResult>> _query;
    private readonly Func<ShellDirectory, GitRepository?> _locate;
    private readonly Func<ShellDirectory, GitRepository?, Action, IDisposable?> _watch;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _gate = new();

    internal GitStatusHub(Func<ShellDirectory, CancellationToken, Task<GitResult>> query,
        Func<ShellDirectory, GitRepository?>? locate = null, Func<ShellDirectory, GitRepository?, Action, IDisposable?>? watch = null,
        Func<DateTimeOffset>? clock = null)
    {
        _query = query;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _locate = locate ?? (directory => directory.WslDistribution is null ? GitRepository.Locate(directory.Path) : null);
        _watch = watch ?? ((directory, repository, changed) => repository?.Watch(changed) ?? GitRepository.WatchCreation(directory, changed));
    }

    internal bool Paused { get; set; }
    internal int EntryCount { get { lock (_gate) return _entries.Count; } }

    internal Entry Acquire(ShellDirectory directory)
    {
        var repository = _locate(directory);
        var key = directory.WslDistribution is { } distribution
            ? $"wsl:{distribution}:{directory.Path}" : (repository?.WorkTree ?? directory.Path).ToUpperInvariant();
        Entry? entry;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out entry)) { entry.Users++; return entry; }
            entry = new(key);
            _entries.Add(key, entry);
        }
        var watcher = _watch(directory, repository, () => Invalidate(entry, _clock()));
        lock (_gate)
        {
            if (entry.Users > 0) { entry.Watcher = watcher; watcher = null; }
        }
        watcher?.Dispose();
        return entry;
    }

    internal void Release(Entry entry)
    {
        lock (_gate)
        {
            if (--entry.Users > 0) return;
            _entries.Remove(entry.Key);
        }
        entry.Lifetime.Cancel();
        entry.Watcher?.Dispose();
    }

    private void Invalidate(Entry entry, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (entry.Stale) return;
            entry.Stale = true;
            entry.StaleSince = at;
        }
    }

    internal bool TryRead(Entry entry, ref int version, out GitResult result)
    {
        lock (_gate)
        {
            result = entry.Result!;
            if (entry.Version == version) return false;
            version = entry.Version;
            return true;
        }
    }

    internal Task<GitResult>? Ensure(Entry entry, ShellDirectory directory, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (entry.Running is not null || entry.Users == 0) return entry.Running;
            var backoff = entry.Duration * 10;
            var due = entry.Completed == DateTimeOffset.MinValue
                || now >= entry.Completed + Max(entry.Watcher is null ? UnwatchedInterval : WatchedInterval, backoff)
                || (entry.Stale && now >= entry.StaleSince + Debounce && now >= entry.Completed + Max(ChangeInterval, backoff));
            if (!due) return null;
            entry.Stale = false;
            return entry.Running = Task.Run(() => RunAsync(entry, directory, now));
        }
    }

    private async Task<GitResult> RunAsync(Entry entry, ShellDirectory directory, DateTimeOffset started)
    {
        var clock = Stopwatch.StartNew();
        GitResult? result = null;
        try
        {
            result = await _query(directory, entry.Lifetime.Token).ConfigureAwait(false);
            return result;
        }
        finally
        {
            lock (_gate)
            {
                entry.Running = null;
                entry.Duration = clock.Elapsed;
                entry.Completed = started + clock.Elapsed;
                if (result is not null && result != entry.Result)
                {
                    entry.Result = result;
                    entry.Version++;
                }
            }
        }
    }

    private static TimeSpan Max(TimeSpan first, TimeSpan second) => first > second ? first : second;

    internal sealed class Entry(string key)
    {
        internal string Key { get; } = key;
        internal CancellationTokenSource Lifetime { get; } = new();
        internal IDisposable? Watcher;
        internal int Users = 1;
        internal Task<GitResult>? Running;
        internal GitResult? Result;
        internal int Version;
        internal bool Stale;
        internal DateTimeOffset StaleSince;
        internal DateTimeOffset Completed = DateTimeOffset.MinValue;
        internal TimeSpan Duration;
    }
}
