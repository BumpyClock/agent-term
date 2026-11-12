using AgentTerm_App;

internal static class PromptFocusChecks
{
    internal static void Run()
    {
        // Expected owners come from the user's interaction. Shell state and display mode never move input:
        // the terminal owns typing in Smart and Legacy, and only the open agent surface owns its reply field.
        Check(PromptFocusPolicy.Owner(agentSurfaceOpen: false) == PromptInputOwner.Terminal,
            "With the agent surface closed, keystrokes belong to the shell's own prompt.");
        Check(PromptFocusPolicy.Owner(agentSurfaceOpen: true) == PromptInputOwner.Composer,
            "With the agent surface open, keystrokes belong to the agent reply field.");

        var terminal = PromptInputOwner.Terminal;
        var composer = PromptInputOwner.Composer;
        Check(PromptFocusPolicy.ShouldTransfer(terminal, composer, true, true, true),
            "Opening the surface from this pane's focused terminal must move typing to the reply field.");
        Check(PromptFocusPolicy.ShouldTransfer(composer, terminal, true, true, true),
            "Leaving the surface from a focused reply field must return typing to the terminal.");
        foreach (var (before, after) in new[] { (terminal, composer), (composer, terminal) })
        {
            Check(!PromptFocusPolicy.ShouldTransfer(before, after, false, true, true),
                "A background tab or inactive split pane must not acquire focus.");
            Check(!PromptFocusPolicy.ShouldTransfer(before, after, true, true, false),
                "Buttons, search, other panes and tab headers retain intentional focus.");
            Check(!PromptFocusPolicy.ShouldTransfer(before, after, true, false, true),
                "Composition or search must not be interrupted.");
        }
        Check(!PromptFocusPolicy.ShouldTransfer(composer, composer, true, true, true),
            "Agent progress refresh must not reassert focus.");
        Check(!PromptFocusPolicy.ShouldTransfer(terminal, terminal, true, true, true),
            "Shell readiness and output refresh must not reassert focus.");
        Console.WriteLine("Prompt focus policy checks passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
