using System.Text;
using AgentTerm.Terminal;

/// <summary>Acceptance checks for native-boundary protocol ordering, filtering and capture.</summary>
internal static class ProtocolStreamChecks
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    // One ordered log of everything the stream reports, so split invariance covers event order, not just totals.
    private sealed class Probe : IDisposable
    {
        public readonly GhosttyTerminal Terminal = new(40, 6);
        public readonly TerminalProtocolStream Stream;
        public readonly List<string> Log = [];
        public readonly CommandOutputCapture Plain = new();
        public readonly CommandOutputCapture Fenced = new("x");
        public readonly ShellPromptProtocol Prompt = new("tok");
        private readonly MemoryStream _output = new();
        public byte[] Output => _output.ToArray();

        public Probe(string prefix = "", int limit = 1_000_000)
        {
            Stream = new(Terminal, prefix, limit);
            Stream.Output += bytes => { _output.Write(bytes); Plain.Feed(bytes); Fenced.Feed(bytes); };
            Stream.ControlStringStarted += () => { Plain.EndEscape(); Fenced.EndEscape(); };
            Stream.Sequence += (text, length) => { Log.Add("seq " + text + ":" + length); Prompt.Accept(text, length); };
            Stream.ProtocolEvent += item => Log.Add(item.ToString());
            Prompt.Marker += message => { Log.Add("marker " + message); Plain.Marker(message); };
        }

        public Probe Feed(byte[] data, IEnumerable<int> cuts)
        {
            var start = 0;
            foreach (var cut in cuts.Append(data.Length))
            {
                var end = Math.Clamp(cut, start, data.Length);
                Stream.Write(data.AsSpan(start, end - start));
                start = end;
            }
            return this;
        }

        public void Dispose() => Terminal.Dispose();
    }

    private static Probe Run(string wire, IEnumerable<int> cuts, string prefix = "") =>
        new Probe(prefix).Feed(Encoding.UTF8.GetBytes(wire), cuts);

    private static Probe Run(byte[] wire, IEnumerable<int> cuts, string prefix = "") => new Probe(prefix).Feed(wire, cuts);

    // Every single cut and a bytewise feed must report exactly what one whole write reports.
    private static Probe SplitInvariant(byte[] wire, string label, string prefix = "")
    {
        var whole = Run(wire, [], prefix);
        foreach (var cuts in Enumerable.Range(0, wire.Length + 1).Select(cut => new[] { cut }).Append(Enumerable.Range(1, wire.Length).ToArray()))
        {
            using var split = Run(wire, cuts, prefix);
            Check(whole.Log.SequenceEqual(split.Log), $"{label}: events must not depend on chunking (cuts {string.Join(",", cuts.Take(4))}). " +
                $"Whole [{string.Join(" | ", whole.Log)}] split [{string.Join(" | ", split.Log)}]");
            Check(whole.Output.SequenceEqual(split.Output) && whole.Plain.Text == split.Plain.Text && whole.Fenced.Text == split.Fenced.Text &&
                whole.Plain.Ended == split.Plain.Ended && whole.Fenced.Ended == split.Fenced.Ended,
                $"{label}: output and capture must not depend on chunking (cuts {string.Join(",", cuts.Take(4))}).");
            Check(whole.Terminal.ReadText() == split.Terminal.ReadText() && whole.Terminal.ReadTitle() == split.Terminal.ReadTitle(),
                $"{label}: terminal state must not depend on chunking.");
        }
        return whole;
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static byte[] Join(params object[] parts) => parts.SelectMany(part => part switch
    {
        string text => Bytes(text), byte value => [value], byte[] bytes => bytes, _ => throw new ArgumentException("part")
    }).ToArray();

    public static void Run()
    {
        // Authenticated private markers: only the exact token prefix is buffered and reported.
        using (var probe = SplitInvariant(Bytes("\u001b]777;other;ready\a\u001b]777;tok;ready\a\u001b]777;tok\a\u001b]777;tokx;ready\u001b\\\u001b]777;tok;start\u001b\\"), "authentication", "777;tok;"))
        {
            Check(probe.Log.SequenceEqual(["seq 777;tok;ready:16", "marker ready", "seq 777;tok;start:17", "marker start"]),
                "Only token-prefixed markers may dispatch: " + string.Join(" | ", probe.Log));
            Check(probe.Prompt.State == ShellPromptState.Executing, "Authenticated markers drive prompt state.");
        }

        // OSC 133 and 7501 arrive as copied native events, in order with private markers.
        using (var probe = SplitInvariant(Bytes("\u001b]133;A\a$ \u001b]133;B\a\u001b]133;C;cmdline_url=echo%20hi\a\u001b]777;tok;start\ahi\r\n" +
            "\u001b]7501;state=blocked:kind=permission:app=cargo:progress=30:msg=QXBwbHk/\u001b\\\u001b]133;D;7\a\u001b]133;A;k=s\a"), "semantic", "777;tok;"))
        {
            var semantic = probe.Log.Where(line => line.StartsWith("TerminalSemanticPrompt", StringComparison.Ordinal)).ToArray();
            Check(semantic.Length == 5 && semantic[0].Contains("Step = PromptStart") && semantic[1].Contains("InputStart") &&
                semantic[2].Contains("OutputStart") && semantic[2].Contains("Command = echo hi") && semantic[3].Contains("CommandEnd") &&
                semantic[3].Contains("ExitCode = 7") && semantic[4].Contains("PromptKind = Secondary"), "OSC 133 steps: " + string.Join(" | ", semantic));
            var status = probe.Log.Single(line => line.StartsWith("TerminalProgramStatusReport", StringComparison.Ordinal));
            Check(status.Contains("State = Blocked") && status.Contains("Kind = permission") && status.Contains("Progress = 30") &&
                status.Contains("App = cargo") && status.Contains("Message = Apply?"), "OSC 7501 report: " + status);
            Check(probe.Log.IndexOf("marker start") > probe.Log.FindIndex(line => line.Contains("OutputStart")) &&
                probe.Log.IndexOf("marker start") < probe.Log.IndexOf(status), "Private markers and native events keep stream order.");
        }

        // Title set before and after a prompt marker in one chunk: both titles and the marker keep their positions.
        using (var probe = SplitInvariant(Bytes("\u001b]2;before\a\u001b]777;tok;ready\u001b\\\u001b]0;after é\u001b\\"), "title order", "777;tok;"))
        {
            Check(probe.Log.SequenceEqual(["TerminalTitleChanged { Title = before }", "seq 777;tok;ready:17", "marker ready", "TerminalTitleChanged { Title = after é }"]),
                "Title and prompt order: " + string.Join(" | ", probe.Log));
        }

        // Full reset reports cleared program status before the reset, and later titles after it.
        using (var probe = SplitInvariant(Bytes("\u001b]7501;state=working\a\u001b]2;old\aX\u001bcY\u001b]2;new\a\u001b]7;file://host/C:/work\a"), "reset order"))
        {
            var events = probe.Log.Where(line => !line.StartsWith("seq", StringComparison.Ordinal)).ToArray();
            Check(events.Length == 6 && events[0].Contains("Working") && events[1] == "TerminalTitleChanged { Title = old }" &&
                events[2].Contains("State = Clear") && events[2].Contains("Id = ,") && events[3] == "TerminalReset { }" &&
                events[4] == "TerminalTitleChanged { Title = new }" && events[5] == "TerminalWorkingDirectoryChanged { Report = file://host/C:/work }",
                "Reset ordering: " + string.Join(" | ", events));
            Check(Encoding.UTF8.GetString(probe.Output) == "X\u001bcY", "Reset bytes stay ordinary output for transcript filtering.");
        }

        // CAN and SUB cancel strings: no marker, no native effect, and following text is output.
        using (var probe = SplitInvariant(Bytes("\u001b]777;tok;ready\u0018A\u001b]2;gone\u001aB\u001b]7501;state=working\u0018C\u001bPq#0\u0018D\u001b_G\u001aE"), "cancel", "777;tok;"))
        {
            Check(probe.Log.Count == 0, "Cancelled strings must not dispatch: " + string.Join(" | ", probe.Log));
            Check(Encoding.UTF8.GetString(probe.Output) == "ABCDE", "Text after cancelled strings is output: " + Encoding.UTF8.GetString(probe.Output));
        }

        // An OSC ended by ESC without a backslash is not a properly terminated private marker.
        using (var probe = SplitInvariant(Bytes("\u001b]777;tok;ready\u001bZ\u001b]777;tok;start\u001b\u001b\\"), "improper ST", "777;tok;"))
            Check(probe.Log.Count == 0, "ESC without backslash must not dispatch a marker: " + string.Join(" | ", probe.Log));

        // C1 introducers start strings only inside another escape sequence; at ground they are UTF-8 data.
        using (var probe = SplitInvariant(Join("\u001b]777;tok;start\a", "a", (byte)0x9D, "2;ground\a", "b\u001b", (byte)0x9D, "2;c1\a", "c\u001b_apc", (byte)0x9D, "0;abort\a",
            "d\u001b_x", (byte)0x9C, "e\u001bP1;2", (byte)0x9C, "f\u001b[3", (byte)0x9D, "2;csi\aZg"), "c1"))
        {
            var titles = probe.Log.Where(line => line.StartsWith("TerminalTitle", StringComparison.Ordinal)).ToArray();
            Check(titles.SequenceEqual(["TerminalTitleChanged { Title = c1 }", "TerminalTitleChanged { Title = abort }", "TerminalTitleChanged { Title = csi }"]),
                "C1 OSC handling: " + string.Join(" | ", titles));
            Check(probe.Output.SequenceEqual(Join("a", (byte)0x9D, "2;ground\abcdef\u001b[3Zg")), "C1 output filtering: " + Encoding.Latin1.GetString(probe.Output));
            Check(probe.Plain.Text == "a�2;ground\abcdefZg", "A C1 string start ends the open CSI in capture: " + probe.Plain.Text);
        }

        // DEL and C0 controls inside an escape do not end it, so the following ] still introduces an OSC.
        using (var probe = SplitInvariant(Bytes("a\u001b\u007f]2;del\ab\u001b\n]2;c0\ac"), "escape controls"))
        {
            Check(probe.Log.Where(line => line.StartsWith("TerminalTitle", StringComparison.Ordinal))
                .SequenceEqual(["TerminalTitleChanged { Title = del }", "TerminalTitleChanged { Title = c0 }"]), "Escape controls: " + string.Join(" | ", probe.Log));
            Check(Encoding.UTF8.GetString(probe.Output) == "a\u001b\u007fb\u001b\nc", "Executed controls stay output: " + Encoding.UTF8.GetString(probe.Output));
        }

        // APC, DCS, SOS and PM content never reaches transcript output, including binary-looking payloads.
        using (var probe = SplitInvariant(Bytes("\u001b]777;tok;start\u001b\\one\u001b_Ga=T,f=100;AAAA\u001b\\two\u001bP1;2|sixel\u001b\\three\u001bXsos\u001b\\" +
            "\u001b^pm\u001b\\four\u001b[31m red\u001b[0m\u001b]777;tok;end;1;0\a"), "strings", "777;tok;"))
        {
            Check(probe.Plain.Ended && probe.Plain.Text == "onetwothreefour red", "Control strings and SGR are filtered: " + probe.Plain.Text);
        }

        // Command start and end markers in one chunk, with fragmented Unicode between them.
        var unicode = "\u001b]777;tok;start\a" + "终端 🦉 e\u0301 👩‍👩‍👧" + "\u001b]777;tok;end;1;0\aprompt";
        using (var probe = SplitInvariant(Bytes(unicode), "same chunk unicode", "777;tok;"))
            Check(probe.Plain.Ended && probe.Plain.Text == "终端 🦉 e\u0301 👩‍👩‍👧", "Same-chunk capture keeps complete graphemes: " + probe.Plain.Text);

        // Fenced capture with the start fence, end fence and reordered OSC markers inside one chunk.
        using (var probe = SplitInvariant(Bytes("echo\r\nCommand started [x]\r\nfirst\r\n\u001b]777;tok;end;1;0;x\a\u001b]777;tok;ready;x\alast\r\nCommand ended [x]\r\nPS> "), "fenced", "777;tok;"))
            Check(probe.Fenced.Ended && probe.Fenced.Text == "first\r\nlast\r\n", "Fenced capture: " + probe.Fenced.Text);

        // Truncation keeps the 64,000-character cap and never splits a surrogate pair.
        var big = Bytes("\u001b]777;tok;start\a" + string.Concat(Enumerable.Repeat("x🦉", 40_000)) + "\u001b]777;tok;end;1;0\a");
        using (var probe = Run(big, [5, 33_333, 33_334, 70_001], "777;tok;"))
        {
            Check(probe.Plain.Truncated && probe.Plain.Ended && probe.Plain.Text.Length <= 64_000 && !char.IsHighSurrogate(probe.Plain.Text[^1]),
                "Truncated capture must stay capped without a split surrogate.");
        }

        // Retention limit: content longer than the limit never dispatches, content at the limit does.
        using (var probe = new Probe("777;tok;", 30).Feed(Bytes("\u001b]777;tok;" + new string('a', 22) + "\a\u001b]777;tok;" + new string('b', 23) + "\a"), [12]))
            Check(probe.Log.Count(line => line.StartsWith("seq", StringComparison.Ordinal)) == 1 && probe.Log[0] == "seq 777;tok;" + new string('a', 22) + ":33",
                "Retention limit: " + string.Join(" | ", probe.Log));

        // OSC 7501 support queries are answered natively, mirroring the query terminator.
        using (var probe = Run("\u001b]7501;?\u001b\\\u001b]7501;?\a", []))
        {
            var replies = probe.Terminal.TakeResponses().Select(Encoding.UTF8.GetString).ToArray();
            Check(replies.SequenceEqual(["\u001b]7501;?:states=idle,working,done,blocked,error:kinds=permission,question,auth\u001b\\",
                "\u001b]7501;?:states=idle,working,done,blocked,error:kinds=permission,question,auth\a"]), "Support replies: " + string.Join(" | ", replies));
            Check(probe.Log.All(line => line.StartsWith("seq 7501;?", StringComparison.Ordinal)), "Support queries are not reports.");
        }

        // Normal output without control strings is one native write and passes through unchanged.
        using (var probe = Run("plain \u001b[1mbold\u001b[0m 終", [3]))
            Check(Encoding.UTF8.GetString(probe.Output) == "plain \u001b[1mbold\u001b[0m 終" && probe.Log.Count == 0, "Plain output passes through.");

        Console.WriteLine("Protocol stream checks passed authentication, OSC 133/7501, title/reset order, cancellation, C1, string filtering, capture and limits.");
    }
}
