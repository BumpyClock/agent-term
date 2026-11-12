using AgentTerm.Terminal;

internal static class NativeProtocolChecks
{
    internal static void Run()
    {
        var protocol = new ShellPromptProtocol("fixture");
        void Send(string body) => protocol.Accept("777;fixture;" + body, body.Length + 12);
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        Send("entry;0"); Send("ready;");
        Check(protocol.State == ShellPromptState.Ready && !protocol.AgentEntryAvailable, "Custom Enter disables only entry, not shell readiness.");
        Send("entry;1"); Send("start"); Send("end;0;7;"); Send("ready;");
        Check(protocol.LastCommandSucceeded == false && protocol.LastExitCode == 7, "Accepted human failure is recorded.");
        Send("end;1;0;"); Send("ready;");
        Check(protocol.LastCommandSucceeded == false && protocol.LastExitCode == 7, "A redraw is not a successful command.");
        Send("agent;aXQncyBicm9rZW4=");
        Check(protocol.TakeAgentRequest() == "it's broken" && protocol.TakeAgentRequest() is null, "Agent requests decode literally and drain once.");
        Send("start;a:unique"); Send("end;1;0;a:unique"); Send("ready;a:unique");
        Check(protocol.State == ShellPromptState.Ready && protocol.ReadyCommandId == "a:unique", "Mailbox readiness identifies its command.");
        Send("start"); Send("end;1;0;"); Send("ready;");
        Check(protocol.ReadyCommandId is null, "Later human readiness does not retain an agent id.");

        var scanner = new OscScanner();
        var captureProtocol = new ShellPromptProtocol("capture");
        var capture = new CommandOutputCapture("a:fixture");
        scanner.Sequence += captureProtocol.Accept;
        scanner.OutputByte += capture.Feed;
        captureProtocol.Marker += capture.Marker;
        void Feed(string wire)
        {
            foreach (var value in System.Text.Encoding.UTF8.GetBytes(wire)) scanner.Feed([value]);
        }
        Feed("echo command\r\n\u001b]777;other;start;a:fixture\aignored\u001b]777;capture;start;a:other\aignored");
        Check(capture.Text == "" && !capture.Ended, "OSC alone cannot start ordered text capture.");
        Feed("\u001b]777;capture;start;a:fixture\aCommand started [a:fixture]\r\nfirst 🦉終\r\n\r\n\r\nlast\r\n");
        Feed("\u001b]777;capture;end;1;0;a:other\a");
        capture.Marker("ready;a:fixture");
        Check(!capture.Ended, "Readiness and another command's end cannot finish output capture.");
        Feed("Command ended [a:literal]\r\nCommand started [a:literal]\r\n");
        Feed("\u001b]777;capture;end;0;7;a:fixture\a\u001b]777;capture;ready;a:fixture\a");
        Check(!capture.Ended, "Reordered OSC completion must not cut off final text output.");
        Feed("final after OSC\r\nCommand ended [a:fixture]\r\nPS actual prompt> \u001b]777;capture;restored;a:fixture\a");
        Check(capture.Ended && capture.Text == "first 🦉終\r\n\r\n\r\nlast\r\nCommand ended [a:literal]\r\nCommand started [a:literal]\r\nfinal after OSC\r\n",
            "Ordered boundaries preserve fragmented UTF-8, blank rows, literal output and text following reordered OSC, excluding echo and prompt.");
        Check(captureProtocol.LastCommandSucceeded == false && captureProtocol.LastExitCode == 7,
            "Shell failure remains command metadata, not a fabricated agent-turn outcome.");
    }
}
