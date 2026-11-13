using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Text;

// Run on the WinUI dispatcher from the app smoke entrypoint.
internal static class KeyboardAutomationChecks
{
    internal static void Run()
    {
        using var terminal = new GhosttyTerminal(8, 3);
        terminal.Write("A界é\r\nrow"u8);
        var surface = new TerminalSurface();
        surface.SetFrame(terminal.ReadFrame());
        surface.AutomationSelectRange = (_, x1, y1, x2, y2) =>
        {
            terminal.BeginSelection(x1, y1);
            terminal.ExtendSelection(x2, y2);
            terminal.EndSelection();
            return true;
        };
        surface.AutomationClearSelection = terminal.ClearSelection;
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(surface);
        var provider = (ITextProvider)peer.GetPattern(PatternInterface.Text);
        var document = provider.DocumentRange;
        const string expected = "A界é    \nrow     \n        ";
        Equal(expected, document.GetText(-1), "Automation reads an owned Unicode viewport");
        var word = document.FindText("界é", false, false);
        word.Select();
        Equal("界é", terminal.ReadSelectionText(), "Automation selects the native wide and combining cells");
        surface.SetFrame(terminal.ReadFrame());
        Equal("界é", string.Concat(provider.GetSelection().Select(range => range.GetText(-1))), "Automation reports native selection flags");

        var character = provider.DocumentRange;
        character.ExpandToEnclosingUnit(TextUnit.Character);
        if (character.Move(TextUnit.Character, 2) != 2) throw new InvalidOperationException("Automation character movement must advance two graphemes.");
        Equal("é", character.GetText(-1), "Automation character units retain a combining sequence");
        if (!Equals(character.GetAttributeValue(40015), true)) throw new InvalidOperationException("Terminal output must be read-only.");
        terminal.Write("\x1b[2;1Hchanged"u8);
        surface.SetFrame(terminal.ReadFrame());
        Equal(expected, document.GetText(-1), "Old automation ranges own their text after output changes");
        try { word.Select(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("A stale automation range must not select new output.");
    }

    private static void Equal(string expected, string? actual, string label)
    {
        if (expected != actual) throw new InvalidOperationException($"{label}. Expected <{expected}>, got <{actual}>.");
    }
}
