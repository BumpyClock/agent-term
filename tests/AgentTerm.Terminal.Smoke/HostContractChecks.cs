using System.Text;
using AgentTerm.Terminal;

internal static class HostContractChecks
{
    internal static void Run()
    {
        if (GhosttyTerminal.RenderLayoutMismatch is { } mismatch) throw new Exception(mismatch);
        CheckUnsupportedProtocols();
        using var terminal = new GhosttyTerminal(80, 24);
        string Drain() => string.Concat(terminal.TakeResponses().Select(Encoding.UTF8.GetString));
        void Equal(string expected, string actual, string reason)
        {
            if (expected != actual) throw new Exception($"{reason}. Expected <{expected}>, got <{actual}>.");
        }
        terminal.Write("\u001b_Gi=31,s=1,v=1,a=q,t=d,f=24;AAAA\u001b\\"u8);
        Equal("", Drain(), "Images are not advertised before the host enables rendering");
        terminal.Resize(80, 24, 9, 18);
        Equal("", Drain(), "Resize without mode 2048 is silent");
        terminal.Write("\u001b[14t\u001b[16t\u001b[18t\u001b[19t"u8);
        Equal("\u001b[4;432;720t\u001b[6;18;9t\u001b[8;24;80t\u001b[9;24;80t", Drain(), "Native size queries use device pixels");
        terminal.Write("\u001b[?2048h"u8);
        Equal("\u001b[48;24;80;432;720t", Drain(), "Enabling in-band reports queries host geometry");
        terminal.Resize(80, 24, 12, 24);
        Equal("\u001b[48;24;80;576;960t", Drain(), "Font or DPI changes report even with unchanged grid");
        terminal.Write("\u001b[?2048l"u8);
        terminal.Resize(80, 24, 9, 18);
        Equal("", Drain(), "Disabling in-band reports stops unsolicited replies");
        terminal.Write("\u001b[>q\u001bP+q544e\u001b\\\u001b[c\u001b[>c"u8);
        Equal("\u001bP>|" + GhosttyTerminal.HostVersion + "\u001b\\\u001bP1+r544E=787465726D2D323536636F6C6F72\u001b\\\u001b[?62;22c\u001b[>1;0;0c", Drain(), "Truthful host identity and supported capabilities");
        terminal.SetHostCapabilities(new(Clipboard: true));
        terminal.Write("\u001b[c"u8);
        Equal("\u001b[?62;22;52c", Drain(), "Clipboard advertisement is explicitly enabled by its host owner");
        terminal.SetLightTheme(false); terminal.SetLightTheme(true);
        Equal("", Drain(), "Theme changes without mode 2031 are silent");
        terminal.Write("\u001b[?2031h"u8); Drain();
        terminal.SetLightTheme(false);
        Equal("\u001b[?997;1n", Drain(), "Dark scheme change reports when subscribed");
        terminal.SetLightTheme(false);
        Equal("", Drain(), "Same scheme emits no unsolicited reply");
        terminal.SetLightTheme(true);
        Equal("\u001b[?997;2n", Drain(), "Light scheme change reports when subscribed");
        terminal.Write("\u001b[?996n"u8);
        Equal("\u001b[?997;2n", Drain(), "Existing color scheme query remains functional");
        terminal.Write("\u001b[999999999999999999999999mhello"u8);
        var report = terminal.ReadDiagnosticReport();
        if (!report.Contains("libghostty-vt 1.") || !report.Contains("VT processing error False") || !report.Contains("Page resident bytes"))
            throw new Exception("Diagnostics must expose runtime version, memory and nonfatal VT status. " + report);
        using var conpty = new GhosttyTerminal(10, 3);
        using var general = new GhosttyTerminal(10, 3);
        conpty.ConfigureConPtyHost();
        foreach (var target in new[] { conpty, general })
        {
            target.Write("one\r\ntwo\r\nthree\r\nfour\r\nfive"u8);
            target.Resize(10, 5);
        }
        Equal("three\nfour\nfive", conpty.ReadText(), "ConPTY growth keeps history out of the active screen");
        Equal("one\ntwo\nthree\nfour\nfive", general.ReadText(), "General terminal retains its default scrollback pulling behavior");
        conpty.ScrollTo(0);
        Equal("o", conpty.ReadFrame().Cells[0].Text, "ConPTY resize preserves old history for inspection");
        Console.WriteLine("Host geometry, identity, capabilities, theme and diagnostic checks passed.");
    }

    private static void CheckUnsupportedProtocols()
    {
        using var terminal = new GhosttyTerminal();
        terminal.Write("\u001b]984321;private-payload\a"u8);
        if (terminal.ReadDiagnosticReport().Contains("984321")) throw new Exception("Unsupported protocol capture must be opt-in.");
        terminal.SetUnknownSequenceDiagnostics(true);
        terminal.Write("\u001b]984321;private-"u8);
        terminal.Write("payload\a\u001b]984322;cancelled\u0018"u8);
        terminal.Write(Encoding.UTF8.GetBytes("\u001b]984321;" + new string('x', 200) + "\a"));
        terminal.Write("\u001b_unrecognized-private-data\u001b\\"u8);
        var report = terminal.ReadDiagnosticReport();
        if (!report.Contains("Unsupported OSC 984321: 2, truncated 1") || !report.Contains("Unsupported APC unknown: 1") ||
            report.Contains("private") || report.Contains("984322"))
            throw new Exception("Unsupported protocol counters must be bounded metadata from complete native events. " + report);
        for (var i = 0; i < 100; i++) terminal.Write(Encoding.UTF8.GetBytes($"\u001b]{985000 + i};data\a"));
        report = terminal.ReadDiagnosticReport();
        if (report.Split('\n').Count(line => line.StartsWith("Unsupported OSC ")) > 16 || !report.Contains("Other unsupported sequences"))
            throw new Exception("Unsupported protocol identifier storage must stay bounded.");
        terminal.SetUnknownSequenceDiagnostics(false);
        terminal.SetUnknownSequenceDiagnostics(true);
        if (terminal.ReadDiagnosticReport().Contains("984321")) throw new Exception("Disabling diagnostics must release collected identifiers.");
    }
}
