namespace AgentTerm.Terminal;

public sealed partial class TerminalSession
{
    private void DrainHostResponses()
    {
        foreach (var reply in _terminal.TakeResponses())
            if (_disposeTask is null && _stopTask is null) _connection.SendReply(reply);
    }

    public void SetHostCapabilities(TerminalHostCapabilities capabilities)
    {
        using (_gate.Enter())
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            _terminal.SetHostCapabilities(capabilities);
            DrainHostResponses();
        }
    }

    public string ReadDiagnosticReport()
    {
        lock (_renderGate) using (_gate.Enter())
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            return _terminal.ReadDiagnosticReport();
        }
    }

    public bool UnknownSequenceDiagnosticsEnabled { get { using (_gate.Enter()) return _terminal.UnknownSequenceDiagnosticsEnabled; } }

    public void SetUnknownSequenceDiagnostics(bool enabled)
    {
        using (_gate.Enter())
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            _terminal.SetUnknownSequenceDiagnostics(enabled);
        }
    }
}
