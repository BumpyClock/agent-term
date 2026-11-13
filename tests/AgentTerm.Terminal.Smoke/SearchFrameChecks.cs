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
        PreparationSlices();
        if (OperatingSystem.IsWindows()) SessionPreparationAsync().GetAwaiter().GetResult();
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
    private static void PreparationSlices()
    {
        using var terminal = new GhosttyTerminal(80, 4);
        terminal.Write(System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", Enumerable.Repeat(string.Concat(Enumerable.Repeat("aA ", 26)), 4))));
        terminal.Find("a", true, true);
        var feeds = terminal.HighlightFeeds;
        var checks = terminal.HighlightExactChecks;
        var empty = terminal.BeginFrameWithPreparedSearch();
        if (empty.SearchHighlights is not null || terminal.HighlightFeeds != feeds || terminal.HighlightExactChecks != checks)
            throw new Exception("Live capture must not feed or filter search.");
        terminal.CompleteFrame(empty);
        if (terminal.PrepareSearchHighlightsSlice(TimeSpan.Zero)) throw new Exception("Dense preparation must yield.");
        var feedsDuringPreparation = terminal.HighlightFeeds;
        terminal.CaptureSearchCandidates();
        terminal.PrepareSearchHighlightsSlice(TimeSpan.Zero);
        if (terminal.HighlightFeeds != feedsDuringPreparation)
            throw new Exception("History candidate reads must not restart owned highlight preparation.");
        terminal.Write("\x1b[1;1HX"u8);
        if (terminal.ReadPreparedSearchHighlights() is not null) throw new Exception("A changed content version must reject preparation.");
        while (!terminal.PrepareSearchHighlightsSlice(TimeSpan.Zero)) { }
        var first = terminal.CompleteFrame(terminal.BeginFrameWithPreparedSearch());
        if (first.SearchHighlights is not { Spans.Length: 103 } || first.ContentVersion != first.SearchHighlights.ContentVersion)
            throw new Exception("Prepared exact highlights must match current content.");
        terminal.Find("A", true, true);
        if (terminal.ReadPreparedSearchHighlights() is not null) throw new Exception("A changed query must reject preparation.");
        while (!terminal.PrepareSearchHighlightsSlice(TimeSpan.Zero)) { }
        var second = terminal.CompleteFrame(terminal.BeginFrameWithPreparedSearch());
        if (second.SearchHighlights is not { Spans.Length: 104 } || first.SearchHighlights.Spans.Length != 103)
            throw new Exception("Query replacement must publish owned highlights without changing retained frames.");
        var feedsBeforeNavigation = terminal.HighlightFeeds;
        terminal.Find("A", true, true);
        if (terminal.ReadPreparedSearchHighlights() is not null) throw new Exception("Navigation must invalidate the selected marker.");
        while (!terminal.PrepareSearchHighlightsSlice(TimeSpan.Zero)) { }
        var navigated = terminal.ReadPreparedSearchHighlights()!;
        if (navigated.Spans.Count(span => span.Selected) != 1 || terminal.HighlightFeeds != feedsBeforeNavigation)
            throw new Exception("Navigation must update the selected marker without repeating the feed.");
        terminal.Resize(40, 4);
        if (terminal.ReadPreparedSearchHighlights() is not null) throw new Exception("Resize must invalidate the prepared viewport.");
        terminal.Write("\x1b[?1049hA A"u8);
        if (terminal.ReadPreparedSearchHighlights() is not null) throw new Exception("A screen switch must reject old highlights.");
        while (!terminal.PrepareSearchHighlightsSlice(TimeSpan.Zero)) { }
        if (terminal.ReadPreparedSearchHighlights() is not { AlternateScreen: true })
            throw new Exception("Prepared highlights must describe the active screen.");
        terminal.Write("\x1b[?1049l"u8);
        if (terminal.ReadPreparedSearchHighlights() is not null) throw new Exception("Returning to the primary screen must reject alternate highlights.");
        terminal.PrepareSearchHighlightsSlice(TimeSpan.Zero);
        terminal.Find("", true);
        if (terminal.ReadPreparedSearchHighlights() is not null || !terminal.PrepareSearchHighlightsSlice(TimeSpan.Zero))
            throw new Exception("Clearing search must cancel pending highlights.");
    }

    private static async Task SessionPreparationAsync()
    {
        await using var session = new TerminalSession(80, 4);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var terminal = (GhosttyTerminal)typeof(TerminalSession).GetField("_terminal", flags)!.GetValue(session)!;
        var gate = typeof(TerminalSession).GetField("_gate", flags)!.GetValue(session)!;
        lock (gate) terminal.Write("one ONE one"u8);
        await session.FindAsync("one", true, true);
        var redraw = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OutputAvailable += () => redraw.TrySetResult();
        lock (gate)
        {
            var feeds = terminal.HighlightFeeds;
            var frame = session.ReadFrame();
            if (frame.SearchHighlights is not null || terminal.HighlightFeeds != feeds)
                throw new Exception("Session capture must return before search preparation.");
            terminal.PrepareSearch("ONE", true);
        }
        await redraw.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var updated = (await session.CaptureFrameAsync()).Frame;
        if (updated.SearchHighlights is not { Spans.Length: 1 } || updated.ContentVersion != updated.SearchHighlights.ContentVersion)
            throw new Exception("Replacement query must publish the current highlights and request redraw.");
        var owner = (SemaphoreSlim)typeof(TerminalSession).GetField("_searchOwner", flags)!.GetValue(session)!;
        await owner.WaitAsync();
        try
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.OutputAvailable += () => ready.TrySetResult();
            lock (gate) { terminal.PrepareSearch("one", true); session.ReadFrame(); }
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (session.ReadFrame().SearchHighlights is not { Spans.Length: 2 })
                throw new Exception("Highlight preparation must proceed while history search owns its native search.");
        }
        finally { owner.Release(); }
    }

}
