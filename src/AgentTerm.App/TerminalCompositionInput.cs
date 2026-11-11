using System;

namespace AgentTerm_App;

// The TextBox owns the in-progress text. Only the final suffix belongs to the terminal.
internal sealed class TerminalCompositionInput
{
    private string? _prefix;
    public bool IsComposing => _prefix is not null;

    public void Start(string text) => _prefix = text;

    public string Finish(string text)
    {
        if (_prefix is not { } prefix) return string.Empty;
        _prefix = null;
        return text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text;
    }

    public void Cancel() => _prefix = null;
}
