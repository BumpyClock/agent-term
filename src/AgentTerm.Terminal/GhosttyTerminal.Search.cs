using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentTerm.Terminal;

public sealed partial class GhosttyTerminal
{
    private SearchHandle? _search;
    private string? _searchQuery;
    private bool _searchCaseSensitive;

    /// <summary>Finds the next match in screen order and selects it. Search wraps at either end.</summary>
    public unsafe bool Find(string query, bool forward, bool caseSensitive = false)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length == 0)
        {
            _search?.Dispose(); _search = null; _searchQuery = null;
            return false;
        }
        if (_search is null || _searchQuery != query || _searchCaseSensitive != caseSensitive)
        {
            _search?.Dispose(); _search = null; _searchQuery = null;
            ThrowIfFailed(SearchNative.ghostty_search_new(IntPtr.Zero, out var ptr, _handle), "Could not create terminal search.");
            _search = new SearchHandle(ptr);
            var bytes = Encoding.UTF8.GetBytes(query);
            fixed (byte* p = bytes)
            {
                var needle = new SearchString { Pointer = p, Length = (nuint)bytes.Length };
                ThrowIfFailed(SearchNative.ghostty_search_set(_search, 1, &needle), "Could not set terminal search text.");
            }
            _searchQuery = query;
            _searchCaseSensitive = caseSensitive;
        }
        var originalOffset = ReadViewport().Offset;
        ThrowIfFailed(SearchNative.ghostty_search_run(_search), "Could not search terminal history.");
        nuint count = 0;
        ThrowIfFailed(SearchNative.ghostty_search_get(_search, 3, &count), "Could not read terminal search count.");
        for (nuint i = 0; i < count; i++)
        {
            // Native next goes toward older rows; screen-forward goes toward newer rows.
            var result = forward ? SearchNative.ghostty_search_select_prev(_search) : SearchNative.ghostty_search_select_next(_search);
            if (result == -4) break;
            ThrowIfFailed(result, "Could not select terminal search match.");
            Native.Selection match = default;
            ThrowIfFailed(SearchNative.ghostty_search_get(_search, 5, &match), "Could not read terminal search match.");
            if (caseSensitive && !string.Equals(FormatSearchMatch(match), query, StringComparison.Ordinal)) continue;
            ThrowIfFailed(Native.ghostty_terminal_set(_handle, 22, (IntPtr)(&match)), "Could not highlight terminal search match.");
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

    private void DisposeSearch() => _search?.Dispose();
    [StructLayout(LayoutKind.Sequential)] private unsafe struct SearchString { public byte* Pointer; public nuint Length; }
    private sealed class SearchHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SearchHandle(IntPtr ptr) : base(true) => SetHandle(ptr);
        protected override bool ReleaseHandle() { SearchNative.ghostty_search_free(handle); return true; }
    }
    private static unsafe class SearchNative
    {
        private const string Lib = "ghostty-vt";
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_search_new(IntPtr allocator, out IntPtr search, TerminalHandle terminal);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void ghostty_search_free(IntPtr search);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_search_set(SearchHandle search, int option, void* value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_search_get(SearchHandle search, int data, void* value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_search_run(SearchHandle search);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_search_select_next(SearchHandle search);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_search_select_prev(SearchHandle search);
    }
}
