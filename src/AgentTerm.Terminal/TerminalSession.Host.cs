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
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            _terminal.SetHostCapabilities(capabilities);
            DrainHostResponses();
        }
    }

    public string ReadDiagnosticReport()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            return _terminal.ReadDiagnosticReport();
        }
    }
}
