using System.Globalization;
using System.Text;

namespace AgentTerm.Terminal;

/// <summary>One run of cells of a link on a single viewport row. <see cref="EndColumn"/> is exclusive.</summary>
public readonly record struct TerminalLinkSpan(int Row, int StartColumn, int EndColumn)
{
    public bool Contains(int column, int row) => row == Row && column >= StartColumn && column < EndColumn;
}

public enum TerminalLinkDecision
{
    /// <summary>Safe to open on a deliberate user click.</summary>
    Allow,
    /// <summary>Well formed but hands the target to another program. Ask the user first.</summary>
    Confirm,
    /// <summary>Never opened.</summary>
    Block
}

/// <summary>
/// What the app may do with one OSC 8 target. <see cref="Text"/> is the exact decoded URI when <see cref="IsCopyable"/>.
/// <see cref="Display"/> is always safe to show: it escapes control characters and is length limited.
/// </summary>
public sealed record TerminalLinkTarget(string Text, string Display, Uri? Uri, TerminalLinkDecision Decision,
    string Reason, bool IsCopyable)
{
    public bool CanOpen => Decision != TerminalLinkDecision.Block && Uri is not null;
}

/// <summary>An OSC 8 link found at a displayed cell. Owned data. Holds no native reference.</summary>
public sealed class TerminalLink
{
    private readonly byte[] _raw;

    internal TerminalLink(byte[] raw, IReadOnlyList<TerminalLinkSpan> spans)
    {
        _raw = raw;
        Spans = spans;
        Target = TerminalLinkPolicy.Evaluate(raw);
    }

    public TerminalLinkTarget Target { get; }
    /// <summary>The cells of this link in the viewport, in row order. Rows join only across soft wraps.</summary>
    public IReadOnlyList<TerminalLinkSpan> Spans { get; }
    public bool Contains(int column, int row)
    {
        foreach (var span in Spans) if (span.Contains(column, row)) return true;
        return false;
    }
    /// <summary>True when both links carry the same URI bytes. Spans are not compared.</summary>
    public bool SameDestination(TerminalLink? other) => other is not null && _raw.AsSpan().SequenceEqual(other._raw);
}

/// <summary>Decides whether a program-supplied URI may be shown, copied or opened. Pure. Starts no program.</summary>
public static class TerminalLinkPolicy
{
    public const int MaxUriBytes = 2083;
    private const int MaxDisplayCharacters = 512;
    private static readonly UTF8Encoding Strict = new(false, true);

    private static readonly HashSet<string> BlockedSchemes = new(StringComparer.Ordinal)
    {
        "javascript", "vbscript", "data", "blob", "about", "ms-msdt", "msdt", "search-ms", "search", "ms-appinstaller",
        "ms-officecmd", "ms-cxh", "ms-cxh-full", "shell", "mk", "res", "jar", "livescript", "mocha"
    };

