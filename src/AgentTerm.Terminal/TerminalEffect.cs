namespace AgentTerm.Terminal;

/// <summary>Progress state reported by OSC 9;4.</summary>
public enum TerminalProgressState { Remove, Set, Error, Indeterminate, Pause }

/// <summary>An effect a program requested through the terminal. Instances are immutable copies that own their data.</summary>
public abstract record TerminalEffect;

/// <summary>Withdraws earlier notices, approvals, and progress after a full terminal reset.</summary>
public sealed record ResetEffect : TerminalEffect;

/// <summary>One or more bells. Consecutive bells in one batch coalesce into <paramref name="Count"/>.</summary>
public sealed record BellEffect(int Count) : TerminalEffect;

/// <summary>A desktop notification request. <paramref name="Repeats"/> counts identical or suppressed requests merged into this one.</summary>
public sealed record NotificationEffect(string Title, string Body, int Repeats = 1) : TerminalEffect;

/// <summary>
/// Latest progress state. <paramref name="Percent"/> is 0 to 100, or -1 when the program omitted it for Error and Pause,
/// which means keep the previous percentage.
/// </summary>
public sealed record ProgressEffect(TerminalProgressState State, int Percent) : TerminalEffect;

/// <summary>What happened to a clipboard request, for user-visible disclosure.</summary>
public enum ClipboardNoticeKind
{
    /// <summary>A program replaced the clipboard text.</summary>
    Wrote,
    /// <summary>A program cleared the clipboard.</summary>
    Cleared,
    /// <summary>A write was refused by policy.</summary>
    WriteBlocked,
    /// <summary>A write was refused because it exceeded the size limit.</summary>
    WriteTooLarge,
    /// <summary>The system clipboard rejected the write.</summary>
    WriteFailed,
    /// <summary>A write waits for the user to allow it. See <see cref="ClipboardEffect.RequestId"/>.</summary>
    WriteRequested,
    /// <summary>A program read the clipboard text.</summary>
    ReadServed,
    /// <summary>A read was refused by policy.</summary>
    ReadBlocked,
    /// <summary>The clipboard could not be read or exceeded the size limit.</summary>
    ReadFailed,
}

/// <summary>A clipboard request outcome. <paramref name="Program"/> is the name the program supplied, or empty.</summary>
public sealed record ClipboardEffect(ClipboardNoticeKind Kind, int Length, string Program, long RequestId = 0,
    string Preview = "", int Repeats = 1) : TerminalEffect;

/// <summary>A program asked to resize the window's text area. Zero keeps the current dimension.</summary>
public sealed record ResizeRequestEffect(int Rows, int Columns) : TerminalEffect;
