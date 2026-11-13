using System.Diagnostics;

namespace AgentTerm.Terminal;

public sealed record TerminalSearchProgress(long Matches, bool Complete, bool Found = false)
{
    public long HistoryVersion { get; init; }
    /// <summary>1-based position of the selected match in screen order, oldest first. Set only on a complete result that found a match.</summary>
    public long? SelectedOrdinal { get; init; }
}

/// <summary>Formatted history owned by the caller. Bytes are UTF-8 exactly as the native formatter produced them.</summary>
public sealed record TerminalHistoryExport(TerminalExportFormat Format, byte[] Utf8)
{
    public string Text => System.Text.Encoding.UTF8.GetString(Utf8);
}

/// <summary>HTML and plain text of one selection, formatted in a single terminal exclusion.</summary>
public sealed record TerminalSelectionCopy(string Html, string Text);

public sealed partial class TerminalSession
{
    private static readonly TimeSpan SearchSlice = TimeSpan.FromMilliseconds(2);
    private const int ProvisionalDelayMs = 100, ProgressIntervalMs = 50, StaleRetryDelayMs = 16;
    private readonly SemaphoreSlim _searchOwner = new(1, 1);
    private Timer? _historyIdle;
    private ulong? _compressionToken;
    private long _compressionActivityAt;

    public long HistoryVersion { get { lock (_gate) return _terminal.ContentVersion; } }

    public void NavigatePrompt(int delta) => MutateView(() => _terminal.NavigatePrompt(delta));