    // A file link goes to the shell, so anything that can run or install code is refused.
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi",
        ".msp", ".msix", ".msixbundle", ".appx", ".appxbundle", ".appinstaller", ".application", ".scr", ".pif", ".lnk", ".url",
        ".hta", ".jar", ".reg", ".cpl", ".dll", ".sys", ".inf", ".scf", ".gadget", ".chm", ".settingcontent-ms", ".library-ms",
        ".search-ms", ".docm", ".xlsm", ".pptm", ".py", ".pyw", ".rb", ".sh"
    };

    public static TerminalLinkTarget Evaluate(ReadOnlySpan<byte> raw)
    {
        if (raw.IsEmpty) return Blocked("", "", "Empty link.", false);
        if (raw.Length > MaxUriBytes)
            return Blocked("", Escape(Encoding.UTF8.GetString(raw[..MaxUriBytes])) + "…", "The link is too long.", false);

        string text;
        try { text = Strict.GetString(raw); }
        catch (DecoderFallbackException)
        {
            return Blocked("", Escape(Encoding.UTF8.GetString(raw)), "The link is not valid UTF-8.", false);
        }

        var display = Escape(text);
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsControl(rune) || Rune.IsWhiteSpace(rune) || category is UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned or
                UnicodeCategory.PrivateUse)
                return Blocked("", display, "The link contains control, space or invisible characters.", false);
        }

        var colon = text.IndexOf(':');
        if (colon < 2 || !IsSchemeName(text.AsSpan(0, colon)))
            return Blocked(text, display, "The link is not an absolute URI with a scheme.", true);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, text[..colon], StringComparison.OrdinalIgnoreCase))
            return Blocked(text, display, "The link is not a valid URI.", true);

        var scheme = uri.Scheme.ToLowerInvariant();
        if (BlockedSchemes.Contains(scheme))
            return new(text, display, null, TerminalLinkDecision.Block, $"Links using {scheme}: are never opened.", true);

        switch (scheme)
        {
            case "http" or "https":
                if (string.IsNullOrEmpty(uri.Host))
                    return new(text, display, null, TerminalLinkDecision.Block, "The web link has no host.", true);
                // user@host spoofs the real destination, so it needs an explicit look.
                return new(text, display, uri, uri.UserInfo.Length == 0 ? TerminalLinkDecision.Allow : TerminalLinkDecision.Confirm,
                    uri.UserInfo.Length == 0 ? "Web link." : "The link contains a user name before the host.", true);
            case "mailto":
                return new(text, display, uri, TerminalLinkDecision.Allow, "Email link.", true);
            case "file":
                return EvaluateFile(text, display, uri);
            default:
                return new(text, display, uri, TerminalLinkDecision.Confirm, $"Opens another program using {scheme}:.", true);
        }
    }

    private static TerminalLinkTarget EvaluateFile(string text, string display, Uri uri)
    {
        // .NET treats even file://localhost/ as a network path, so only a link with no host counts as local.
        if (uri.Host.Length != 0 || uri.IsUnc)
            return new(text, display, null, TerminalLinkDecision.Block, "File links to other machines are never opened.", true);
        var path = uri.LocalPath;
        var name = Path.GetFileName(path.Replace('\\', '/').TrimEnd('/'));
        // Windows drops trailing dots and spaces, and ":" selects an alternate data stream.
        if (name.EndsWith('.') || name.EndsWith(' ') || name.Contains(':') || path.StartsWith(@"\\", StringComparison.Ordinal))
            return new(text, display, null, TerminalLinkDecision.Block, "The file path is not a plain local path.", true);
        if (CodeExtensions.Contains(Path.GetExtension(name)))
            return new(text, display, null, TerminalLinkDecision.Block, "File links to programs and scripts are never opened.", true);
        return new(text, display, uri, TerminalLinkDecision.Confirm, "Opens a local file with its default program.", true);
    }

    private static bool IsSchemeName(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || !char.IsAsciiLetter(value[0])) return false;
        foreach (var c in value) if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.')) return false;
        return true;
    }

    private static TerminalLinkTarget Blocked(string text, string display, string reason, bool copyable) =>
        new(text, display, null, TerminalLinkDecision.Block, reason, copyable);

    /// <summary>Makes text safe to show: controls become visible symbols, and long text is cut.</summary>
    internal static string Escape(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, MaxDisplayCharacters + 1));
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (count++ >= MaxDisplayCharacters) { builder.Append('…'); break; }
            var category = Rune.GetUnicodeCategory(rune);
            if (rune.Value < 0x20) builder.Append((char)(0x2400 + rune.Value));
            else if (rune.Value == 0x7F) builder.Append('␡');
            else if (Rune.IsControl(rune) || category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or
                     UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned)
                builder.Append('�');
            else builder.Append(rune.ToString());
        }
        return builder.ToString();
    }
}

/// <summary>
/// Which pointer events belong to a link and which stay with the terminal program. Pure, so hosts and tests share it.
/// Ctrl alone is the open gesture. Shift stays the selection override and Ctrl with Alt is AltGr.
/// </summary>
public static class TerminalLinkGesture
{
    public static bool IsOpenModifier(TerminalModifiers modifiers) => modifiers == TerminalModifiers.Control;

    /// <summary>
    /// Whether a link lookup and hover feedback may run. A program that tracks the mouse keeps plain pointer events,
    /// so feedback waits for the open modifier. A selection drag never shows hover feedback.
    /// </summary>
    public static bool ShowsHover(TerminalModifiers modifiers, bool programTracksMouse, bool selecting) =>
        !selecting && (!programTracksMouse || IsOpenModifier(modifiers));

    /// <summary>Whether a button press is taken from both selection and the program. Only Ctrl+left on a link is.</summary>
    public static bool ClaimsPress(TerminalModifiers modifiers, TerminalMouseButton button, bool linkUnderPointer) =>
        linkUnderPointer && button == TerminalMouseButton.Left && IsOpenModifier(modifiers);

    /// <summary>Whether a release opens the link. The pointer must still be on a link with the pressed destination.</summary>
    public static bool OpensOnRelease(TerminalLink pressed, TerminalLink? underPointer) =>
        underPointer is not null && underPointer.SameDestination(pressed);

    /// <summary>The pointer to show. The open gesture over a link overrides the program's requested shape.</summary>
    public static TerminalPointerKind PointerFor(TerminalMouseShape requested, TerminalLink? link, TerminalModifiers modifiers, bool selecting)
    {
        if (link is not null && !selecting && IsOpenModifier(modifiers))
            return link.Target.CanOpen ? TerminalPointerKind.Hand : TerminalPointerKind.UniversalNo;
        return TerminalMouseShapes.ToPointer(requested);
    }
}
