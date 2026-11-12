namespace AgentTerm_App;

internal enum PromptInputOwner { Composer, Terminal }

internal static class PromptFocusPolicy
{
    internal static PromptInputOwner Owner(bool agentSurfaceOpen) =>
        agentSurfaceOpen ? PromptInputOwner.Composer : PromptInputOwner.Terminal;

    internal static bool ShouldTransfer(PromptInputOwner previous, PromptInputOwner next,
        bool active, bool navigationAllowed, bool inputFocused) =>
        previous != next && active && navigationAllowed && inputFocused;
}
