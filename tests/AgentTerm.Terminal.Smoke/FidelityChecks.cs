using System.Text;
using AgentTerm.Terminal;

internal static class FidelityChecks
{
    internal static void Run()
    {
        using var terminal = new GhosttyTerminal(20, 3);
        terminal.Write(Encoding.UTF8.GetBytes("\u001b[4:2;58:2::12:34:56;53;5mA\u001b[0m\u001b[4:3mB\u001b[4:4mC\u001b[4:5mD"));
        var cells = terminal.ReadFrame().Cells;
        if (cells[0].UnderlineStyle != TerminalUnderlineStyle.Double || cells[0].UnderlineColor != new TerminalColor(12, 34, 56) || !cells[0].Overline || !cells[0].Blink ||
            cells[1].UnderlineStyle != TerminalUnderlineStyle.Curly || cells[2].UnderlineStyle != TerminalUnderlineStyle.Dotted || cells[3].UnderlineStyle != TerminalUnderlineStyle.Dashed)
            throw new InvalidOperationException("Text decorations were not captured from native state.");
        terminal.Write(Encoding.UTF8.GetBytes("\u001b[0m\u001b[4;58;5;1mE"));
        var before = terminal.ReadFrame().Cells[4];
        terminal.Write(Encoding.UTF8.GetBytes("\u001b]4;1;rgb:12/34/56\u001b\\"));
        var after = terminal.ReadFrame().Cells[4];
        if (before.UnderlineColor == after.UnderlineColor || after.UnderlineColor != new TerminalColor(0x12, 0x34, 0x56))
            throw new InvalidOperationException("Palette changes must update underline color.");
    }
}
