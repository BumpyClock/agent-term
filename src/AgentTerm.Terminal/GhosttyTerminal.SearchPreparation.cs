using System.Diagnostics;

namespace AgentTerm.Terminal;

public sealed partial class GhosttyTerminal
{
    internal readonly record struct HighlightKey(long Content, long Offset, long Search, int Screen, ushort Columns, ushort Rows);
    private sealed class HighlightPreparation(HighlightKey key, ScreenSpan?[] candidates, long first)
    {
        internal readonly HighlightKey Key = key;
        internal readonly ScreenSpan?[] Candidates = candidates;
        internal int Index;
        internal readonly long First = first;
        internal readonly List<(TerminalSearchSpan Span, ScreenSpan Match)> Matches = [];
    }
    private SearchHandle? _highlightSearch;
    private HighlightPreparation? _highlightPreparation;
    private HighlightKey? _preparedHighlightKey;
    private TerminalSearchHighlights? _preparedHighlights;

    private HighlightKey CurrentHighlightKey() => new(ContentVersion, ReadNativeViewport().Offset, _searchRevision, ActiveScreen(), _columns, _rows);

    internal bool NeedsSearchHighlights() => _search is not null && _preparedHighlightKey != CurrentHighlightKey();

    internal TerminalSearchHighlights? ReadPreparedSearchHighlights() =>
        _search is not null && _preparedHighlightKey == CurrentHighlightKey() ? _preparedHighlights : null;

    internal unsafe bool PrepareSearchHighlightsSlice(TimeSpan budget)
    {
        if (_search is null) { _highlightPreparation = null; return true; }
        var key = CurrentHighlightKey();
        if (_preparedHighlightKey == key) return true;
        var dataKey = (key.Content, key.Offset, key.Screen);
        if (_highlightDataKey == dataKey)
        {
            PublishPreparedSearchHighlights(key, _highlightData);
            return true;
        }
        if (_highlightSearch is null)
        {
            ThrowIfFailed(SearchNative.ghostty_search_new(IntPtr.Zero, out var ptr, _handle), "Could not create highlight search.");
            _highlightSearch = new SearchHandle(ptr);
            fixed (byte* needle = _needle)
            {
                var value = new SearchString { Pointer = needle, Length = (nuint)_needle.Length };
                ThrowIfFailed(SearchNative.ghostty_search_set(_highlightSearch, SearchOptionNeedle, &value), "Could not set highlight search text.");
            }
        }
        var complete = PrepareHighlightDataSlice(key, dataKey, budget);
        if (complete) PublishPreparedSearchHighlights(key, _highlightData);
        return complete;
    }

    private unsafe bool PrepareHighlightDataSlice(HighlightKey key, (long Content, long Offset, int Screen) dataKey, TimeSpan budget)
    {
        var work = _highlightPreparation;
        if (work is null || work.Key != key)
        {
            ThrowIfFailed(SearchNative.ghostty_search_feed(_highlightSearch!), "Could not feed search highlights.");
            HighlightFeeds++;
            var count = ReadMatches(_highlightSearch!, SearchDataViewportMatches);
            var candidates = new ScreenSpan?[count];
            for (var i = 0; i < count; i++) candidates[i] = ToScreen(_matchScratch[i]);
            ThrowIfFailed(Native.ghostty_terminal_grid_ref(_handle, new Native.Point { Tag = PointViewport }, out var topRef), "Could not locate the viewport.");
            Native.PointValue top = default;
            ThrowIfFailed(SearchNative.ghostty_terminal_point_from_grid_ref(_handle, &topRef, PointScreen, &top), "Could not locate the viewport.");
            _highlightPreparation = work = new(key, candidates, top.Y);
        }
        var started = Stopwatch.GetTimestamp();
        while (work.Index < work.Candidates.Length)
        {
            var span = work.Candidates[work.Index++];
            long last = work.First + key.Rows - 1;
            if (span is { } found && found.EndY >= work.First && found.StartY <= last)
            {
                var exact = true;
                if (SearchNeedsExactFilter) { HighlightExactChecks++; exact = ExactSearchSpan(found); }
                if (exact)
                    for (long y = Math.Max(work.First, found.StartY); y <= Math.Min(last, found.EndY); y++)
                        work.Matches.Add((new((int)(y - work.First), y == found.StartY ? found.StartX : 0, y == found.EndY ? found.EndX : key.Columns - 1, false), found));
            }
            if (Stopwatch.GetElapsedTime(started) >= budget) return false;
        }
        _highlightData = [.. work.Matches];
        _highlightDataKey = dataKey;
        _highlightPreparation = null;
        return true;
    }

    private unsafe void PublishPreparedSearchHighlights(HighlightKey key, (TerminalSearchSpan Span, ScreenSpan Match)[] data)
    {
        Native.Selection selectedMatch = default;
        var selected = SearchNative.ghostty_search_get(_search!, SearchDataSelectedMatch, &selectedMatch) == NativeResult.Success ? ToScreen(selectedMatch) : null;
        var spans = new TerminalSearchSpan[data.Length];
        for (var i = 0; i < spans.Length; i++) spans[i] = data[i].Span with { Selected = data[i].Match == selected };
        _preparedHighlights = new(key.Content, key.Offset, key.Screen != 0, spans);
        _preparedHighlightKey = key;
    }
}
