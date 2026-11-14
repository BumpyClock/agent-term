namespace AgentTerm.Terminal;

public sealed partial class TerminalSession
{
    /// <summary>
    /// Plain text of the command output containing a screen-absolute cell, without changing the selection. Pass the
    /// displayed frame's <see cref="TerminalViewport.Offset"/> plus the frame row. Null outside output.
    /// </summary>
    public string? ReadCommandOutput(long screenRow, int column)
    {
        using (_gate.Enter())
        {
            ThrowIfClosed();
            return _terminal.ReadCommandOutput(screenRow, column);
        }
    }

    /// <summary>Search highlights for the live terminal. Tests only; frames carry their own captured highlights.</summary>
    internal TerminalSearchHighlights? ReadSearchHighlights()
    {
        lock (_renderGate) using (_gate.Enter())
        {
            ThrowIfClosed();
            return _terminal.PrepareSearchHighlightsSynchronously();
        }
    }
}
