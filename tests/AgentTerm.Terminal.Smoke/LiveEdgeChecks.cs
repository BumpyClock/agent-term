using AgentTerm.Terminal;
using System.Text;

internal static class LiveEdgeChecks
{
    private static void CheckDisposedGetters()
    {
        var terminal = new GhosttyTerminal(10, 3);
        terminal.ReadFrame();
        terminal.Dispose();
        foreach (var read in new Action[] { () => terminal.ReadActiveCursorRow(), () => _ = terminal.ActiveTop,
            () => terminal.ReadPresentedViewport() })
        {
            try { read(); }
            catch (ObjectDisposedException) { continue; }
            throw new InvalidOperationException("Disposed live-edge getters must reject before calling native code.");
        }
    }

    internal static void Run()
    {
        CheckDisposedGetters();
        static void Check(bool ok, string message) { if (!ok) throw new Exception("Gate 0. " + message); }
        static string Row(TerminalFrame f, int row) => string.Concat(f.Cells.Skip(row * f.Columns).Take(f.Columns).Select(c => c.Text)).TrimEnd();
        using var t = new GhosttyTerminal(20, 6);
        var legacy = t.ReadFrame();
        Check(legacy.Viewport.TotalRows == 6 && legacy.Viewport.TopInset == 0, "Legacy retains the actual grid.");
        t.SetBottomAnchored(true);
        var empty = t.ReadFrame();
        Check(empty.Viewport.TotalRows == 1 && empty.Viewport.TopInset == 5, "Empty live document occupies the cursor row.");
        Check(ReferenceEquals(empty, t.ReadFrame()), "Idle frame identity is stable.");
        t.Write("one\r\ntwo"u8);
        var shortFrame = t.ReadFrame();
        Check(shortFrame.Viewport.TopInset == 4 && Row(shortFrame, 0) == "one" && Row(shortFrame, 1) == "two", "Short content remains chronological.");
        t.Write("\r\n3\r\n4\r\n5\r\n6"u8);
        Check(t.ReadFrame().Viewport.TopInset == 0, "Full screen is unchanged.");
        for (var i = 0; i < 20; i++) t.Write(Encoding.UTF8.GetBytes("\r\nhistory-" + i));
        t.Write("\x1b[2J\x1b[Hprompt>"u8);
        var clear = t.ReadFrame();
        Check(clear.Viewport.TotalRows > 6 && clear.Viewport.IsAtBottom && clear.Cursor.Row == 5 && Row(clear, 5) == "prompt>", "Clear with history borrows history, with prompt at bottom.");
        using var anchorScope = new AnchorScope(t, t.AnchorActiveRow(t.ReadActiveCursorRow()));
        Check(t.ReadAnchor(anchorScope.Anchor) == clear.Viewport.Offset + clear.Cursor.Row, "Active cursor anchor is not the frame row.");
        t.ScrollTo(0);
        var scrolled = t.ReadFrame();
        t.Write("x"u8);
        Check(t.ReadFrame().Viewport.Offset == scrolled.Viewport.Offset, "Writing does not resume follow while scrolled away.");
        t.ScrollTo(long.MaxValue);
        Check(t.ReadFrame().Viewport.IsAtBottom, "Scroll clamps to trimmed live edge.");
        t.BeginSelection(0, 0); t.ExtendSelection(4, 0); t.EndSelection();
        var selected = t.ReadFrame();
        t.Write("\r\nnext"u8);
        Check(t.ReadFrame().Viewport.Offset == selected.Viewport.Offset, "Selection holds the viewport across output.");
        t.ClearSelection(); t.ScrollToBottom();
        foreach (var height in new ushort[] { 10, 4, 6 })
        {
            t.Resize(20, height);
            var resized = t.ReadFrame();
            Check(resized.Viewport.IsAtBottom && resized.Cursor.Row == height - 1, "Taller and shorter reflow keeps the real cursor at the edge.");
            Check(ReferenceEquals(resized, t.ReadFrame()), "Resize settles to a cached frame.");
        }
        t.Write("\x1b[?1049h\x1b[Halt"u8);
        var alternate = t.ReadFrame();
        Check(alternate.Viewport.IsAlternateScreen && alternate.Viewport.TopInset == 0 && alternate.Cursor.Row == 0, "Alternate screen remains untrimmed.");
        t.SetBottomAnchored(false); t.SetBottomAnchored(true);
        Check(t.TakeResponses().Length == 0, "Mode switching creates no PTY traffic.");
        t.Write("\x1b[?1049l"u8);
        Check(t.ReadFrame().Viewport.IsAtBottom, "Primary presentation resumes after alternate screen.");
        t.SetBottomAnchored(false);
        Check(t.ReadFrame().Viewport.TopInset == 0, "Legacy restores the full document.");
        using var menu = new GhosttyTerminal(20, 6);
        menu.SetBottomAnchored(true);
        menu.Write("prompt>\r\nmenu\x1b[H"u8);
        Check(menu.ReadFrame().Viewport.TotalRows == 2, "Menu occupies a real row.");
        menu.Write("\x1b[2;1H\x1b[J\x1b[H"u8);
        Check(menu.ReadFrame().Viewport.TotalRows == 1, "Erased menu tail is trimmed.");
        var stable = menu.ReadFrame();
        menu.Write("\x1b[?2026hheld"u8);
        Check(ReferenceEquals(stable, menu.ReadFrame()), "Synchronized output holds the published frame.");
        menu.Write("\x1b[?2026l"u8);
        Check(!ReferenceEquals(stable, menu.ReadFrame()), "Synchronized output publishes on release.");
        menu.Write("\x1b[6;1H\x1b[41m \x1b[0m\x1b[H"u8);
        Check(menu.ReadFrame().Viewport.TotalRows == 6, "Styled blank backgrounds are content.");
    }
    private sealed class AnchorScope(GhosttyTerminal terminal, TerminalAnchor anchor) : IDisposable
    {
        public TerminalAnchor Anchor => anchor;
        public void Dispose() => terminal.ReleaseAnchor(anchor);
    }
}
