using AgentTerm.Terminal;

namespace AgentTerm_App;

public sealed partial class MainPage
{
    internal byte[] CaptureArchiveSnapshot() => (_session ?? throw new System.InvalidOperationException("No terminal session is available to save.")).CaptureSnapshot();
}
