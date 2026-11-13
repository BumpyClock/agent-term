using AgentTerm.Terminal;

namespace AgentTerm_App;

public sealed partial class MainPage
{
    internal Task<byte[]> CaptureArchiveSnapshotAsync()
    {
        var session = _session ?? throw new System.InvalidOperationException("No terminal session is available to save.");
        return Task.Run(session.CaptureSnapshot);
    }
}
