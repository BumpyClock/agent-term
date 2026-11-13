using System.Text;
using AgentTerm.Terminal;

internal static class HistoryFeatureChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        using var terminal = new GhosttyTerminal(40, 4);
        terminal.Write(Encoding.UTF8.GetBytes("first history row\r\n\u001b[31m<&> é 界 é\u001b[0m\r\nthird\r\nfourth\r\nfifth"));
        var before = terminal.ExportHistory(TerminalExportFormat.Plain)!;
        Check(before.Contains("first history row"), "History export must include scrollback.");
        var html = terminal.ExportHistory(TerminalExportFormat.Html)!;
        Check(html.Contains("&lt;") && html.Contains("&amp;") && System.Net.WebUtility.HtmlDecode(html).Contains("界") && System.Net.WebUtility.HtmlDecode(html).Contains("é"), "HTML must escape text and preserve Unicode.");
        var vt = terminal.ExportHistory(TerminalExportFormat.StyledVt)!;
        Check(vt.Contains("\u001b[") && vt.Contains("<&>"), "VT export must preserve styling.");
        terminal.ScrollTo(0);
        terminal.BeginSelection(0, 0); terminal.ExtendSelection(4, 0); terminal.EndSelection();
        Check(terminal.ExportHistory(TerminalExportFormat.Plain, true) == "first", "Selection export must use inclusive exact range.");
        Check(terminal.ExportHistory(TerminalExportFormat.Html, true)!.Contains("first") && !terminal.ExportHistory(TerminalExportFormat.Html, true)!.Contains("third"), "HTML selection must exclude unselected rows.");
        terminal.ClearSelection();
        Check(terminal.ExportHistory(TerminalExportFormat.Html, true) is null, "Missing selection must not export entire history.");
        for (int i = 0; i < 12000; i++) terminal.Write(Encoding.UTF8.GetBytes($"\r\nrow {i:D5} repeated repeated repeated"));
        var anchor = terminal.AnchorActiveRow(0);
        var logical = terminal.ExportHistory(TerminalExportFormat.Plain)!;
        var anchorBefore = terminal.ReadAnchor(anchor);
        Check(anchorBefore is not null, "Compression fixture needs a live tracked anchor.");
        for (int i = 0; i < 20000 && terminal.CompressHistoryStep(); i++) { }
        Check(terminal.ExportHistory(TerminalExportFormat.Plain) == logical, "Compression must preserve exact logical history.");
        Check(terminal.ReadAnchor(anchor) == anchorBefore, "Compression must preserve tracked references.");
        terminal.ReleaseAnchor(anchor);
        using var wrap = new GhosttyTerminal(4, 3);
        wrap.Write("abcdef  "u8);
        Check(wrap.ExportHistory(TerminalExportFormat.Plain, unwrap: true, trim: true)!.Contains("abcdef"), "Unwrapped export must join soft wraps.");
        Check(wrap.ExportHistory(TerminalExportFormat.Plain, unwrap: false, trim: false)!.Contains("abcd\nef"), "Physical export must retain soft row breaks.");
        wrap.BeginSelection(0, 0, rectangular: true); wrap.ExtendSelection(1, 1); wrap.EndSelection();
        Check(wrap.ExportHistory(TerminalExportFormat.Plain, true) == wrap.ReadSelectionText() && wrap.ReadSelectionText() == "ab\nef", "Rectangular exports must preserve physical rows across a soft wrap.");
        var rectangularHtml = wrap.ExportHistory(TerminalExportFormat.Html, true)!;
        Check(rectangularHtml.Contains("ab") && rectangularHtml.Contains("ef") && !rectangularHtml.Contains("abef"), "Rich rectangles must not join the selected rows.");
        using var prompts = new GhosttyTerminal(20, 3);
        prompts.Write("\u001b]133;A\aprompt1\r\none\r\n\u001b]133;A\aprompt2\r\ntwo\r\n\u001b]133;A\aprompt3"u8);
        prompts.NavigatePrompt(-1);
        var previous = prompts.ReadFrame().Viewport.Offset;
        Check(previous < prompts.ReadFrame().Viewport.MaximumOffset, "Previous prompt must move into history.");
        prompts.Resize(10, 3);
        prompts.NavigatePrompt(1);
        Check(prompts.ReadFrame().Viewport.Offset >= previous, "Prompt navigation must remain usable after reflow.");
        SearchOwnershipChecks.RunAsync().GetAwaiter().GetResult();
        HistoryGapChecks.Run();
        Console.WriteLine("History feature checks passed.");
    }
}
