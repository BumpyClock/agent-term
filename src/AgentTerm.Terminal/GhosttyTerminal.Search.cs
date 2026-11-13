using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentTerm.Terminal;

/// <summary>
/// One row of one search match, in the row space of <see cref="TerminalFrame.Cells"/>. Columns are inclusive.
/// A wide character's head column marks the whole cell. <see cref="Selected"/> marks the native current match.
/// </summary>
public readonly record struct TerminalSearchSpan(int Row, int StartColumn, int EndColumn, bool Selected);

/// <summary>
/// Owned search highlights for one frame capture. Coordinates were converted from native matches inside the same
/// terminal exclusion as the frame, so they describe exactly that frame and never reference native memory.
/// </summary>
public sealed record TerminalSearchHighlights(long ContentVersion, long ViewportOffset, bool AlternateScreen, TerminalSearchSpan[] Spans);

public sealed partial class GhosttyTerminal
{
    private const int SearchOptionNeedle = 1;
    private const int SearchDataTotal = 3, SearchDataSelectedIndex = 4, SearchDataSelectedMatch = 5, SearchDataMatches = 6, SearchDataViewportMatches = 7;
    private const int PointViewport = 1, PointScreen = 2;

    private SearchHandle? _search;
    private string? _searchQuery;
    private bool _searchCaseSensitive;
    private byte[] _needle = [];
    // Native matching is byte-exact except ASCII letters, so only a needle with one needs exact-case filtering.
    private bool _needleFoldsCase;
    private long _searchRevision;
    // Owned screen coordinates of native matches, newest first, captured at one ContentVersion. No grid reference
    // survives terminal exclusion; each exact check rebuilds its references from these coordinates.
    private ScreenSpan[] _candidates = [];
    private int _candidateCount;
    private Native.Selection[] _matchScratch = [];
    // One formatter and one needle-sized buffer serve every match comparison. Neither allocates per candidate.
    private FormatterHandle? _matchFormatter;
    private byte[] _matchText = [];
    // Where search last installed the terminal selection, in owned screen coordinates. A selection elsewhere belongs to the user.
    private ScreenSpan? _searchSelection;
    private (long Content, long Offset, long Search, int Screen) _highlightKey = (-1, -1, -1, -1);
    private TerminalSearchHighlights? _highlights;
    private SearchResults? _completedSearch;

    internal sealed record SearchResults(long ContentVersion, long Candidates, long Matches, bool[]? ExactFlags);
    internal SearchResults? CompletedSearch => _completedSearch?.ContentVersion == ContentVersion ? _completedSearch : null;
    internal void CompleteSearch(long candidates, long matches, bool[]? exactFlags) =>
        _completedSearch = new(ContentVersion, candidates, matches, exactFlags);

    private readonly record struct ScreenSpan(uint StartY, ushort StartX, uint EndY, ushort EndX);

    /// <summary>Finds the next match in screen order and selects it. Search wraps at either end.</summary>
    public bool Find(string query, bool forward, bool caseSensitive = false)
    {
        ArgumentNullException.ThrowIfNull(query);
        PrepareSearch(query, caseSensitive);
        if (query.Length == 0) return false;
        ThrowIfFailed(SearchNative.ghostty_search_run(_search!), "Could not search terminal history.");
        return SelectSearchMatch(forward);
    }