    /// <summary>
    /// Formats history or the selection in session order. Native formatting and the owned byte copy hold terminal
    /// exclusion; decoding and file writes happen after it is released. Null means selection export without a selection.
    /// </summary>
    public Task<TerminalHistoryExport?> ExportHistoryAsync(TerminalExportFormat format, bool selectionOnly = false, CancellationToken cancellationToken = default) =>
        DispatchAsync<TerminalHistoryExport?>(_ =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfClosed();
                var bytes = _terminal.ExportHistoryUtf8(format, selectionOnly);
                cancellationToken.ThrowIfCancellationRequested();
                ScheduleHistoryCompression();
                return bytes is null ? null : new TerminalHistoryExport(format, bytes);
            }
        });

    /// <summary>Formats the selection as HTML and plain text from one terminal snapshot. Null without a selection.</summary>
    public Task<TerminalSelectionCopy?> CopySelectionRichAsync(CancellationToken cancellationToken = default) =>
        DispatchAsync<TerminalSelectionCopy?>(_ =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[]? html;
            string? text;
            lock (_gate)
            {
                ThrowIfClosed();
                html = _terminal.ExportHistoryUtf8(TerminalExportFormat.Html, true);
                text = html is null ? null : _terminal.ReadSelectionText();
                ScheduleHistoryCompression();
            }
            return html is null || text is null ? null : new TerminalSelectionCopy(System.Text.Encoding.UTF8.GetString(html), text);
        });

    /// <summary>Search yields between native step batches. One owner excludes query changes and disposal; the terminal lock protects each batch.</summary>
    /// <param name="retainSelection">
    /// Automatic refresh. Keeps a still-valid selected query match without advancing, and never replaces a selection the
    /// search does not own. Counts and highlights still update.
    /// </param>
    public async Task<TerminalSearchProgress> FindAsync(string query, bool forward, bool caseSensitive = false,
        IProgress<TerminalSearchProgress>? progress = null, CancellationToken cancellationToken = default, bool retainSelection = false)
    {
        ArgumentNullException.ThrowIfNull(query);
        await _searchOwner.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DispatchAsync(_ =>
            {
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfClosed();
                    _terminal.PrepareSearch(query, caseSensitive);
                }
            }).ConfigureAwait(false);
            if (query.Length == 0) return new(0, true);
            return await Task.Run(() => SearchAsync(forward, progress, retainSelection, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { lock (_gate) ScheduleHistoryCompression(); }
            finally { _searchOwner.Release(); }
        }
    }

    private async Task<TerminalSearchProgress> SearchAsync(bool forward, IProgress<TerminalSearchProgress>? progress, bool retain, CancellationToken token)
    {
        var started = Stopwatch.StartNew();
        long reportedAt = -ProgressIntervalMs;
        bool owned, keepUserSelection;
        lock (_gate)
        {
            ThrowIfClosed();
            owned = retain && _terminal.IsSearchMatchSelected();
            keepUserSelection = retain && !owned && _terminal.HasUserSelection();
        }
        void Report(long matches)
        {
            if (started.ElapsedMilliseconds - reportedAt < ProgressIntervalMs) return;
            reportedAt = started.ElapsedMilliseconds;
            progress?.Report(new(matches, false));
        }
        void Provisional(long version)
        {
            if (owned || keepUserSelection || started.ElapsedMilliseconds < ProvisionalDelayMs) return;
            bool selected;
            long matches;
            lock (_renderGate) lock (_gate)
            {
                ThrowIfClosed();
                if (_terminal.ContentVersion != version) return;
                selected = _terminal.SelectSearchMatch(forward, 32);
                matches = _terminal.SearchNeedsExactFilter ? (selected ? 1 : 0) : _terminal.SearchCount();
            }
            if (!selected) return;
            owned = true;
            progress?.Report(new(matches, false, true) { HistoryVersion = version });
        }

        long? fed = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (fed is { } previous)
            {
                bool changed;
                lock (_gate) { ThrowIfClosed(); changed = _terminal.ContentVersion != previous; }
                if (changed) await Task.Delay(StaleRetryDelayMs, token).ConfigureAwait(false);
            }
            long version;
            GhosttyTerminal.SearchResults? completed;
            lock (_gate)
            {
                ThrowIfClosed();
                fed = version = _terminal.ContentVersion;
                completed = _terminal.CompletedSearch;
                if (completed is null) _terminal.FeedSearch();
            }
            if (completed is null) Provisional(version);
            int status = completed is null ? 0 : 2;
            var stale = false;
            while (status == 0)
            {
                token.ThrowIfCancellationRequested();
                long matches;
                lock (_gate)
                {
                    ThrowIfClosed();
                    if (_terminal.ContentVersion != version) { stale = true; break; }
                    // Native 1.0.1 tickHistory reads the live tracked history.start_pin despite the header's
                    // independent-tick contract, so every tick stays serialized with terminal mutations.
                    var slice = Stopwatch.GetTimestamp();
                    do status = _terminal.TickSearch(); while (status == 0 && Stopwatch.GetElapsedTime(slice) < SearchSlice);
                    matches = _terminal.SearchNeedsExactFilter ? 0 : _terminal.SearchCount();
                }
                Report(matches);
                await Task.Yield();
            }
            if (stale || status != 2) continue;

            long count, exact;
            bool[]? exactFlags = null;
            lock (_gate)
            {
                ThrowIfClosed();
                if (_terminal.ContentVersion != version) continue;
                count = completed?.Candidates ?? _terminal.SearchCount();
                exact = completed?.Matches ?? count;
                exactFlags = completed?.ExactFlags;
                if (completed is null && _terminal.SearchNeedsExactFilter) exactFlags = new bool[_terminal.CaptureSearchCandidates()];
            }
            if (completed is null && exactFlags is not null)
            {
                exact = 0;
                for (var i = 0; i < exactFlags.Length && !stale;)
                {
                    token.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        ThrowIfClosed();
                        if (_terminal.ContentVersion != version) { stale = true; break; }
                        var slice = Stopwatch.GetTimestamp();
                        do { if (exactFlags[i] = _terminal.ExactSearchCandidate(i)) exact++; i++; }
                        while (i < exactFlags.Length && Stopwatch.GetElapsedTime(slice) < SearchSlice);
                    }
                    Report(exact);
                    Provisional(version);
                    await Task.Yield();
                }
                if (stale) continue;
            }

            bool found;
            lock (_gate)
            {
                ThrowIfClosed();
                if (_terminal.ContentVersion != version) continue;
                if (completed is null) _terminal.CompleteSearch(count, exact, exactFlags);
                found = owned && _terminal.IsSearchMatchSelected();
            }
            bool Exact(long index) => exactFlags is null || index < exactFlags.Length && exactFlags[index];
            for (long steps = 0; !found && !keepUserSelection && exact != 0 && steps < count;)
            {
                token.ThrowIfCancellationRequested();
                lock (_renderGate) lock (_gate)
                {
                    ThrowIfClosed();
                    if (_terminal.ContentVersion != version) { stale = true; break; }
                    var slice = Stopwatch.GetTimestamp();
                    do { found = _terminal.SelectSearchMatch(forward, 1, Exact); steps++; }
                    while (!found && steps < count && Stopwatch.GetElapsedTime(slice) < SearchSlice);
                }
                if (!found) await Task.Yield();
            }
            if (stale) continue;
            long? ordinal = null;
            lock (_gate)
            {
                ThrowIfClosed();
                if (_terminal.ContentVersion != version) continue;
                found = found && _terminal.IsSearchMatchSelected();
                if (found && _terminal.SearchSelectedIndex() is { } index)
                    ordinal = exactFlags is null ? count - index : exact - exactFlags.AsSpan(0, (int)Math.Min(index, exactFlags.Length)).Count(true);
            }
            var result = new TerminalSearchProgress(exact, true, found) { HistoryVersion = version, SelectedOrdinal = ordinal };
            progress?.Report(result);
            return result;
        }
    }

    private void ThrowIfClosed() => ObjectDisposedException.ThrowIf(_disposeTask is not null, this);

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
        _compressionActivityAt = Stopwatch.GetTimestamp();
        _historyIdle ??= new Timer(CompressIdle);
        _historyIdle.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }
    private void CompressIdle(object? state)
    {
        if (!Monitor.TryEnter(_renderGate)) { RetryCompressionWhenIdle(); return; }
        try
        {
            if (!Monitor.TryEnter(_gate)) { RetryCompressionWhenIdle(); return; }
            try
            {
                if (_disposeTask is not null) return;
                var token = _terminal.CompressionActivity;
                if (_compressionToken != token)
                {
                    _compressionToken = token;
                    _compressionActivityAt = Stopwatch.GetTimestamp();
                    _historyIdle!.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
                    return;
                }
                var remaining = TimeSpan.FromSeconds(2) - Stopwatch.GetElapsedTime(_compressionActivityAt);
                if (remaining > TimeSpan.Zero)
                {
                    _historyIdle!.Change(remaining, Timeout.InfiniteTimeSpan);
                    return;
                }
                if (_terminal.CompressHistoryStep()) _historyIdle!.Change(25, Timeout.Infinite);
            }
            finally { Monitor.Exit(_gate); }
        }
        finally { Monitor.Exit(_renderGate); }
    }

    private void RetryCompressionWhenIdle()
    {
        try { _historyIdle?.Change(250, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }
}
