namespace AgentTerm.Terminal;

public sealed partial class TerminalSession
{
    public void SetOverscanEnabled(bool enabled) => MutateView(() => _terminal.SetOverscanEnabled(enabled));
}
