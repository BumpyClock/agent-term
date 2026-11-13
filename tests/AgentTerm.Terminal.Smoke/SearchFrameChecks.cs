using AgentTerm.Terminal;

internal static class SearchFrameChecks
{
    internal static void Run()
    {
        using var terminal = new GhosttyTerminal(20, 4);
        terminal.Write("one one\r\nother"u8);
        if (!terminal.Find("one", true)) throw new Exception("Search fixture has no match.");
        var capture = terminal.BeginFrame();
        var captured = capture.SearchHighlights ?? throw new Exception("Search spans were not captured with the frame.");
        if (captured.Spans.Length != 2 || !captured.Spans.Any(span => span.Selected))
            throw new Exception("Both viewport matches and the current match must be captured.");
        terminal.Write("\x1b[1;1Hxxx"u8);
        var frame = terminal.CompleteFrame(capture);
        if (!ReferenceEquals(frame.SearchHighlights, captured) || frame.ContentVersion != captured.ContentVersion)
            throw new Exception("Split capture mixed live highlights with an older frame.");
        terminal.ReadFrame();
        terminal.Write("\x1b[?2026h\x1b[2;1Hone"u8);
        var held = terminal.ReadFrame();
        if (held.SearchHighlights is not null) throw new Exception("A native write callback must not run search feed.");
        terminal.Write("\x1b[?2026l"u8);
        if (terminal.ReadFrame().SearchHighlights is null) throw new Exception("Ordinary capture must restore highlights after a hold.");
        terminal.Find("", true);
        if (terminal.ReadFrame().SearchHighlights is not null) throw new Exception("Clearing search left highlights behind.");
        if (frame.SearchHighlights.Spans.Length != 2) throw new Exception("A retained frame's search spans changed.");
        DenseSelectionReuse();
    }

    private static void DenseSelectionReuse()
    {
        using var terminal = new GhosttyTerminal(80, 4);
        terminal.Write(System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", Enumerable.Repeat(string.Concat(Enumerable.Repeat("aA ", 26)), 4))));
        if (!terminal.Find("a", true, caseSensitive: true)) throw new Exception("Dense fixture needs exact matches.");
        var first = terminal.ReadFrame().SearchHighlights!;
        if (first.Spans.Length != 104 || first.Spans.Count(s => s.Selected) != 1) throw new Exception("Exact highlights must include only lowercase matches.");
        var feeds = terminal.HighlightFeeds;
        var checks = terminal.HighlightExactChecks;
        if (feeds != 1 || checks != 208) throw new Exception($"Dense fixture expected one feed and 208 exact checks, got {feeds} and {checks}.");
        if (!terminal.Find("a", true, caseSensitive: true)) throw new Exception("Search navigation failed.");
        var next = terminal.ReadFrame().SearchHighlights!;
        if (next.Spans.Length != 104 || next.Spans.Count(s => s.Selected) != 1 ||
            first.Spans.Single(s => s.Selected) == next.Spans.Single(s => s.Selected))
            throw new Exception("Search navigation must move the selected highlight.");
        if (terminal.HighlightFeeds != feeds || terminal.HighlightExactChecks != checks)
            throw new Exception("Selection-only capture repeated search preparation.");
        if (first.Spans.Count(s => s.Selected) != 1) throw new Exception("Navigation mutated retained highlights.");
        terminal.Write("\x1b[1;1HX"u8);
        var changed = terminal.ReadFrame().SearchHighlights!;
        if (changed.Spans.Length != 103 || terminal.HighlightFeeds != feeds + 1)
            throw new Exception("Content changes must refresh exact highlights.");
        if (!terminal.Find("A", true, caseSensitive: true)) throw new Exception("Changed query needs matches.");
        var query = terminal.ReadFrame().SearchHighlights!;
        if (query.Spans.Length != 104 || terminal.HighlightFeeds != feeds + 2)
            throw new Exception("Query changes must refresh exact highlights.");
    }
}
