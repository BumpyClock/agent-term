namespace AgentTerm.Terminal;

public enum ClipboardOutcome { Success, Denied, Unsupported, Busy, InvalidData, IoError, TooLarge }

public enum ClipboardLocation { Standard, Selection, Primary }

/// <summary>One decoded MIME representation. <see cref="Data"/> is owned by this instance.</summary>
public sealed record ClipboardPart(string Mime, byte[] Data);

/// <summary>
/// The system clipboard as a program-driven terminal sees it. Calls run synchronously on the terminal reader thread
/// while it holds the terminal lock, so implementations must be thread-safe, must not wait for the UI thread,
/// and must give up quickly (Busy) instead of blocking. Tests inject a fake.
/// </summary>
public interface ITerminalClipboard
{
    /// <summary>Reads clipboard text. Fails with TooLarge when it exceeds <paramref name="maxChars"/> UTF-16 units.</summary>
    ClipboardOutcome TryReadText(int maxChars, out string text);
    ClipboardOutcome TryWriteText(string text);
    ClipboardOutcome TryClear();
    /// <summary>Reports whether the clipboard currently holds text without copying it.</summary>
    bool HasText();
}

public readonly record struct ClipboardWriteInput(ClipboardLocation Location, IReadOnlyList<ClipboardPart> Parts, string Program,
    long TotalBytes);
public readonly record struct ClipboardReadInput(ClipboardLocation Location, IReadOnlyList<string> Mimes, bool List, string Program, bool Granted);
public readonly record struct ClipboardWriteDecision(ClipboardOutcome Outcome);
public readonly record struct ClipboardReadDecision(ClipboardOutcome Outcome, ClipboardPart? Content, IReadOnlyList<string> Available);
