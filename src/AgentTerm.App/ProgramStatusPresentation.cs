using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AgentTerm.Terminal;

namespace AgentTerm_App;

/// <summary>One report from one pane. Title is the pane's program-set terminal title, when it has one.</summary>
internal sealed record PaneProgramStatus(int Pane, string Shell, ProgramStatus Status, string? Title = null)
{
    /// <summary>The pane as its tab names it: the program title, else the shell profile.</summary>
    internal string Label => Title ?? Shell;

    /// <summary>The shell, when a program title replaced it in the label.</summary>
    internal string? ShellDetail => Title is null || Title.Equals(Shell, StringComparison.OrdinalIgnoreCase) ? null : Shell;
}

internal sealed record ProgramStatusPresentation(string Glyph, string Summary, string Details)
{
    internal static ProgramStatusPresentation Create(IReadOnlyList<PaneProgramStatus> records)
    {
        if (records.Count == 0) return new("", "", "");
        var primary = ActivePrimary(records);
        var glyph = primary is null ? "" : GlyphFor(primary);
        var details = new StringBuilder();
        foreach (var pane in records.GroupBy(record => record.Pane))
        {
            if (details.Length > 0) details.AppendLine();
            details.AppendLine(PaneHeading(pane.First()));
            foreach (var entry in pane.OrderBy(record => record.Status.Id, Comparer<string>.Create(CompareIds)))
            {
                var status = entry.Status;
                var depth = status.Id.Length == 0 ? 0 : status.Id.Count(c => c == '/') + 1;
                var indent = new string(' ', depth * 2);
                var name = Name(status);
                details.AppendLine($"{indent}{name} · {Label(status)}");
                if (OwnApp(status) is { } app && app != name) details.AppendLine($"{indent}  {app}");
                if (status.Id.Length > 0) details.AppendLine($"{indent}  {status.Id}");
                details.AppendLine($"{indent}  Updated {status.UpdatedAt.ToLocalTime():HH:mm:ss}");
                if (status.Message is { Length: > 0 } message) details.AppendLine($"{indent}  {Clean(message)}");
            }
        }
        return new(glyph, Label(primary ?? records.OrderBy(record => Priority(record.Status.State)).First().Status), details.ToString().TrimEnd());
    }

    internal static string PaneHeading(PaneProgramStatus record) =>
        record.ShellDetail is { } shell ? $"Pane {record.Pane} · {Clean(record.Label)} · {shell}" : $"Pane {record.Pane} · {Clean(record.Label)}";

    internal static string GlyphFor(ProgramStatus status) => status.State switch
    {
        ProgramState.Blocked => status.Kind is "permission" or "auth" ? "\uE72E" : "\uE9CE",
        ProgramState.Error => "\uEA39",
        ProgramState.Working => "\uE916",
        ProgramState.Done => "\uE73E",
        _ => "\uECCA"
    };

    // Reported app values are CLI names; people know these agents by their product names.
    private static readonly Dictionary<string, string> AppNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = "Claude Code", ["claude-code"] = "Claude Code",
        ["codex"] = "Codex", ["codex-cli"] = "Codex",
        ["gemini"] = "Gemini CLI", ["gemini-cli"] = "Gemini CLI",
        ["copilot"] = "GitHub Copilot CLI", ["copilot-cli"] = "GitHub Copilot CLI", ["github-copilot"] = "GitHub Copilot CLI",
        ["cursor"] = "Cursor Agent", ["cursor-agent"] = "Cursor Agent",
        ["opencode"] = "OpenCode",
        ["qwen"] = "Qwen Code", ["qwen-code"] = "Qwen Code",
        ["cline"] = "Cline",
        ["pi"] = "Pi",
        ["agentterm"] = "AgentTerm",
    };

    internal static string AppName(string app) => AppNames.GetValueOrDefault(app, app);

    /// <summary>The app a record reports itself, or the one a session root inherits. Nested tasks do not repeat their parent's app.</summary>
    internal static string? IdentityApp(ProgramStatus status) => status.App ?? (status.Id.Length == 0 ? status.EffectiveApp : null);

    internal static string? OwnApp(ProgramStatus status) => status.App is { } app ? Clean(AppName(app)) : null;

    /// <summary>Title, then product name, then the last id segment, since nesting already shows the parent path.</summary>
    internal static string Name(ProgramStatus status) => Clean(status.Title
        ?? (IdentityApp(status) is { } app ? AppName(app) : null)
        ?? (status.Id.Length == 0 ? "Program" : status.Id[(status.Id.LastIndexOf('/') + 1)..]));

    internal static ProgramStatus? ActivePrimary(IReadOnlyList<PaneProgramStatus> records) => ActivePrimary(records.Select(record => record.Status));

    internal static ProgramStatus? ActivePrimary(IEnumerable<ProgramStatus> records) => records
        .Where(record => record.IsActive)
        .OrderBy(record => Priority(record.State)).FirstOrDefault();

    internal static int CompareIds(string left, string right)
    {
        var a = left.Split('/');
        var b = right.Split('/');
        for (var index = 0; index < Math.Min(a.Length, b.Length); index++)
        {
            var comparison = StringComparer.Ordinal.Compare(a[index], b[index]);
            if (comparison != 0) return comparison;
        }
        return a.Length.CompareTo(b.Length);
    }

    internal static int Priority(ProgramState state) => state switch
    {
        ProgramState.Blocked => 0, ProgramState.Error => 1, ProgramState.Working => 2,
        ProgramState.Done => 3, _ => 4
    };

    internal static string Label(ProgramStatus status)
    {
        var label = status.State == ProgramState.Blocked ? status.Kind switch
        {
            "permission" => "Approval needed", "question" => "Answer needed", "auth" => "Sign-in needed", _ => "Blocked"
        } : status.State.ToString();
        return status.Progress is { } progress ? $"{label} · {progress}%" : label;
    }

    internal static string Clean(string text) => string.Concat(text.Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.Format));
}
