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
    }
}
