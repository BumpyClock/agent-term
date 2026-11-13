using System;
using System.IO;
using System.Text;

namespace AgentTerm_App;

/// <summary>Turns a program-set terminal title into a tab label.</summary>
internal static class ProgramTitle
{
    private const int MaximumLength = 80;

    /// <summary>Returns null when the title adds nothing over the shell name.</summary>
    internal static string? Clean(string title)
    {
        var builder = new StringBuilder(Math.Min(title.Length, MaximumLength));
        var leading = true;
        foreach (var rune in title.EnumerateRunes())
        {
            // Agents animate a spinner glyph at the start of the title; the tab already shows activity.
            if (leading && (Rune.IsWhiteSpace(rune) || rune.Value is >= 0x2700 and <= 0x27BF or >= 0x2800 and <= 0x28FF)) continue;
            leading = false;
            if (Rune.IsControl(rune)) continue;
            if (builder.Length + rune.Utf16SequenceLength > MaximumLength) break;
            builder.Append(rune.ToString());
        }
        var text = builder.ToString().TrimEnd();
        if (text.Length == 0) return null;
        // ConPTY reports the shell's executable path as the initial console title.
        if (Path.IsPathFullyQualified(text) && text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        return text;
    }
}
