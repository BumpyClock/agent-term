using System.Text;
using AgentTerm.Terminal;

ProgramTitleChecks.Run();
if (args.Contains("--program-title-only")) return;
PromptFocusChecks.Run();
if (args.Contains("--focus-policy-only")) return;
NativeProtocolChecks.Run();
LiveEdgeChecks.Run();
if (args.Contains("--gate0-only")) return;

static void AssertText(GhosttyTerminal terminal, string expected, string message)
{
    var actual = terminal.ReadText();
    if (!string.Equals(actual, expected, StringComparison.Ordinal))
        throw new InvalidOperationException($"{message} Expected <{expected}>, got <{actual}>.");
}

static void AssertThrows<T>(Action action, string message) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

using (var terminal = new GhosttyTerminal(20, 4))
{
    AssertText(terminal, "", "A new terminal should be empty.");
    terminal.Write(ReadOnlySpan<byte>.Empty);
    AssertText(terminal, "", "An empty write should preserve the screen.");

    terminal.Write(Encoding.UTF8.GetBytes("plain\x1b[31m red\x1b[0m"));
    AssertText(terminal, "plain red", "SGR sequences should not appear in plain text output.");
}

using (var terminal = new GhosttyTerminal(20, 4))
{
    if (terminal.ReadTitle() != "") throw new InvalidOperationException("A new terminal should have no title.");
    var title = Encoding.UTF8.GetBytes("\x1b]0;⠂ Fix tab é\a");
    terminal.Write(title.AsSpan(0, 7));
    terminal.Write(title.AsSpan(7));
    if (terminal.ReadTitle() != "⠂ Fix tab é") throw new InvalidOperationException($"OSC 0 should set a UTF-8 title, got <{terminal.ReadTitle()}>.");
    terminal.Write("\x1b]2;Claude Code\x1b\\"u8);
    if (terminal.ReadTitle() != "Claude Code") throw new InvalidOperationException("OSC 2 with ST should replace the title.");
    AssertText(terminal, "", "Title sequences should not print.");
}

using (var terminal = new GhosttyTerminal(20, 4))
{
    terminal.Write("abcdef\rOK"u8);
    AssertText(terminal, "OKcdef", "Carriage return should overwrite the start of the current row.");
}

using (var terminal = new GhosttyTerminal(20, 4))
{
    terminal.Write("old\r\ncontent\x1b[2J"u8);
    AssertText(terminal, "", "Erase display should remove all existing screen text.");
    terminal.Write("\x1b[2;3Hhere"u8);
    AssertText(terminal, "\n  here", "Cursor positioning should use one-based row and column coordinates.");
    terminal.Write("\x1b[1;1Htop"u8);
    AssertText(terminal, "top\n  here", "Cursor movement should leave other rows intact.");
}

using (var terminal = new GhosttyTerminal(20, 4))
{
    terminal.Write("old"u8);
    terminal.Write("\x1b"u8);
    terminal.Write("[2"u8);
    terminal.Write("J\x1b["u8);
    terminal.Write("2;"u8);
    terminal.Write("3Hnew"u8);
    AssertText(terminal, "\n  new", "Escape sequences split across writes should clear and position the cursor.");
}

using (var terminal = new GhosttyTerminal(20, 4))
{
    var utf8 = Encoding.UTF8.GetBytes("Aé€🙂界");
    for (var index = 0; index < utf8.Length; index++)
        terminal.Write(utf8.AsSpan(index, 1));
    AssertText(terminal, "Aé€🙂界", "Split two-, three-, and four-byte UTF-8 sequences should preserve text.");
}

using (var terminal = new GhosttyTerminal(4, 4))
{
    terminal.Write("abcdef"u8);
    AssertText(terminal, "abcd\nef", "Text should wrap at the original width.");
    terminal.Resize(8, 4);
    AssertText(terminal, "abcdef", "Widening should reflow soft-wrapped rows.");
    terminal.Write("ghi"u8);
    AssertText(terminal, "abcdefgh\ni", "New text should wrap at the resized width.");
    terminal.Resize(3, 4);
    AssertText(terminal, "abc\ndef\nghi", "Narrowing should reflow the existing text.");
}

using (var terminal = new GhosttyTerminal(10, 2))
{
    terminal.Write("one\r\ntwo\r\nthree"u8);
    AssertText(terminal, "two\nthree", "ReadText should exclude rows that scrolled into history.");
    terminal.Write("\x1b[?1049h\x1b[Halt"u8);
    AssertText(terminal, "alt", "ReadText should follow the alternate screen.");
    terminal.Write("\x1b[?1049l"u8);
    AssertText(terminal, "two\nthree", "Leaving the alternate screen should restore the primary screen.");
}

using (var terminal = new GhosttyTerminal())
{
    AssertThrows<ArgumentOutOfRangeException>(() => terminal.Resize(0, 4), "Zero resize width should be rejected.");
    AssertThrows<ArgumentOutOfRangeException>(() => terminal.Resize(4, 0), "Zero resize height should be rejected.");
    terminal.Dispose();
    terminal.Dispose();
    AssertThrows<ObjectDisposedException>(() => terminal.ReadText(), "Reading a disposed terminal should fail clearly.");
    AssertThrows<ObjectDisposedException>(() => terminal.Write("x"u8), "Writing a disposed terminal should fail clearly.");
    AssertThrows<ObjectDisposedException>(() => terminal.Write(ReadOnlySpan<byte>.Empty), "Empty writes should still reject disposal.");
    AssertThrows<ObjectDisposedException>(() => terminal.Resize(4, 4), "Resizing a disposed terminal should fail clearly.");
}

AssertThrows<ArgumentOutOfRangeException>(() => new GhosttyTerminal(0, 4), "Zero initial width should be rejected.");
AssertThrows<ArgumentOutOfRangeException>(() => new GhosttyTerminal(4, 0), "Zero initial height should be rejected.");

using (var terminal = new GhosttyTerminal(20, 4))
{
    terminal.Write("abc\x1b[6n"u8);
    var replies = terminal.TakeResponses();
    if (replies.Length != 1 || Encoding.UTF8.GetString(replies[0]) != "\x1b[1;4R")
        throw new InvalidOperationException("A cursor position query must produce the current cursor reply.");
    if (terminal.TakeResponses().Length != 0)
        throw new InvalidOperationException("Replies must be drained exactly once.");
    AssertText(terminal, "abc", "Query replies must not be drawn into the terminal.");
}


OscScannerEquivalenceChecks.Run();
RenderChecks.Run();
ProgramStatusChecks.Run();
ProgramStatusPresentationChecks.Run();
ThemeChecks.Run();
InputChecks.Run();
MouseChecks.Run();
ScrollbackChecks.Run();
PreferencesChecks.Run();
TabPositionChecks.Run();
GitHubChecks.Run();
await PaneStatusChecks.RunAsync();
Console.WriteLine("AgentTerm.Terminal smoke checks passed.");
