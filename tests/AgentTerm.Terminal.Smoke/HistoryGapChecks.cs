using System.Diagnostics;
using System.Reflection;
using System.Text;
using AgentTerm.Terminal;

// Search highlights, exact-case search, semantic command output, screen-versus-transcript capture, and async export.
internal static class HistoryGapChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static void Run()
    {
        Console.WriteLine("libghostty-vt " + GhosttyTerminal.NativeVersion);
        Highlights();
        SemanticOutput();
        FencedOutput();
        if (OperatingSystem.IsWindows()) SessionChecksAsync().GetAwaiter().GetResult();
        Console.WriteLine("History gap checks passed.");
    }

    private static string CellsText(TerminalFrame frame, TerminalSearchSpan span) =>
        string.Concat(Enumerable.Range(span.StartColumn, span.EndColumn - span.StartColumn + 1).Select(c => frame.Cells[span.Row * frame.Columns + c].Text));

    private static void Highlights()
    {
        using var terminal = new GhosttyTerminal(12, 4);
        terminal.Write(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 30).Select(i => i % 3 == 0 ? $"row{i:D2} FOO\r\n" : $"row{i:D2} foo\r\n")) + "tail xxxxxfo"));
        terminal.Write("o"u8);
        Check(terminal.Find("foo", false), "Fixture needs a match.");
        var frame = terminal.ReadFrame();
        var highlights = terminal.CaptureSearchHighlights()!;
        Check(highlights.ContentVersion == frame.ContentVersion && highlights.ViewportOffset == frame.Viewport.Offset, "Highlights describe the captured viewport.");
        Check(highlights.Spans.Length >= 3, $"All visible matches highlight, got {highlights.Spans.Length}.");
        foreach (var span in highlights.Spans)
            Check(CellsText(frame, span).Equals("foo", StringComparison.OrdinalIgnoreCase) || span.EndColumn == frame.Columns - 1 || span.StartColumn == 0,
                $"Span {span} must cover match text, got <{CellsText(frame, span)}>.");
        Check(highlights.Spans.Count(s => s.Selected) >= 1 && terminal.ReadSelectionText()!.Equals("foo", StringComparison.OrdinalIgnoreCase), "The native current match is marked and selected.");
        Check(ReferenceEquals(highlights, terminal.CaptureSearchHighlights()), "Unchanged state reuses the highlight snapshot.");

        terminal.ScrollTo(0);
        var top = terminal.ReadFrame();
        var scrolled = terminal.CaptureSearchHighlights()!;
        Check(scrolled.ViewportOffset == 0 && scrolled.Spans.Length > 0 && scrolled.Spans.All(s => CellsText(top, s).Equals("foo", StringComparison.OrdinalIgnoreCase)),
            "A viewport shift re-derives highlights for the new rows.");

        terminal.PrepareSearch("FOO", true);
        var exact = terminal.CaptureSearchHighlights()!;
        Check(exact.Spans.Length > 0 && exact.Spans.All(s => CellsText(top, s) == "FOO"), "Match case highlights only exact-case matches.");
        terminal.PrepareSearch("", false);
        Check(terminal.CaptureSearchHighlights() is null, "Clearing the query removes highlights.");

        // A match wrapped across a soft wrap yields one span per row. "fo" ends row 30 and "o" starts row 31.
        terminal.PrepareSearch("foo", false);
        terminal.ScrollToBottom();
        var bottom = terminal.ReadFrame();
        var wrapped = terminal.CaptureSearchHighlights()!.Spans.Where(s => s.Row >= bottom.Rows - 2).ToArray();
        Check(wrapped.Any(s => s.EndColumn == bottom.Columns - 1 && s.StartColumn == bottom.Columns - 2) && wrapped.Any(s => s.StartColumn == 0 && s.EndColumn == 0),
            "A soft-wrapped match splits into per-row spans: " + string.Join(", ", wrapped));
    }

    private static void SemanticOutput()
    {
        using var terminal = new GhosttyTerminal(30, 8);
        terminal.Write("\u001b]133;A\a$ \u001b]133;B\aecho\r\n\u001b]133;C\aout one\r\nout two\r\n\u001b]133;D;0\a\u001b]133;A\a$ "u8);
        var frame = terminal.ReadFrame();
        var outputRow = frame.Viewport.Offset + 1;
        Check(terminal.ReadCommandOutput(outputRow, 3) == "out one\nout two", "Semantic output text: <" + terminal.ReadCommandOutput(outputRow, 3) + ">.");
        Check(!terminal.HasSelection(), "Reading command output leaves the selection alone.");
        terminal.BeginSelection(0, 0); terminal.ExtendSelection(1, 0); terminal.EndSelection();
        Check(!terminal.SelectCommandOutput(frame.Viewport.Offset, 0) && terminal.ReadSelectionText() == "$", "A prompt row is not output and keeps the user selection.");
        Check(terminal.SelectCommandOutput(outputRow + 1, 0) && terminal.ReadSelectionText() == "out one\nout two", "Selecting command output installs the native output range.");
    }

    // The same bytes feed the terminal and the transcript. The screen result applies CR rewrites; the transcript keeps them.
    private static void FencedOutput()
    {
        const string started = "\u001b]133;A\aPS> \u001b]133;B\acmd\r\nCommand started [a:x]\r\n\u001b]133;C\a", ended = "Command ended [a:x]\r\n\u001b]133;D;0\a\u001b]133;A\aPS> ";
        string? Run(string beforeTrack, string body, out CommandOutputCapture capture, ushort columns = 40, Action<GhosttyTerminal>? before = null)
        {
            using var terminal = new GhosttyTerminal(columns, 6);
            var stream = new TerminalProtocolStream(terminal);
            var transcript = capture = new CommandOutputCapture("a:x");
            stream.Output += bytes => transcript.Feed(bytes);
            stream.Write(Encoding.UTF8.GetBytes(started + beforeTrack));
            using var start = terminal.TrackCommandOutputStart()!;
            stream.Write(Encoding.UTF8.GetBytes(body + ended));
            before?.Invoke(terminal);
            var screen = terminal.ReadFencedCommandOutput(start, transcript.StartFence, transcript.EndFence);
            transcript.Resolve(screen);
            return screen;
        }
        var screen = Run("", "10%\r20%\r100%\r\nline \u001b[31mred\u001b[0m  \r\n\r\nlast\r\n", out var capture);
        Check(screen == "100%\r\nline red\r\n\r\nlast\r\n", "Screen result applies CR rewrites and SGR: <" + screen + ">.");
        Check(capture.Transcript == "10%\r20%\r100%\r\nline red  \r\n\r\nlast\r\n", "Transcript stays lossless: <" + capture.Transcript + ">.");
        Check(capture.FromScreen && capture.Text == screen && !capture.Truncated, "A proven screen region replaces the transcript.");

        Check(Run("", "", out capture) == "" && capture.FromScreen && capture.Text == "", "Empty output resolves to empty text.");
        Check(Run("", "a very long output line that wraps\r\n", out capture, 12) == "a very long output line that wraps\r\n", "Narrow screens unwrap the output and match wrapped fences.");

        Check(Run("early\r\n", "late\r\n", out capture) is null && !capture.FromScreen && capture.Text == "early\r\nlate\r\n", "Output before the start marker falls back to the transcript.");
        Check(Run("", "before clear\r\n\u001b[2J\u001b[3J\u001b[Hafter\r\n", out capture) is null && capture.Text.Contains("before clear"), "A clear that removes the start fence keeps the transcript.");
        Check(Run("", "x\r\n", out capture, before: t => t.Write("\u001b[?1049h"u8)) is null, "The alternate screen cannot prove the primary region.");
        Check(Run("", string.Concat(Enumerable.Repeat("evicted\r\n", 10_100)), out capture) is null && capture.Text.StartsWith("evicted"), "Expired history keeps the transcript.");

        var limited = new CommandOutputCapture("a:x");
        limited.Resolve(new string('y', CommandOutputCapture.Limit + 10));
        Check(limited.Truncated && limited.Text.Length == CommandOutputCapture.Limit, "Screen results share the transcript limit.");
    }

    private static async Task SessionChecksAsync()
    {
        await using var session = new TerminalSession(40, 6);
        var terminal = (GhosttyTerminal)typeof(TerminalSession).GetField("_terminal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        var gate = typeof(TerminalSession).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        lock (gate) terminal.Write(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 40).Select(i => $"Needle {i:D2} needle\r\n"))));

        var first = await session.FindAsync("needle", true);
        Check(first.Found && first.SelectedOrdinal is { } a && first.Matches == 80, $"Found result reports its ordinal: {first}.");
        var next = await session.FindAsync("needle", true);
        Check(next.SelectedOrdinal == (first.SelectedOrdinal % 80) + 1, $"Enter moves to the next ordinal: {first.SelectedOrdinal} then {next.SelectedOrdinal}.");
        var exact = await session.FindAsync("Needle", false, true);
        Check(exact.Found && exact.Matches == 40 && exact.SelectedOrdinal is >= 1 and <= 40 && session.ReadSelectionText() == "Needle", $"Exact-case ordinal counts exact matches only: {exact}.");

        // Automatic refresh must not replace a selection the user made.
        session.BeginSelection(0, 0); session.ExtendSelection(3, 0); session.EndSelection();
        var userText = session.ReadSelectionText();
        lock (gate) terminal.Write("more needle output\r\n"u8);
        var refresh = await session.FindAsync("Needle", false, true, retainSelection: true);
        Check(!refresh.Found && session.ReadSelectionText() == userText && refresh.Matches == 40, $"Refresh keeps the user selection: {refresh}, <{session.ReadSelectionText()}>.");
        Check(session.ReadSearchHighlights() is { Spans.Length: > 0 }, "Highlights stay available while the user selection is kept.");

        // Reflow moves the search-installed selection; refresh still recognizes it as the search's own match.
        session.ClearSelection();
        Check((await session.FindAsync("Needle", true, true)).Found, "Fixture needs a selected match before reflow.");
        lock (gate) terminal.Resize(30, 6);
        lock (gate) terminal.Write(Encoding.UTF8.GetBytes("after reflow" + Environment.NewLine));
        var afterReflow = await session.FindAsync("Needle", true, true, retainSelection: true);
        Check(afterReflow.Found && session.ReadSelectionText() == "Needle", $"Refresh after reflow keeps the search match: {afterReflow}, <{session.ReadSelectionText()}>.");
        Check((await session.FindAsync("Needle", true, true)).Found && session.ReadSelectionText() == "Needle", "Explicit navigation re-acquires after reflow.");
        lock (gate) terminal.Resize(40, 6);

        var history = await session.ExportHistoryAsync(TerminalExportFormat.Html);
        string direct;
        lock (gate) direct = terminal.ExportHistory(TerminalExportFormat.Html)!;
        Check(history!.Text == direct, "Async export matches the formatter output.");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try { await session.ExportHistoryAsync(TerminalExportFormat.Html, cancellationToken: cancellation.Token); throw new Exception("Canceled export must not return a snapshot."); }
            catch (OperationCanceledException) { }
        }
        session.ClearSelection();
        Check(await session.ExportHistoryAsync(TerminalExportFormat.Plain, true) is null && await session.CopySelectionRichAsync() is null, "No selection exports nothing.");
        session.BeginSelection(0, 0); session.ExtendSelection(5, 0); session.EndSelection();
        var copy = await session.CopySelectionRichAsync();
        Check(copy is not null && copy.Text == session.ReadSelectionText() && copy.Html.Contains(copy.Text), "Rich copy carries HTML and text from one snapshot.");

        // Exact-case filtering cost over a large candidate set. Reported, not asserted against a fixed budget.
        await using var large = new TerminalSession(100, 30);
        var largeTerminal = (GhosttyTerminal)typeof(TerminalSession).GetField("_terminal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(large)!;
        largeTerminal.Write(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 9000).Select(i => $"Needle {i:D5} needle row\r\n"))));
        var timer = Stopwatch.StartNew();
        var largeExact = await large.FindAsync("Needle", true, true);
        Check(largeExact.Found && largeExact.Matches == 9000, $"Large exact search: {largeExact}.");
        Console.WriteLine($"Exact-case search over {largeExact.Matches * 2} candidates: {timer.ElapsedMilliseconds} ms.");

        // Frame captures feed the same native search between incremental slices. Feeding unchanged content must not
        // restart or skip history, so the interleaved total equals an undisturbed one.
        var interleavedFeeds = 0;
        var interleaved = await large.FindAsync("needle row", false, progress: new Progress(_ => { if (large.ReadSearchHighlights() is not null) interleavedFeeds++; }));
        var undisturbed = await large.FindAsync("needle ROW", false);
        Check(interleavedFeeds > 0 && interleaved.Matches == 9000 && undisturbed.Matches == 9000, $"Highlight feeds during search keep totals: {interleaved.Matches}/{undisturbed.Matches}, feeds {interleavedFeeds}.");
        await large.FindAsync("", true);
        Check(large.ReadSearchHighlights() is null, "Closing find clears highlights.");

        await session.DisposeAsync();
        try { session.ReadSearchHighlights(); throw new Exception("Disposed highlights must fail."); } catch (ObjectDisposedException) { }
    }

    private sealed class Progress(Action<TerminalSearchProgress> action) : IProgress<TerminalSearchProgress>
    {
        public void Report(TerminalSearchProgress value) => action(value);
    }
}
