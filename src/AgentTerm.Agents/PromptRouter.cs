namespace AgentTerm.Agents;

public abstract record PromptIntent;
/// <summary>Text for the pane's shell, unchanged.</summary>
public sealed record ShellIntent(string Command) : PromptIntent;
/// <summary>An explicit agent invocation. An empty request opens the agent surface without starting a turn.</summary>
public sealed record AgentIntent(string Request) : PromptIntent;

/// <summary>
/// Agent use is explicit. Only a leading <c>/agent</c> token routes away from the shell, so ordinary commands,
/// unknown names and prose never wait on a classifier. <c>/agentx</c> and <c> /agent</c> stay shell text.
/// </summary>
public static class PromptRouter
{
    public const string AgentCommand = "/agent";
    public static PromptIntent Parse(string text)
    {
        if (!text.StartsWith(AgentCommand, StringComparison.Ordinal)) return new ShellIntent(text);
        var rest = text[AgentCommand.Length..];
        if (rest.Length > 0 && !char.IsWhiteSpace(rest[0])) return new ShellIntent(text);
        return new AgentIntent(rest.Trim());
    }
}
