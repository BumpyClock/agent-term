namespace AgentTerm.Terminal;

public sealed partial class TerminalSession
{
    /// <summary>Captures an archive without replacing the parser or command ownership of this shell.</summary>
    public byte[] CaptureSnapshot()
    {
        lock (_renderGate) using (_gate.Enter())
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            return _terminal.ExportSnapshot();
        }
    }
}
