using System.Text;
using AgentTerm.Terminal;

/// <summary>OSC 7501 record rules over native validated reports.</summary>
internal static class ProgramStatusChecks
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Report(TerminalProtocolStream scanner, string body) => scanner.Feed(Encoding.UTF8.GetBytes("\u001b]7501;" + body + "\u001b\\"));
    private static void Feed(this TerminalProtocolStream stream, ReadOnlySpan<byte> bytes) => stream.Write(bytes);

    public static void Run()
    {
        // Wired as TerminalSession wires native reports, prompt markers and resets.
        using var terminal = new GhosttyTerminal(80, 8);
        var protocol = new ProgramStatusProtocol();
        var scanner = new TerminalProtocolStream(terminal, "777;secret;");
        var prompt = new ShellPromptProtocol("secret");
        scanner.Sequence += prompt.Accept;
        scanner.ProtocolEvent += item =>
        {
            if (item is TerminalProgramStatusReport report) protocol.Apply(report);
            else if (item is TerminalSemanticPrompt { StartsNewPrompt: true }) protocol.EndCommand();
            else if (item is TerminalReset) protocol.Reset();
        };
        prompt.PromptStarted += protocol.EndCommand;
        var stream = Encoding.UTF8.GetBytes("plain\u001b]7501;?\u001b\\\u001b]7501;state=working:app=claude-code:msg=SGVsbG8:progress=40\a");
        foreach (var b in stream) scanner.Feed([b]);
        var replies = terminal.TakeResponses();
        Check(replies.Length == 1, "A split feature query must produce one support response.");
        Check(Encoding.UTF8.GetString(replies[0]) == "\u001b]7501;?:states=idle,working,done,blocked,error:kinds=permission,question,auth\u001b\\", "Detection must advertise supported states and kinds without reflecting input.");
        var root = protocol.ReadStatuses().Single();
        Check(root.State == ProgramState.Working && root.Message == "Hello" && root.Progress == 40, "Split reports and unpadded text must decode.");
        Report(scanner, "state=blocked:id=task/child:kind=permission:title=VGVzdA==");
        var child = protocol.ReadStatuses().Single(r => r.Id == "task/child");
        Check(child.EffectiveApp == "claude-code" && child.Title == "Test" && child.Kind == "permission", "Missing parent records must still inherit the nearest app.");
        Report(scanner, "state=idle:app=codex");
        Check(protocol.ReadStatuses().Single(r => r.Id == "").Message is null && protocol.ReadStatuses().Single(r => r.Id == "task/child").EffectiveApp == "codex", "Replacement must remove omitted fields and update inherited apps.");
        Report(scanner, "state=done:id=task/child:progress=50:kind=auth");
        child = protocol.ReadStatuses().Single(r => r.Id == "task/child");
        Check(child.Progress is null && child.Kind is null, "State-specific fields must be ignored outside their states.");
        Report(scanner, "state=working:id=task");
        Report(scanner, "state=error:id=taskmate");
        Report(scanner, "state=clear:id=task");
        Check(protocol.ReadStatuses().Count == 2 && protocol.ReadStatuses().Any(r => r.Id == "taskmate"), "Clear must remove a subtree without matching sibling prefixes.");

        foreach (var invalid in new[]
        {
            "state=clear:title=" + new string('@', 257),
            "state=clear:msg=" + new string('@', 2733),
            "state=clear:title=" + new string('@', 257) + ":title=SGk=",
            "state=unknown", "state=done:id=bad/", "state=done:id=", "state=done:id=bad!",
            "state=done:msg=AA==", "state=done:msg=woA=", "state=done:msg=/w==", "state=done:msg=A",
            "state=done:title=" + new string('A', 260), "state=done:app=" + new string('a', 33),
            "state=done:abcdefghijklmnopq=x", "state=done:id=a/b/c/d/e/f/g/h/i",
            "state=done:extra=" + new string('a', 4096)
        })
        {
            Report(scanner, invalid);
            Check(protocol.ReadStatuses().Single(r => r.Id == "").State == ProgramState.Idle, "Invalid report must leave existing state unchanged: " + invalid[..Math.Min(invalid.Length, 80)]);
        }
        // Native parsing applies the specification's last-value rule: a replaced msg is length-checked but not decoded.
        Report(scanner, "state=done:msg=AA==:msg=SGk=");
        Check(protocol.ReadStatuses().Single(r => r.Id == "") is { State: ProgramState.Done, Message: "Hi" }, "The last msg value must win.");
        Report(scanner, "garbage:state=working:state=blocked:kind=question:future=yes:msg=@@@@");
        Check(protocol.ReadStatuses().Single(r => r.Id == "").State == ProgramState.Blocked, "Malformed pairs and unknown keys must be skipped; last state wins.");
        scanner.Feed("\u001b]133;A\a"u8);
        Check(protocol.ReadStatuses().Count == 1 && protocol.ReadStatuses()[0] is { State: ProgramState.Error, IsActive: false }, "Shell prompt must clear active states but preserve inactive outcomes.");
        Report(scanner, "state=done");
        Report(scanner, "state=working:id=busy");
        protocol.EndCommand();
        Check(protocol.ReadStatuses().Count == 2 && protocol.ReadStatuses().All(record => !record.IsActive), "Process exit must remove working records and deactivate retained outcomes.");
        Report(scanner, "state=idle:app=pi");
        Check(protocol.ReadStatuses().Single(record => record.Id == "").IsActive, "A new report must reactivate a retained record.");
        protocol.EndCommand();
        Check(protocol.ReadStatuses().All(record => !record.IsActive), "Idle reports must stop controlling tab identity when the shell returns.");
        protocol.EndCommand();
        Check(protocol.ReadStatuses().Count == 2, "Repeated prompt cleanup must preserve inactive reports.");
        scanner.Feed("\u001b[?1049h\u001b[!p\u001b[?1049l"u8);
        Check(protocol.ReadStatuses().Count == 2, "Alternate screen and soft reset must preserve records.");
        scanner.Feed("\u001bc"u8);
        Check(protocol.ReadStatuses().Count == 0, "Full terminal reset must clear records.");
        scanner.Feed("\u001b]7501;state=working\a\u001b]777;secret;ready\a\u001b]7501;state=blocked\a"u8);
        Check(protocol.ReadStatuses().Single().State == ProgramState.Blocked, "Native prompt cleanup must respect stream order within one chunk.");
        scanner.Feed("\u001b]777;wrong;ready\a"u8);
        Check(protocol.ReadStatuses().Count == 1, "Unrelated shell marker tokens must not clear records.");
        Report(scanner, "state=clear");
        scanner.Feed("\u001bP\u001b]7501;state=error\a\u001b\\"u8);
        Check(protocol.ReadStatuses().Single().State == ProgramState.Error, "ESC must abort DCS and start a new OSC.");
        Report(scanner, "state=clear");
        Report(scanner, "state=working");
        scanner.Feed("\u001b_ignored\u001b]133;A\a\u001b]7501;state=done\a"u8);
        Check(protocol.ReadStatuses().Single().State == ProgramState.Done, "Unterminated APC must not hide prompts or later reports.");
        Report(scanner, "state=working");
        scanner.Feed("\u001b]777;secret;rea\u001bdy\a"u8);
        Check(prompt.State == ShellPromptState.Ready && protocol.ReadStatuses().Single().State == ProgramState.Working, "Interrupted OSC must not change prompt readiness or clear status.");
        scanner.Feed("\u001b]777;secret;ready\a"u8);
        Check(prompt.State == ShellPromptState.Ready && protocol.ReadStatuses().Count == 0, "Prompt readiness and status cleanup must consume the same completed marker.");
        for (int i = 0; i < 256; i++) Report(scanner, $"state=idle:id=task{i}");
        Report(scanner, "state=working:id=task0");
        Report(scanner, "state=working:id=new");
        Check(protocol.ReadStatuses().Count == 256 && protocol.ReadStatuses().Any(r => r.Id == "task0") && !protocol.ReadStatuses().Any(r => r.Id == "task1"), "Record cap must evict the least recently updated record.");
        Report(scanner, "state=clear");
        Check(protocol.ReadStatuses().Count == 0, "Root clear must remove every record.");

        var version = protocol.Version;
        var snapshot = protocol.ReadStatuses();
        protocol.EndCommand();
        protocol.Reset();
        Report(scanner, "state=clear:id=missing");
        Check(protocol.Version == version && ReferenceEquals(snapshot, protocol.ReadStatuses()), "No-op cleanup must keep the status version and snapshot.");
        Report(scanner, "state=done:id=versioned");
        Check(protocol.Version > version && !ReferenceEquals(snapshot, protocol.ReadStatuses()), "A report must advance the status version and snapshot.");
        version = protocol.Version;
        snapshot = protocol.ReadStatuses();
        Check(ReferenceEquals(snapshot, protocol.ReadStatuses()), "Unchanged statuses must reuse one snapshot.");
        protocol.EndCommand();
        Check(protocol.Version > version && protocol.ReadStatuses().Single() is { IsActive: false }, "Deactivating a record must advance the status version.");
        version = protocol.Version;
        protocol.EndCommand();
        Check(protocol.Version == version, "Repeated prompt cleanup must not advance the status version.");
        Report(scanner, "state=working:id=versioned/child");
        version = protocol.Version;
        Report(scanner, "state=idle:id=versioned:app=codex");
        Check(protocol.Version > version && protocol.ReadStatuses().Single(r => r.Id == "versioned/child").EffectiveApp == "codex", "Parent changes must refresh inherited apps in the next snapshot.");
        version = protocol.Version;
        protocol.Reset();
        Check(protocol.Version > version && protocol.ReadStatuses().Count == 0, "Reset must advance the status version.");
    }
}
