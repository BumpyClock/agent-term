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
    private readonly Func<ShellDirectory, Task<GitRepositoryDiscovery>> _locateWsl;
    private readonly Dictionary<ShellDirectory, (DateTimeOffset Started, Task<GitRepositoryDiscovery> Task)> _discoveries = [];
    private const int DiscoveryCacheLimit = 256;
    private readonly Func<ShellDirectory, GitRepository?, Action, IDisposable?> _watch;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _gate = new();

    internal GitStatusHub(Func<ShellDirectory, CancellationToken, Task<GitResult>> query,
        Func<ShellDirectory, GitRepository?>? locate = null, Func<ShellDirectory, GitRepository?, Action, IDisposable?>? watch = null,
        Func<DateTimeOffset>? clock = null, Func<ShellDirectory, Task<GitRepositoryDiscovery>>? locateWsl = null)
    {
        _query = query;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _locate = locate ?? (directory => directory.WslDistribution is null ? GitRepository.Locate(directory.Path) : null);
        _locateWsl = locateWsl ?? new GitStatusProvider().LocateWslAsync;
        _watch = watch ?? ((directory, repository, changed) => directory.WslDistribution is not null ? null
            : repository is null ? GitRepository.WatchCreation(directory, changed) : repository.Watch(changed));
    }

    internal bool Paused { get; set; }
    internal int EntryCount { get { lock (_gate) return _entries.Count; } }

    private Task<GitRepositoryDiscovery> DiscoverAsync(ShellDirectory directory, bool refresh = false)
    {
        if (directory.WslDistribution is null) return Task.FromResult(new GitRepositoryDiscovery(_locate(directory)));
        lock (_gate)
        {
            var now = _clock();
            if (_discoveries.TryGetValue(directory, out var cached) &&
                (!cached.Task.IsCompleted || !refresh && now < cached.Started + UnwatchedInterval)) return cached.Task;
            foreach (var expired in _discoveries.Where(pair => pair.Value.Task.IsCompleted && now >= pair.Value.Started + UnwatchedInterval).Select(pair => pair.Key).ToArray())
                _discoveries.Remove(expired);
            var task = _locateWsl(directory);
            if (_discoveries.Count < DiscoveryCacheLimit || _discoveries.ContainsKey(directory)) _discoveries[directory] = (now, task);
            return task;
        }
    }

    internal async Task<Entry> AcquireAsync(ShellDirectory directory)
    {
        var discovery = await DiscoverAsync(directory).ConfigureAwait(false);
        var repository = discovery.Repository;
        var canonical = repository is null ? directory : directory with { Path = repository.WorkTree };
        var key = directory.WslDistribution is { } distribution
            ? $"wsl:{distribution}:{canonical.Path}" : LocalKey(canonical.Path);
        if (repository is not null) key += directory.WslDistribution is null
            ? $"|{LocalKey(repository.GitDirectory)}|{LocalKey(repository.CommonDirectory)}"
            : $"|{repository.GitDirectory}|{repository.CommonDirectory}";
        Entry? entry;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out entry))
            {
                entry.Users++;
                if (repository is null && entry.Result?.State != discovery.State) { entry.Result = new(discovery.State); entry.Version++; }
                return entry;
            }
            entry = new(key, canonical, repository);
            if (repository is null) { entry.Result = new(discovery.State); entry.Version = 1; }
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

    internal bool NeedsRefresh(Entry entry, DateTimeOffset now)
    {
        lock (_gate) return entry.Repository is not null && entry.Running is null && Due(entry, now);
    }

    private static bool Due(Entry entry, DateTimeOffset now)
    {
        var backoff = entry.Duration * 10;
        return entry.Completed == DateTimeOffset.MinValue
            || now >= entry.Completed + Max(entry.Watcher is null || entry.Repository is null ? UnwatchedInterval : WatchedInterval, backoff)
            || (entry.Stale && now >= entry.StaleSince + Debounce && now >= entry.Completed + Max(ChangeInterval, backoff));
    }

    internal Task<GitResult>? Ensure(Entry entry, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (entry.Running is not null || entry.Users == 0 || entry.Repository is null) return entry.Running;
            if (!Due(entry, now)) return null;
            entry.Stale = false;
            return entry.Running = Task.Run(() => RunAsync(entry, now));
        }
    }

    private async Task<GitResult> RunAsync(Entry entry, DateTimeOffset started)
    {
        var clock = Stopwatch.StartNew();
        GitResult? result = null;
        try
        {
            // A removed repository can make Git walk into its parent. Never publish that result.
            if ((await DiscoverAsync(entry.Directory).ConfigureAwait(false)).Repository != entry.Repository) return new(GitQueryState.NoRepository);
            var queried = await _query(entry.Directory, entry.Lifetime.Token).ConfigureAwait(false);
            if ((await DiscoverAsync(entry.Directory, refresh: true).ConfigureAwait(false)).Repository == entry.Repository) result = queried;
            return result ?? new(GitQueryState.NoRepository);
        }
        finally
        {
            lock (_gate)
            {
                entry.Running = null;
                entry.Duration = clock.Elapsed;
                entry.Completed = started + clock.Elapsed;
                if (entry.Users > 0 && result is not null && result != entry.Result)
                {
                    entry.Result = result;
                    entry.Version++;
                }
            }
        }
    }

    private static string LocalKey(string path) => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;

    private static TimeSpan Max(TimeSpan first, TimeSpan second) => first > second ? first : second;

    internal sealed class Entry(string key, ShellDirectory directory, GitRepository? repository)
    {
        internal string Key { get; } = key;
        internal ShellDirectory Directory { get; } = directory;
        internal GitRepository? Repository { get; } = repository;
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
