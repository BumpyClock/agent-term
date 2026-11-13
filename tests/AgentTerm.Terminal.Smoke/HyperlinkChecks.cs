using System.Text;
using AgentTerm.Terminal;

internal static class HyperlinkChecks
{
    public static void Run()
    {
        PolicyChecks();
        LookupChecks();
        MouseShapeChecks();
        GestureChecks();
        Console.WriteLine("Hyperlink checks passed native OSC 8 lookup, wrap, wide glyph, reflow, scroll, screen switch, URI policy, pointer shapes and click routing.");
    }

    private static void Fail(string message) => throw new InvalidOperationException("Hyperlink check failed: " + message);
    private static void Expect(bool condition, string message) { if (!condition) Fail(message); }
    private static void Expect<T>(T actual, T expected, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected)) Fail($"{message}. Expected <{expected}>, got <{actual}>.");
    }

    private static TerminalLinkTarget Policy(string uri) => TerminalLinkPolicy.Evaluate(Encoding.UTF8.GetBytes(uri));

    private static void PolicyChecks()
    {
        foreach (var uri in new[] { "https://example.com/a?b=c#d", "http://localhost:8080/x", "mailto:someone@example.com", "HTTPS://EXAMPLE.COM" })
        {
            var target = Policy(uri);
            Expect(target.Decision, TerminalLinkDecision.Allow, $"{uri} is allowed");
            Expect(target.CanOpen && target.IsCopyable && target.Text == uri, $"{uri} stays exact and copyable");
        }
        Expect(Policy("https://example.com@evil.test/").Decision, TerminalLinkDecision.Confirm, "A user name before the host needs confirmation");
        Expect(Policy("vscode://file/C:/repo/a.cs:3").Decision, TerminalLinkDecision.Confirm, "A custom scheme needs confirmation");
        Expect(Policy("ssh://host").CanOpen, "A confirmed link can still be opened after confirmation");
        Expect(Policy("file:///C:/docs/readme.txt").Decision, TerminalLinkDecision.Confirm, "A plain local file needs confirmation");
        Expect(Policy("file://localhost/C:/docs/readme.txt").Decision, TerminalLinkDecision.Block, "A file link with any host is treated as remote");

        foreach (var uri in new[]
        {
            "javascript:alert(1)", "JavaScript:alert(1)", "data:text/html;base64,AAAA", "vbscript:x", "ms-msdt:/id", "search-ms:query=x",
            "file:///C:/Windows/notepad.exe", "file:///C:/x/run.CMD", "file:///C:/x/a.ps1", "file://server/share/a.txt", "file:////server/share/a.txt",
            "file:///C:/x/a.txt.", "file:///C:/x/a.txt:stream", "C:\\Windows\\notepad.exe", "\\\\server\\share", "relative/path", "/rooted", "x:y",
            "https:///nohost", "1http://x", "http//x", ":x"
        })
        {
            var target = Policy(uri);
            Expect(target.Decision, TerminalLinkDecision.Block, $"{uri} is blocked");
            Expect(!target.CanOpen && target.Uri is null || target.Decision == TerminalLinkDecision.Block && !target.CanOpen, $"{uri} cannot be opened");
        }

        foreach (var uri in new[] { "https://a.test/\u001b[31m", "https://a.test/\u0000", "https://a.test/\u007f", "https://a.test/\u0085",
            "https://a.test/ x", "https://a.test/\n", "https://a.test/\u202e", "https://a.test/\u200b", "https://a.test/\ue000" })
        {
            var target = Policy(uri);
            Expect(target.Decision, TerminalLinkDecision.Block, "A hidden or control character blocks the link");
            Expect(!target.IsCopyable && !target.CanOpen, "A hidden or control character blocks copying too");
            Expect(target.Display.All(c => !char.IsControl(c) && c != '\u202e' && c != '\u200b'), "Display text never carries controls or invisible characters");
        }
        Expect(Policy("https://a.test/\u001b[31m").Display, "https://a.test/\u241b[31m", "An escape shows as a visible symbol");

        var invalid = TerminalLinkPolicy.Evaluate([(byte)'h', (byte)'t', (byte)'t', (byte)'p', (byte)':', (byte)'/', (byte)'/', 0xFF, 0xFE]);
        Expect(invalid.Decision, TerminalLinkDecision.Block, "Invalid UTF-8 is blocked");
        Expect(!invalid.IsCopyable && invalid.Display.Contains('\uFFFD'), "Invalid UTF-8 is not copyable and shows replacement characters");
        Expect(TerminalLinkPolicy.Evaluate([]).Decision, TerminalLinkDecision.Block, "An empty link is blocked");
        var tooLong = Policy("https://a.test/" + new string('a', TerminalLinkPolicy.MaxUriBytes));
        Expect(tooLong.Decision == TerminalLinkDecision.Block && !tooLong.IsCopyable, "An oversized link is blocked");
        Expect(Policy("https://a.test/" + new string('a', TerminalLinkPolicy.MaxUriBytes - 15)).Decision, TerminalLinkDecision.Allow, "A link at the size limit is allowed");
        Expect(Policy("https://münchen.test/ä?ü=1").Decision, TerminalLinkDecision.Allow, "Non-ASCII URIs are allowed");
        Expect(Policy("https://a.test/" + new string('é', 300)).Display.Length <= 513, "Display text is length limited");
    }

    private static GhosttyTerminal Terminal(ushort columns, ushort rows, string text)
    {
        var terminal = new GhosttyTerminal(columns, rows);
        terminal.Write(Encoding.UTF8.GetBytes(text));
        return terminal;
    }

    private static string Spans(TerminalLink? link) =>
        link is null ? "none" : string.Join(" ", link.Spans.Select(span => $"{span.Row}:{span.StartColumn}-{span.EndColumn}"));

    private static void LookupChecks()
    {
        const string Esc = "\u001b";
        using (var terminal = Terminal(40, 6, $"plain {Esc}]8;;https://a.test/x\u0007link{Esc}]8;;\u0007 after"))
        {
            Expect(terminal.ReadLink(0, 0) is null, "Text before a link has no link");
            var link = terminal.ReadLink(6, 0) ?? throw new InvalidOperationException("No link at its first cell.");
            Expect(link.Target.Text, "https://a.test/x", "The link carries its URI");
            Expect(Spans(link), "0:6-10", "The link covers exactly its text");
            Expect(Spans(terminal.ReadLink(9, 0)), "0:6-10", "The last cell resolves the same run");
            Expect(terminal.ReadLink(10, 0) is null, "OSC 8 reset ends the link");
            Expect(terminal.ReadLink(14, 0) is null, "Text after reset has no link");
            Expect(terminal.ReadLink(40, 0) is null && terminal.ReadLink(0, 6) is null && terminal.ReadLink(-1, 0) is null, "Out of range cells have no link");
            Expect(terminal.ReadLink(7, 0)!.SameDestination(link), "Cells of one link share a destination");
            terminal.Write(Encoding.UTF8.GetBytes($"{Esc}[2J{Esc}[H"));
            Expect(terminal.ReadLink(6, 0) is null, "Erasing the display removes the link");
        }

        using (var terminal = Terminal(40, 4, $"{Esc}]8;id=one;https://a.test/1{Esc}\\one{Esc}]8;;{Esc}\\{Esc}]8;;https://b.test/2\u0007two{Esc}]8;;\u0007"))
        {
            Expect(terminal.ReadLink(1, 0)!.Target.Text, "https://a.test/1", "ST terminated OSC 8 with an id parameter");
            Expect(terminal.ReadLink(3, 0)!.Target.Text, "https://b.test/2", "An adjacent link has its own destination");
            Expect(Spans(terminal.ReadLink(1, 0)), "0:0-3", "A link with another URI next to it does not run on");
            Expect(Spans(terminal.ReadLink(4, 0)), "0:3-6", "The next link has its own span");
            Expect(!terminal.ReadLink(1, 0)!.SameDestination(terminal.ReadLink(3, 0)), "Different URIs are different destinations");
        }

        // A sequence split across writes, byte by byte.
        using (var terminal = new GhosttyTerminal(40, 4))
        {
            foreach (var value in Encoding.UTF8.GetBytes($"{Esc}]8;;https://split.test/p\u0007go{Esc}]8;;\u0007"))
                terminal.Write([value]);
            Expect(terminal.ReadLink(0, 0)?.Target.Text, "https://split.test/p", "A byte-split OSC 8 sequence still links");
            Expect(Spans(terminal.ReadLink(1, 0)), "0:0-2", "A byte-split OSC 8 sequence covers its text");
        }

        // A link left open continues over later output and stops at reset.
        using (var terminal = Terminal(10, 4, $"{Esc}]8;;https://open.test/\u0007ab\r\ncd"))
        {
            terminal.Write(Encoding.UTF8.GetBytes($"{Esc}]8;;\u0007ef"));
            Expect(terminal.ReadLink(1, 1)?.Target.Text, "https://open.test/", "An open link covers the next line");
            Expect(Spans(terminal.ReadLink(0, 0)), "0:0-2", "A hard line break does not join rows");
            Expect(terminal.ReadLink(3, 1) is null, "Output after reset has no link");
        }

        // Wide glyphs. The tail half resolves to the whole glyph.
        using (var terminal = Terminal(20, 4, $"{Esc}]8;;https://w.test/\u0007你好{Esc}]8;;\u0007x"))
        {
            for (var column = 0; column < 4; column++)
                Expect(Spans(terminal.ReadLink(column, 0)), "0:0-4", $"Wide glyph cell {column} resolves the whole run");
            Expect(terminal.ReadLink(4, 0) is null, "Text after a wide link has no link");
        }
        using (var terminal = Terminal(20, 4, $"a{Esc}]8;;https://w.test/\u0007你{Esc}]8;;\u0007好"))
        {
            Expect(Spans(terminal.ReadLink(2, 0)), "0:1-3", "A tail cell resolves to its own wide glyph only");
            Expect(terminal.ReadLink(3, 0) is null, "The glyph after the link has no link");
        }
        using (var terminal = Terminal(20, 4, $"{Esc}]8;;https://e.test/\u0007e\u0301🙂👩‍👩‍👧{Esc}]8;;\u0007"))
            Expect(terminal.ReadLink(0, 0)?.Target.Text, "https://e.test/", "Combining marks and emoji clusters keep the link");

        // Soft wrap joins rows. A hard break does not.
        using (var terminal = Terminal(10, 5, $"{Esc}]8;;https://wrap.test/\u0007abcdefghijklmnopqrstuvwxy{Esc}]8;;\u0007z"))
        {
            foreach (var (column, row) in new[] { (0, 0), (9, 0), (5, 1), (4, 2) })
                Expect(Spans(terminal.ReadLink(column, row)), "0:0-10 1:0-10 2:0-5", $"Wrapped link resolves from ({column},{row})");
            Expect(terminal.ReadLink(5, 2) is null, "The wrapped link stops at reset");
        }
        using (var terminal = Terminal(5, 5, $"{Esc}]8;;https://wide.test/\u0007abcd你好{Esc}]8;;\u0007"))
        {
            Expect(terminal.ReadLink(0, 0)?.Spans.Count, 2, "A wide glyph wrapped by its spacer keeps both rows in the link");
            Expect(terminal.ReadLink(1, 1) is not null, "The wrapped wide glyph is linked");
            Expect(terminal.ReadLink(0, 1)?.Spans.Count, 2, "Hovering the wrapped row finds the earlier row");
        }

        // Reflow rejoins and splits runs. Lookups always read the current grid.
        using (var terminal = Terminal(10, 4, $"{Esc}]8;;https://flow.test/\u0007abcdefghijklmno{Esc}]8;;\u0007"))
        {
            Expect(Spans(terminal.ReadLink(2, 1)), "0:0-10 1:0-5", "Narrow terminal wraps the link");
            terminal.Resize(30, 4);
            Expect(Spans(terminal.ReadLink(2, 0)), "0:0-15", "Widening rejoins the link on one row");
            Expect(terminal.ReadLink(2, 1) is null, "The row the link left has no link");
            terminal.Resize(6, 4);
            Expect(terminal.ReadLink(0, 0)?.Spans.Count, 3, "Narrowing splits the link again");
        }

        // Scrolling moves links with their text.
        using (var terminal = Terminal(20, 4, $"{Esc}]8;;https://scroll.test/\u0007top{Esc}]8;;\u0007"))
        {
            for (var line = 0; line < 8; line++) terminal.Write(Encoding.UTF8.GetBytes($"\r\nline {line}"));
            Expect(terminal.ReadLink(1, 0) is null, "A link scrolled into history is not in the live viewport");
            terminal.ScrollTo(0);
            Expect(terminal.ReadLink(1, 0)?.Target.Text, "https://scroll.test/", "Scrolling back finds the link at its new viewport row");
            terminal.ScrollToBottom();
            Expect(terminal.ReadLink(1, 0) is null, "Returning to the bottom leaves it behind again");
        }

        // The alternate screen has its own links.
        using (var terminal = Terminal(20, 4, $"{Esc}]8;;https://main.test/\u0007main{Esc}]8;;\u0007"))
        {
            terminal.Write(Encoding.UTF8.GetBytes($"{Esc}[?1049h{Esc}[H{Esc}]8;;https://alt.test/\u0007alt{Esc}]8;;\u0007"));
            Expect(terminal.ReadLink(1, 0)?.Target.Text, "https://alt.test/", "The alternate screen shows its own link");
            terminal.Write(Encoding.UTF8.GetBytes($"{Esc}[?1049l"));
            Expect(terminal.ReadLink(1, 0)?.Target.Text, "https://main.test/", "Leaving the alternate screen restores the main link");
        }

        // Hostile targets are still reported, so the host can show why they are blocked.
        using (var terminal = Terminal(40, 4, $"{Esc}]8;;javascript:alert(1)\u0007js{Esc}]8;;\u0007 {Esc}]8;;vscode://x/y\u0007vs{Esc}]8;;\u0007"))
        {
            Expect(terminal.ReadLink(0, 0)?.Target.Decision, TerminalLinkDecision.Block, "A script link is blocked");
            Expect(terminal.ReadLink(3, 0)?.Target.Decision, TerminalLinkDecision.Confirm, "A custom scheme link needs confirmation");
        }
        using (var terminal = Terminal(40, 4, $"{Esc}]8;;https://a.test/\u007fb\u0007c{Esc}]8;;\u0007"))
        {
            var target = terminal.ReadLink(0, 0)?.Target;
            Expect(target is { Decision: TerminalLinkDecision.Block, IsCopyable: false }, "A control character that survives the parser is blocked");
            Expect(target!.Display.Contains('\u2421'), "The blocked target shows the control as a visible symbol");
        }
        // The parser drops C0 controls inside the URI itself, so a link can never smuggle an escape sequence.
        using (var terminal = Terminal(40, 4, $"{Esc}]8;;https://a.test/\u0001b\u0007c{Esc}]8;;\u0007"))
            Expect(terminal.ReadLink(0, 0)?.Target.Text, "https://a.test/b", "C0 controls inside a link target are stripped by the parser");

        // Returned links own their data. Later output cannot change them.
        using (var terminal = Terminal(20, 4, $"{Esc}]8;;https://own.test/\u0007own{Esc}]8;;\u0007"))
        {
            var link = terminal.ReadLink(0, 0)!;
            terminal.Write(Encoding.UTF8.GetBytes($"{Esc}[2J{Esc}[Hnew"));
            Expect(link.Target.Text, "https://own.test/", "A link read earlier survives later output");
            Expect(Spans(link), "0:0-3", "A link read earlier keeps its spans");
        }

        var disposed = new GhosttyTerminal(10, 2);
        disposed.Dispose();
        try { disposed.ReadLink(0, 0); Fail("Reading a link after dispose should throw."); }
        catch (ObjectDisposedException) { }
    }

    private static void MouseShapeChecks()
    {
        const string Esc = "\u001b";
        using (var terminal = new GhosttyTerminal(20, 4))
        {
            Expect(terminal.ReadMouseShape(), TerminalMouseShape.Text, "A new terminal asks for the text pointer");
            terminal.Write(Encoding.UTF8.GetBytes($"{Esc}]22;pointer{Esc}\\"));
            Expect(terminal.ReadMouseShape(), TerminalMouseShape.Pointer, "OSC 22 sets the pointer shape");
            terminal.Write(Encoding.UTF8.GetBytes($"{Esc}]22;crosshair\u0007"));
            Expect(terminal.ReadMouseShape(), TerminalMouseShape.Crosshair, "A BEL terminated OSC 22 sets the pointer shape");
            terminal.Write(Encoding.UTF8.GetBytes($"{Esc}]22;not-a-real-cursor{Esc}\\"));
            Expect(terminal.ReadMouseShape(), TerminalMouseShape.Crosshair, "An unknown name leaves the shape alone");
            terminal.Write(Encoding.UTF8.GetBytes($"{Esc}]22;{Esc}\\"));
            Expect(terminal.ReadMouseShape(), TerminalMouseShape.Text, "An empty OSC 22 restores the default shape");
            terminal.Write(Encoding.UTF8.GetBytes($"{Esc}]22;wait{Esc}\\"));
            Expect(terminal.ReadMouseShape(), TerminalMouseShape.Wait, "A later request replaces the shape");
        }

        Expect(TerminalMouseShapes.FromNative(8), TerminalMouseShape.Text, "Native text maps to text");
        Expect(TerminalMouseShapes.FromNative(99), TerminalMouseShape.Default, "A newer native shape falls back to the arrow");
        Expect(TerminalMouseShapes.FromNative(-1), TerminalMouseShape.Default, "A negative native shape falls back to the arrow");
        foreach (var shape in Enum.GetValues<TerminalMouseShape>())
            Expect(Enum.IsDefined(TerminalMouseShapes.ToPointer(shape)), $"{shape} maps to a pointer kind");
        var expected = new (TerminalMouseShape, TerminalPointerKind)[]
        {
            (TerminalMouseShape.Default, TerminalPointerKind.Arrow), (TerminalMouseShape.Text, TerminalPointerKind.IBeam),
            (TerminalMouseShape.VerticalText, TerminalPointerKind.IBeam), (TerminalMouseShape.Pointer, TerminalPointerKind.Hand),
            (TerminalMouseShape.Grab, TerminalPointerKind.Hand), (TerminalMouseShape.Progress, TerminalPointerKind.AppStarting),
            (TerminalMouseShape.Wait, TerminalPointerKind.Wait), (TerminalMouseShape.Crosshair, TerminalPointerKind.Cross),
            (TerminalMouseShape.Move, TerminalPointerKind.SizeAll), (TerminalMouseShape.ColResize, TerminalPointerKind.SizeWestEast),
            (TerminalMouseShape.NsResize, TerminalPointerKind.SizeNorthSouth), (TerminalMouseShape.NeResize, TerminalPointerKind.SizeNortheastSouthwest),
            (TerminalMouseShape.NwseResize, TerminalPointerKind.SizeNorthwestSoutheast), (TerminalMouseShape.NotAllowed, TerminalPointerKind.UniversalNo),
            (TerminalMouseShape.Help, TerminalPointerKind.Help), (TerminalMouseShape.ZoomIn, TerminalPointerKind.Arrow)
        };
        foreach (var (shape, kind) in expected)
            Expect(TerminalMouseShapes.ToPointer(shape), kind, $"{shape} pointer kind");
    }

    private static void GestureChecks()
    {
        const TerminalModifiers None = TerminalModifiers.None, Ctrl = TerminalModifiers.Control, Shift = TerminalModifiers.Shift,
            Alt = TerminalModifiers.Alt;
        var open = LinkOf("https://a.test/");
        var confirm = LinkOf("vscode://x/y");
        var blocked = LinkOf("javascript:alert(1)");

        Expect(TerminalLinkGesture.IsOpenModifier(Ctrl), "Ctrl opens links");
        foreach (var modifiers in new[] { None, Shift, Alt, Ctrl | Shift, Ctrl | Alt, Ctrl | TerminalModifiers.Super })
            Expect(!TerminalLinkGesture.IsOpenModifier(modifiers), $"{modifiers} does not open links");

        Expect(TerminalLinkGesture.ShowsHover(None, programTracksMouse: false, selecting: false), "Hover shows without tracking");
        Expect(!TerminalLinkGesture.ShowsHover(None, programTracksMouse: true, selecting: false), "A tracking program keeps plain hover");
        Expect(!TerminalLinkGesture.ShowsHover(Shift, programTracksMouse: true, selecting: false), "Shift selection override shows no link feedback while tracking");
        Expect(TerminalLinkGesture.ShowsHover(Ctrl, programTracksMouse: true, selecting: false), "Ctrl reaches a link through mouse tracking");
        Expect(!TerminalLinkGesture.ShowsHover(Ctrl, programTracksMouse: false, selecting: true), "A selection drag shows no link feedback");

        Expect(TerminalLinkGesture.ClaimsPress(Ctrl, TerminalMouseButton.Left, true), "Ctrl+left on a link is claimed");
        Expect(!TerminalLinkGesture.ClaimsPress(Ctrl, TerminalMouseButton.Left, false), "Ctrl+left off a link goes to the program");
        Expect(!TerminalLinkGesture.ClaimsPress(None, TerminalMouseButton.Left, true), "A plain press on a link selects or goes to the program");
        Expect(!TerminalLinkGesture.ClaimsPress(Ctrl, TerminalMouseButton.Right, true), "Only the left button opens");
        Expect(!TerminalLinkGesture.ClaimsPress(Ctrl, TerminalMouseButton.Middle, true), "The middle button is never claimed");
        Expect(!TerminalLinkGesture.ClaimsPress(Ctrl | Shift, TerminalMouseButton.Left, true), "Shift keeps selection");

        Expect(TerminalLinkGesture.OpensOnRelease(open, LinkOf("https://a.test/")), "Release on the same destination opens");
        Expect(!TerminalLinkGesture.OpensOnRelease(open, LinkOf("https://b.test/")), "Release on another link does not open");
        Expect(!TerminalLinkGesture.OpensOnRelease(open, null), "Release off any link does not open");

        Expect(TerminalLinkGesture.PointerFor(TerminalMouseShape.Text, open, Ctrl, false), TerminalPointerKind.Hand, "Ctrl over a link shows the hand");
        Expect(TerminalLinkGesture.PointerFor(TerminalMouseShape.Text, confirm, Ctrl, false), TerminalPointerKind.Hand, "Ctrl over a link that asks first shows the hand");
        Expect(TerminalLinkGesture.PointerFor(TerminalMouseShape.Text, blocked, Ctrl, false), TerminalPointerKind.UniversalNo, "Ctrl over a blocked link shows no");
        Expect(TerminalLinkGesture.PointerFor(TerminalMouseShape.Text, open, None, false), TerminalPointerKind.IBeam, "A link alone keeps the requested pointer");
        Expect(TerminalLinkGesture.PointerFor(TerminalMouseShape.Crosshair, open, None, false), TerminalPointerKind.Cross, "A program pointer shows without the modifier");
        Expect(TerminalLinkGesture.PointerFor(TerminalMouseShape.Text, open, Ctrl, true), TerminalPointerKind.IBeam, "A selection drag keeps the text pointer");
        Expect(TerminalLinkGesture.PointerFor(TerminalMouseShape.Pointer, null, Ctrl, false), TerminalPointerKind.Hand, "The program may ask for the hand itself");
    }

    private static TerminalLink LinkOf(string uri)
    {
        using var terminal = Terminal(20, 2, $"\u001b]8;;{uri}\u0007x\u001b]8;;\u0007");
        return terminal.ReadLink(0, 0) ?? throw new InvalidOperationException("No link for " + uri);
    }
}