    /// <summary>Incremental search for a single-dispatcher owner such as an offline viewer. Cancel and await an old search before starting another.</summary>
    public async Task<bool> FindIncrementallyAsync(string query, bool forward, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        PrepareSearch(query, false);
        if (query.Length == 0) return false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FeedSearch();
            int status;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                status = TickSearch();
                await Task.Delay(1, cancellationToken);
            } while (status == 0);
            if (status == 2) return SelectSearchMatch(forward);
        }
    }

    internal unsafe void PrepareSearch(string query, bool caseSensitive)
    {
        ThrowIfDisposed();
        if (_search is not null && _searchQuery == query && _searchCaseSensitive == caseSensitive) return;
        ResetSearch();
        if (query.Length == 0) return;
        ThrowIfFailed(SearchNative.ghostty_search_new(IntPtr.Zero, out var ptr, _handle), "Could not create search.");
        _search = new SearchHandle(ptr);
        _needle = Encoding.UTF8.GetBytes(query);
        fixed (byte* p = _needle)
        {
            var needle = new SearchString { Pointer = p, Length = (nuint)_needle.Length };
            ThrowIfFailed(SearchNative.ghostty_search_set(_search, SearchOptionNeedle, &needle), "Could not set search text.");
        }
        _needleFoldsCase = _needle.Any(b => b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z');
        if (_matchText.Length < _needle.Length) _matchText = new byte[_needle.Length];
        _searchQuery = query; _searchCaseSensitive = caseSensitive;
    }

    /// <summary>True when Match case needs candidates filtered beyond native ASCII-insensitive matching.</summary>
    internal bool SearchNeedsExactFilter => _searchCaseSensitive && _needleFoldsCase;

    internal void FeedSearch()
    {
        _candidateCount = 0;
        ThrowIfFailed(SearchNative.ghostty_search_feed(_search!), "Could not feed search.");
    }
    internal unsafe int TickSearch()
    {
        int status = 0;
        ThrowIfFailed(SearchNative.ghostty_search_tick(_search!, &status), "Could not advance search.");
        return status;
    }
    internal unsafe long SearchCount()
    {
        nuint count = 0;
        ThrowIfFailed(SearchNative.ghostty_search_get(_search!, SearchDataTotal, &count), "Could not read search count.");
        return checked((long)count);
    }
    /// <summary>Native selected index, newest match first, or null without a selected match.</summary>
    internal unsafe long? SearchSelectedIndex()
    {
        nuint index = 0;
        var result = SearchNative.ghostty_search_get(_search!, SearchDataSelectedIndex, &index);
        if (result == NativeResult.NoValue) return null;
        ThrowIfFailed(result, "Could not read selected search index.");
        return checked((long)index);
    }

    /// <summary>Converts every native match to owned screen coordinates. The caller checks ContentVersion before using them.</summary>
    internal unsafe int CaptureSearchCandidates()
    {
        var count = ReadMatches(SearchDataMatches);
        if (_candidates.Length < count) _candidates = new ScreenSpan[count];
        _candidateCount = 0;
        for (var i = 0; i < count; i++)
        {
            // A match that cannot be converted was pruned by eviction; it stays a non-exact placeholder.
            _candidates[i] = ToScreen(_matchScratch[i]) ?? new ScreenSpan(uint.MaxValue, 0, 0, 0);
            _candidateCount++;
        }
        return count;
    }

    /// <summary>Exact-case test for one captured candidate. Valid only at the ContentVersion of the capture.</summary>
    internal bool ExactSearchCandidate(int index)
    {
        if (index >= _candidateCount || _candidates[index] is not { StartY: not uint.MaxValue } span) return false;
        if (Native.ghostty_terminal_grid_ref(_handle, ScreenPoint(span.StartX, span.StartY), out var start) != 0 ||
            Native.ghostty_terminal_grid_ref(_handle, ScreenPoint(span.EndX, span.EndY), out var end) != 0) return false;
        return MatchText(new Native.Selection { Start = start, End = end }, exact: true);
    }

    /// <summary>
    /// Selects up to <paramref name="maxCandidates"/> native steps toward the next match in screen order and installs
    /// the first acceptable one as the terminal selection. <paramref name="exact"/> optionally accepts by native
    /// selected index, so an exact-case caller that already classified every candidate formats nothing here.
    /// </summary>
    internal unsafe bool SelectSearchMatch(bool forward, long maxCandidates = long.MaxValue, Func<long, bool>? exact = null)
    {
        var originalOffset = ReadViewport().Offset;
        nuint count = 0;
        ThrowIfFailed(SearchNative.ghostty_search_get(_search!, SearchDataTotal, &count), "Could not read terminal search count.");
        for (nuint i = 0; i < count && i < (nuint)maxCandidates; i++)
        {
            // Native next goes toward older rows; screen-forward goes toward newer rows.
            var result = forward ? SearchNative.ghostty_search_select_prev(_search!) : SearchNative.ghostty_search_select_next(_search!);
            if (result == NativeResult.NoValue) break;
            ThrowIfFailed(result, "Could not select terminal search match.");
            _searchRevision++;
            Native.Selection match = default;
            ThrowIfFailed(SearchNative.ghostty_search_get(_search!, SearchDataSelectedMatch, &match), "Could not read terminal search match.");
            if (SearchNeedsExactFilter && !(exact is not null && SearchSelectedIndex() is { } index ? exact(index) : MatchText(match, exact: true))) continue;
            InstallSelection(match);
            _searchSelection = ToScreen(match);
            return true;
        }
        // Candidate filtering can move the viewport even when no exact match exists.
        if (ReadViewport().Offset != originalOffset) ScrollTo(originalOffset);
        return false;
    }

    /// <summary>Makes a derived selection the terminal-owned selection, replacing any gesture in progress.</summary>
    private unsafe void InstallSelection(Native.Selection selection)
    {
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 22, (IntPtr)(&selection)), "Could not set terminal selection.");
        if (_gesture is not null) ScrollNative.ghostty_selection_gesture_reset(_gesture, _handle);
        _anchor?.Dispose(); _anchor = null;
        _selectionScreen = ActiveScreen();
        _selectionRectangle = false;
        _selecting = false;
        _viewRevision++;
        CaptureSelectedViewport();
    }

    /// <summary>True when a terminal selection exists that search did not install, such as a pointer or keyboard selection.</summary>
    internal unsafe bool HasUserSelection()
    {
        ThrowIfDisposed();
        Native.Selection selected = default;
        var result = RenderNative.ghostty_terminal_get(_handle, (int)TerminalData.Selection, &selected);
        if (result == NativeResult.NoValue) return false;
        ThrowIfFailed(result, "Could not read terminal selection.");
        return _searchSelection is null || ToScreen(selected) != _searchSelection;
    }

    // Read both snapshots together. Native search owns the tracked match; no borrowed
    // grid reference survives this call or a terminal mutation.
    internal unsafe bool IsSearchMatchSelected()
    {
        ThrowIfDisposed();
        if (_search is null) return false;
        Native.Selection match = default, selected = default;
        var result = SearchNative.ghostty_search_get(_search, SearchDataSelectedMatch, &match);
        if (result == NativeResult.NoValue) return false;
        ThrowIfFailed(result, "Could not read selected search match.");
        result = RenderNative.ghostty_terminal_get(_handle, (int)TerminalData.Selection, &selected);
        if (result == NativeResult.NoValue) return false;
        ThrowIfFailed(result, "Could not read terminal selection.");
        if (match.Rectangle != selected.Rectangle ||
            !match.Start.Equals(selected.Start) || !match.End.Equals(selected.End)) return false;
        return MatchText(selected, _searchCaseSensitive);
    }

    /// <summary>
    /// Feeds the search and converts the viewport's matches to owned frame rows. Call only from a live frame capture
    /// under terminal exclusion, never from the render-hold callback, which runs inside a terminal write.
    /// Returns null without an active query. Unchanged content, viewport and selection reuse the previous instance.
    /// </summary>
    internal unsafe TerminalSearchHighlights? CaptureSearchHighlights()
    {
        ThrowIfDisposed();
        if (_search is null) return null;
        var native = ReadNativeViewport();
        var screen = ActiveScreen();
        var key = (ContentVersion, native.Offset, _searchRevision, screen);
        if (key == _highlightKey) return _highlights;
        ThrowIfFailed(SearchNative.ghostty_search_feed(_search), "Could not feed search highlights.");
        var count = ReadMatches(SearchDataViewportMatches);
        Native.Selection selectedMatch = default;
        var selected = SearchNative.ghostty_search_get(_search, SearchDataSelectedMatch, &selectedMatch) == NativeResult.Success ? ToScreen(selectedMatch) : null;
        ThrowIfFailed(Native.ghostty_terminal_grid_ref(_handle, new Native.Point { Tag = PointViewport }, out var topRef), "Could not locate the viewport.");
        Native.PointValue top = default;
        ThrowIfFailed(SearchNative.ghostty_terminal_point_from_grid_ref(_handle, &topRef, PointScreen, &top), "Could not locate the viewport.");
        var spans = new List<TerminalSearchSpan>();
        long first = top.Y, last = top.Y + _rows - 1;
        for (var i = 0; i < count; i++)
        {
            var match = _matchScratch[i];
            if (ToScreen(match) is not { } span || span.EndY < first || span.StartY > last) continue;
            if (SearchNeedsExactFilter && !MatchText(match, exact: true)) continue;
            var isSelected = span == selected;
            for (long y = Math.Max(first, span.StartY); y <= Math.Min(last, span.EndY); y++)
                spans.Add(new((int)(y - first), y == span.StartY ? span.StartX : 0, y == span.EndY ? span.EndX : _columns - 1, isSelected));
        }
        _highlightKey = key;
        return _highlights = new TerminalSearchHighlights(ContentVersion, native.Offset, screen != 0, [.. spans]);
    }

    private unsafe int ReadMatches(int data)
    {
        var buffer = new SearchSelectionBuffer();
        var result = SearchNative.ghostty_search_get(_search!, data, &buffer);
        if (result == NativeResult.Success) return 0;
        if (result != NativeResult.OutOfSpace) ThrowIfFailed(result, "Could not size search matches.");
        if (_matchScratch.Length < (int)buffer.Length) _matchScratch = new Native.Selection[(int)buffer.Length];
        fixed (Native.Selection* ptr = _matchScratch)
        {
            buffer = new SearchSelectionBuffer { Pointer = ptr, Capacity = (nuint)_matchScratch.Length };
            ThrowIfFailed(SearchNative.ghostty_search_get(_search!, data, &buffer), "Could not read search matches.");
        }
        return checked((int)buffer.Length);
    }

    private unsafe ScreenSpan? ToScreen(Native.Selection match)
    {
        Native.PointValue start = default, end = default;
        if (SearchNative.ghostty_terminal_point_from_grid_ref(_handle, &match.Start, PointScreen, &start) != 0 ||
            SearchNative.ghostty_terminal_point_from_grid_ref(_handle, &match.End, PointScreen, &end) != 0) return null;
        return new ScreenSpan(start.Y, start.X, end.Y, end.X);
    }

    private static Native.Point ScreenPoint(ushort x, uint y) => new() { Tag = PointScreen, Value = new Native.PointValue { X = x, Y = y } };

    /// <summary>
    /// Formats a fresh selection snapshot and compares it with the needle, exactly or folding ASCII letters only.
    /// Output longer than the needle reports the required size without being written, so the buffer never grows.
    /// </summary>
    private unsafe bool MatchText(in Native.Selection selection, bool exact)
    {
        if (_matchFormatter is null)
        {
            ThrowIfFailed(Native.ghostty_formatter_terminal_new(IntPtr.Zero, out var ptr, _handle), "Could not create search formatter.");
            _matchFormatter = new FormatterHandle(ptr);
            byte unwrap = 1, trim = 0;
            ThrowIfFailed(ScrollNative.ghostty_formatter_set(_matchFormatter, 2, &unwrap), "Could not unwrap search match.");
            ThrowIfFailed(ScrollNative.ghostty_formatter_set(_matchFormatter, 3, &trim), "Could not preserve search match whitespace.");
        }
        ThrowIfFailed(Native.ghostty_formatter_set(_matchFormatter, Native.FormatterOption.Selection, in selection), "Could not format search match.");
        nuint written = 0;
        int result;
        fixed (byte* buffer = _matchText) result = SearchNative.ghostty_formatter_format_buf(_matchFormatter, buffer, (nuint)_needle.Length, &written);
        if (result == NativeResult.OutOfSpace) return false;
        ThrowIfFailed(result, "Could not format search match.");
        if ((int)written != _needle.Length) return false;
        var text = _matchText.AsSpan(0, _needle.Length);
        if (exact) return text.SequenceEqual(_needle);
        for (var i = 0; i < text.Length; i++)
            if ((text[i] is >= (byte)'A' and <= (byte)'Z' ? text[i] | 0x20 : text[i]) != (_needle[i] is >= (byte)'A' and <= (byte)'Z' ? _needle[i] | 0x20 : _needle[i])) return false;
        return true;
    }

    private void ResetSearch()
    {
        _completedSearch = null;
        _search?.Dispose();
        _search = null; _searchQuery = null;
        _needle = []; _needleFoldsCase = false;
        _candidateCount = 0;
        _searchRevision++;
        _highlights = null;
    }

    // Terminal disposal is the only caller. The formatter borrows the terminal, so it goes before the handle.
    private void DisposeSearch()
    {
        ResetSearch();
        _matchFormatter?.Dispose();
        _matchFormatter = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SearchSelectionBuffer
    {
        public Native.Selection* Pointer;
        public nuint Capacity;
        public nuint Length;
    }
    [StructLayout(LayoutKind.Sequential)] private unsafe struct SearchString { public byte* Pointer; public nuint Length; }
    private sealed class SearchHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SearchHandle(IntPtr ptr) : base(true) => SetHandle(ptr);
        protected override bool ReleaseHandle() { SearchNative.ghostty_search_free(handle); return true; }
    }
    private static unsafe partial class SearchNative
    {
        private const string Lib = "ghostty-vt";
        [LibraryImport(Lib)] internal static partial int ghostty_search_new(IntPtr allocator, out IntPtr search, TerminalHandle terminal);
        [LibraryImport(Lib)] internal static partial void ghostty_search_free(IntPtr search);
        [LibraryImport(Lib)] internal static partial int ghostty_search_set(SearchHandle search, int option, void* value);
        [LibraryImport(Lib)] internal static partial int ghostty_search_get(SearchHandle search, int data, void* value);
        [LibraryImport(Lib)] internal static partial int ghostty_search_feed(SearchHandle search);
        [LibraryImport(Lib)] internal static partial int ghostty_search_tick(SearchHandle search, int* status);
        [LibraryImport(Lib)] internal static partial int ghostty_search_run(SearchHandle search);
        [LibraryImport(Lib)] internal static partial int ghostty_search_select_next(SearchHandle search);
        [LibraryImport(Lib)] internal static partial int ghostty_search_select_prev(SearchHandle search);
        [LibraryImport(Lib)] internal static partial int ghostty_terminal_point_from_grid_ref(TerminalHandle terminal, Native.GridRef* reference, int tag, Native.PointValue* point);
        [LibraryImport(Lib)] internal static partial int ghostty_formatter_format_buf(FormatterHandle formatter, byte* buffer, nuint length, nuint* written);
    }
}
