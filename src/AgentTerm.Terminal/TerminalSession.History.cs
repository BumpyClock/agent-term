namespace AgentTerm.Terminal;

public sealed record TerminalSearchProgress(long Matches, bool Complete, bool Found = false)
{
    public long HistoryVersion { get; init; }
}

public sealed partial class TerminalSession
{
    private readonly SemaphoreSlim _searchOwner = new(1, 1);
    private Timer? _historyIdle;
    private ulong? _compressionToken;

    public long HistoryVersion { get { lock (_gate) return _terminal.ContentVersion; } }

    public void NavigatePrompt(int delta) => MutateView(() => _terminal.NavigatePrompt(delta));
    public string? ExportHistory(TerminalExportFormat format, bool selectionOnly = false)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            var result = _terminal.ExportHistory(format, selectionOnly);
            ScheduleHistoryCompression();
            return result;
        }
    }

    /// <summary>Search yields between native steps. One owner excludes query changes and disposal; the terminal lock protects each step.</summary>
    /// <param name="retainSelection">Refresh counts without advancing a still-valid selected query match.</param>
    public async Task<TerminalSearchProgress> FindAsync(string query, bool forward, bool caseSensitive = false,
        IProgress<TerminalSearchProgress>? progress = null, CancellationToken cancellationToken = default, bool retainSelection = false)
    {
        ArgumentNullException.ThrowIfNull(query);
        await _searchOwner.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                _terminal.PrepareSearch(query, caseSensitive);
            }
            if (query.Length == 0) return new(0, true);
            return await Task.Run(async () =>
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                bool selectedDuringSearch;
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                    selectedDuringSearch = retainSelection && _terminal.IsSearchMatchSelected();
                }
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long version;
                    lock (_gate)
                    {
                        ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                        version = _terminal.ContentVersion;
                        _terminal.FeedSearch();
                    }
                    bool selected;
                    lock (_gate)
                    {
                        ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                        if (_terminal.ContentVersion != version) continue;
                        selected = selectedDuringSearch && _terminal.IsSearchMatchSelected();
                    }
                    if (!selected && started.ElapsedMilliseconds >= 100)
                    {
                        long matches;
                        lock (_renderGate) lock (_gate)
                        {
                            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                            if (_terminal.ContentVersion != version) continue;
                            selected = _terminal.SelectSearchMatch(forward, caseSensitive, query, 32);
                            matches = caseSensitive ? (selected ? 1 : 0) : _terminal.SearchCount();
                        }
                        if (selected) selectedDuringSearch = true;
                        if (selected) progress?.Report(new(matches, false, true) { HistoryVersion = version });
                    }
                    int status;
                    do
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        long matches;
                        lock (_gate)
                        {
                            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                            if (_terminal.ContentVersion != version) { status = 1; break; }
                            // Native 1.0.1 tickHistory still reads a live tracked pin despite the independent-tick header contract.
                            status = _terminal.TickSearch();
                            matches = caseSensitive ? 0 : _terminal.SearchCount();
                        }
                        progress?.Report(new(matches, false));
                        await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                    } while (status == 0);
                    if (status != 2) continue;
                    long count, exact = 0;
                    lock (_gate)
                    {
                        ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                        if (_terminal.ContentVersion != version) continue;
                        count = _terminal.SearchCount();
                    }
                    if (caseSensitive)
                    {
                        lock (_gate)
                        {
                            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                            if (_terminal.ContentVersion != version) continue;
                            _terminal.CaptureSearchCandidates();
                        }
                        for (long i = 0; i < count; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            lock (_gate)
                            {
                                if (_terminal.ContentVersion != version || _disposeTask is not null) break;
                                if (_terminal.ExactSearchCandidate(checked((int)i), query)) exact++;
                            }
                            if ((i & 31) == 0) await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    bool found;
                    lock (_gate)
                    {
                        ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                        if (_terminal.ContentVersion != version) continue;
                        found = selectedDuringSearch && _terminal.IsSearchMatchSelected();
                    }
                    for (long i = 0; !found && i < count && (!caseSensitive || exact != 0); i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        lock (_renderGate) lock (_gate)
                        {
                            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                            if (_terminal.ContentVersion != version) break;
                            found = _terminal.SelectSearchMatch(forward, caseSensitive, query, 1);
                        }
                        if (found || !caseSensitive) break;
                        if ((i & 31) == 0) await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                    }
                    lock (_gate)
                    {
                        ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                        if (_terminal.ContentVersion != version) continue;
                        found = found && _terminal.IsSearchMatchSelected();
                    }
                    var result = new TerminalSearchProgress(caseSensitive ? exact : count, true, found) { HistoryVersion = version };
                    progress?.Report(result);
                    return result;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _searchOwner.Release(); }
    }

    // Called with _gate held after output or reflow. The timer is one-shot and has no idle polling path.
    private void HistoryActivity()
    {
        ScheduleHistoryCompression();
    }
    private void ScheduleHistoryCompression()
    {
        if (_disposeTask is not null) return;
        var token = _terminal.CompressionActivity;
        if (_compressionToken == token) return;
        _compressionToken = token;
        _historyIdle ??= new Timer(CompressIdle);
        _historyIdle.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }
    private void CompressIdle(object? state)
    {
        lock (_renderGate) lock (_gate)
        {
            if (_disposeTask is not null) return;
            var token = _terminal.CompressionActivity;
            if (_compressionToken != token)
            {
                _compressionToken = token;
                _historyIdle!.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
                return;
            }
            if (_terminal.CompressHistoryStep()) _historyIdle!.Change(25, Timeout.Infinite);
        }
    }
}
