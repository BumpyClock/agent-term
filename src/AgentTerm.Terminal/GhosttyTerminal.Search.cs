using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentTerm.Terminal;

public sealed partial class GhosttyTerminal
{
    private SearchHandle? _search;
    private string? _searchQuery;
    private bool _searchCaseSensitive;
    private Native.Selection[]? _searchCandidates;

    /// <summary>Finds the next match in screen order and selects it. Search wraps at either end.</summary>
    public unsafe bool Find(string query, bool forward, bool caseSensitive = false)
    {
        ArgumentNullException.ThrowIfNull(query);
        PrepareSearch(query, caseSensitive);
        if (query.Length == 0) return false;
        ThrowIfFailed(SearchNative.ghostty_search_run(_search!), "Could not search terminal history.");
        return SelectSearchMatch(forward, caseSensitive, query);
    }

    internal unsafe void PrepareSearch(string query, bool caseSensitive)
    {
        ThrowIfDisposed();
        if (_search is not null && _searchQuery == query && _searchCaseSensitive == caseSensitive) return;
        _searchCandidates = null;
        DisposeSearch(); _search = null; _searchQuery = null;
        if (query.Length == 0) return;
        ThrowIfFailed(SearchNative.ghostty_search_new(IntPtr.Zero, out var ptr, _handle), "Could not create search.");
        _search = new SearchHandle(ptr);
        var bytes = Encoding.UTF8.GetBytes(query);
        fixed (byte* p = bytes)
        {
            var needle = new SearchString { Pointer = p, Length = (nuint)bytes.Length };
            ThrowIfFailed(SearchNative.ghostty_search_set(_search, 1, &needle), "Could not set search text.");
        }
        _searchQuery = query; _searchCaseSensitive = caseSensitive;
    }
    internal void FeedSearch()
    {
        _searchCandidates = null;
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
        ThrowIfFailed(SearchNative.ghostty_search_get(_search!, 3, &count), "Could not read search count.");
        return checked((long)count);
    }
    // The session checks the output/reflow revision before formatting each snapshot.
    // These references are never published to the app and are replaced after every feed.
    internal unsafe void CaptureSearchCandidates()
    {
        _searchCandidates = new Native.Selection[checked((int)SearchCount())];
        fixed (Native.Selection* ptr = _searchCandidates)
        {
            var buffer = new SearchSelectionBuffer { Pointer = ptr, Capacity = (nuint)_searchCandidates.Length };
            ThrowIfFailed(SearchNative.ghostty_search_get(_search!, 6, &buffer), "Could not read search candidates.");
        }
    }
    internal bool ExactSearchCandidate(int index, string query) =>
        string.Equals(FormatSearchMatch(_searchCandidates![index]), query, StringComparison.Ordinal);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SearchSelectionBuffer
    {
        public Native.Selection* Pointer;
        public nuint Capacity;
        public nuint Length;
    }
    internal unsafe bool SelectSearchMatch(bool forward, bool caseSensitive, string query, long maxCandidates = long.MaxValue)
    {
        var originalOffset = ReadViewport().Offset;
        nuint count = 0;
        ThrowIfFailed(SearchNative.ghostty_search_get(_search!, 3, &count), "Could not read terminal search count.");
        for (nuint i = 0; i < count && i < (nuint)maxCandidates; i++)
        {
            // Native next goes toward older rows; screen-forward goes toward newer rows.
            var result = forward ? SearchNative.ghostty_search_select_prev(_search!) : SearchNative.ghostty_search_select_next(_search!);
            if (result == -4) break;
            ThrowIfFailed(result, "Could not select terminal search match.");
            Native.Selection match = default;
            ThrowIfFailed(SearchNative.ghostty_search_get(_search!, 5, &match), "Could not read terminal search match.");
            if (caseSensitive && !string.Equals(FormatSearchMatch(match), query, StringComparison.Ordinal)) continue;
            ThrowIfFailed(Native.ghostty_terminal_set(_handle, 22, (IntPtr)(&match)), "Could not highlight terminal search match.");
            if (_gesture is not null) ScrollNative.ghostty_selection_gesture_reset(_gesture, _handle);
            _anchor?.Dispose(); _anchor = null;
            _selectionScreen = ActiveScreen();
            _selectionRectangle = false;
            _selecting = false;
            _viewRevision++;
            CaptureSelectedViewport();
            return true;
        }
        // Candidate filtering can move the viewport even when no exact match exists.
        if (ReadViewport().Offset != originalOffset) ScrollTo(originalOffset);
        return false;
    }

    private unsafe string FormatSearchMatch(Native.Selection selection)
    {
        ThrowIfFailed(Native.ghostty_formatter_terminal_new(IntPtr.Zero, out var ptr, _handle), "Could not create search formatter.");
        using var formatter = new FormatterHandle(ptr);
        ThrowIfFailed(Native.ghostty_formatter_set(formatter, Native.FormatterOption.Selection, in selection), "Could not format search match.");
        byte unwrap = 1, trim = 0;
        ThrowIfFailed(ScrollNative.ghostty_formatter_set(formatter, 2, &unwrap), "Could not unwrap search match.");
        ThrowIfFailed(ScrollNative.ghostty_formatter_set(formatter, 3, &trim), "Could not preserve search match whitespace.");
        ThrowIfFailed(Native.ghostty_formatter_format_alloc(formatter, IntPtr.Zero, out var output, out var length), "Could not format search match.");
        try { return length == 0 ? string.Empty : Marshal.PtrToStringUTF8(output, checked((int)length))!; }
        finally { Native.ghostty_free(IntPtr.Zero, output, length); }
    }

    private void DisposeSearch()
    {
        _searchCandidates = null;
        _search?.Dispose();
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
    }
}
