namespace AgentTerm_App;

/// <summary>Composer is the agent surface's reply field. The terminal owns every other keystroke in both modes.</summary>
internal enum PromptInputOwner { Composer, Terminal }

internal static class PromptFocusPolicy
{
    internal static PromptInputOwner Owner(bool agentSurfaceOpen) =>
        agentSurfaceOpen ? PromptInputOwner.Composer : PromptInputOwner.Terminal;

    // An owner change can hand off this pane's input, never reclaim focus the user moved elsewhere.
    internal static bool ShouldTransfer(PromptInputOwner previous, PromptInputOwner next,
        bool active, bool navigationAllowed, bool inputFocused) =>
        previous != next && active && navigationAllowed && inputFocused;
}
