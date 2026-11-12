using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AgentTerm.Terminal;

namespace AgentTerm_App;

internal sealed record PaneProgramStatus(int Pane, string Shell, ProgramStatus Status);

internal sealed record ProgramStatusPresentation(string Glyph, string Summary, string Details)
{
    internal static ProgramStatusPresentation Create(IReadOnlyList<PaneProgramStatus> records)
    {
        if (records.Count == 0) return new("", "", "");
        var primary = records.OrderBy(record => Priority(record.Status.State)).First().Status;
        var glyph = primary.State switch
        {
            ProgramState.Blocked => primary.Kind switch { "permission" => "\uE72E", "auth" => "\uE72E", _ => "\uE9CE" },
            ProgramState.Error => "\uEA39",
            ProgramState.Working => "\uE916",
            ProgramState.Done => "\uE73E",
            _ => "\uECCA"
        };
        var details = new StringBuilder();
        foreach (var pane in records.GroupBy(record => (record.Pane, record.Shell)))
        {
            if (details.Length > 0) details.AppendLine();
            details.AppendLine($"Pane {pane.Key.Pane} · {pane.Key.Shell}");
            foreach (var entry in pane.OrderBy(record => record.Status.Id, Comparer<string>.Create(CompareIds)))
            {
                var status = entry.Status;
                var depth = status.Id.Length == 0 ? 0 : status.Id.Count(c => c == '/') + 1;
                var indent = new string(' ', depth * 2);
                var name = Clean(status.Title ?? status.EffectiveApp ?? (status.Id.Length == 0 ? "Program" : status.Id));
                details.AppendLine($"{indent}{name} · {Label(status)}");
                if (status.EffectiveApp is { } app && app != name) details.AppendLine($"{indent}  {app}");
                if (status.Id.Length > 0) details.AppendLine($"{indent}  {status.Id}");
                details.AppendLine($"{indent}  Updated {status.UpdatedAt.ToLocalTime():HH:mm:ss}");
                if (status.Message is { Length: > 0 } message) details.AppendLine($"{indent}  {Clean(message)}");
            }
        }
        return new(glyph, Label(primary), details.ToString().TrimEnd());
    }

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
