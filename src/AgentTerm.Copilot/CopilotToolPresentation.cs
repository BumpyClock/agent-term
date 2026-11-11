using System.Text.Json;
using System.Text.Json.Serialization;
using AgentTerm.Agents;

namespace AgentTerm.Copilot;

/// <summary>Allow-listed presentation fields, never a serialized tool envelope.</summary>
public static class CopilotToolPresentation
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    public static ToolStarted Start(Guid turn, string id, string name, object? arguments)
    {
        var command = "";
        if (name == "run_in_terminal" && arguments is not null)
        {
            var json = JsonSerializer.SerializeToElement(arguments);
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("command", out var value) && value.ValueKind == JsonValueKind.String) command = value.GetString() ?? "";
        }
        var action = name == "run_in_terminal" ? (command.Split(['\r', '\n'], 2)[0] is { Length: > 0 } line ? line[..Math.Min(line.Length, 80)] : "command") : "tool";
        return new(turn, id, name, name == "run_in_terminal" ? ToolCategory.Command : ToolCategory.Other, action, command);
    }
    public static (string Text, WorkStatus Status) Complete(string name, string? content, bool success)
    {
        if (name != "run_in_terminal") return (success ? "Tool completed." : "Tool failed.", success ? WorkStatus.Succeeded : WorkStatus.Failed);
        try
        {
            var result = JsonSerializer.Deserialize<TerminalExecutionResult>(content ?? "", Options);
            if (result is null) return ("No terminal result available.", WorkStatus.Failed);
            var status = result.Status switch
            {
                TerminalExecutionStatus.Interrupted or TerminalExecutionStatus.Denied => WorkStatus.Cancelled,
                TerminalExecutionStatus.Completed when success && result.Succeeded != false => WorkStatus.Succeeded,
                _ => WorkStatus.Failed,
            };
            var text = result.Output;
            if (result.CurrentDirectory is { } cwd) text += $"\nDirectory · {cwd}";
            if (result.ExitCode is { } code) text += $"\nExit code · {code}";
            if (result.Message is { } message) text += "\n" + message;
            if (result.Truncated) text += "\n[Output truncated]";
            if (status != WorkStatus.Succeeded && result.Message is null) text += "\n" + result.Status switch
            {
                TerminalExecutionStatus.Busy => "The terminal was busy.",
                TerminalExecutionStatus.Unsupported => "The terminal could not run agent commands.",
                TerminalExecutionStatus.Exited => "The shell exited.",
                TerminalExecutionStatus.Interrupted => "Interrupted.",
                TerminalExecutionStatus.InterruptionFailed => "Interruption failed.",
                TerminalExecutionStatus.Denied => "Denied.",
                _ => result.Succeeded == false ? "The command reported failure." : "",
            };
            return (text.Trim(), status);
        }
        catch (JsonException) { return ("Terminal result unavailable.", WorkStatus.Failed); }
    }
}
