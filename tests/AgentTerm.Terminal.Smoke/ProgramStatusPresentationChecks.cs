using AgentTerm.Terminal;
using AgentTerm_App;

internal static class ProgramStatusPresentationChecks
{
    internal static void Run()
    {
        var now = DateTimeOffset.UtcNow;
        PaneProgramStatus Entry(ProgramState state, string id = "", int pane = 1, string? kind = null) =>
            new(pane, "PowerShell", new(id, state, kind, null, "claude-code", "claude-code", null, null, now));
        var entries = new[] { Entry(ProgramState.Idle), Entry(ProgramState.Done), Entry(ProgramState.Working), Entry(ProgramState.Error), Entry(ProgramState.Blocked, kind: "question") };
        var labels = new[] { "Idle", "Done", "Working", "Error", "Answer needed" };
        for (var count = 1; count <= entries.Length; count++)
        {
            var presentation = ProgramStatusPresentation.Create(entries.Take(count).ToArray());
            Check(presentation.Summary == labels[count - 1], "The tab must show its highest priority record.");
            Check(presentation.Glyph.Length > 0, "Every known state needs a visible tab symbol.");
        }
        var parent = Entry(ProgramState.Working);
        var child = Entry(ProgramState.Blocked, "task/review", kind: "permission");
        child = child with { Status = child.Status with { Title = "Re\u202eview", Message = "Approve\u2066 changes", Progress = 42 } };
        var otherPane = Entry(ProgramState.Done, pane: 2);
        Check(ProgramStatusPresentation.Age(now, now) == "just now" && ProgramStatusPresentation.Age(now - TimeSpan.FromMinutes(4), now) == "4m ago" &&
            ProgramStatusPresentation.Age(now - TimeSpan.FromHours(3), now) == "3h ago", "Report times must read as relative ages.");
        var details = ProgramStatusPresentation.Create([child, parent, otherPane]);
        Check(details.Summary == "Approval needed · 42%", "Blocked detail must retain reason and percentage.");
        Check(details.Details.Contains("Pane 1 · PowerShell") && details.Details.Contains("Pane 2 · PowerShell"), "Split reports must identify their source pane.");
        Check(details.Details.Contains("    Review · Approval needed · 42%") && details.Details.Contains("task/review"), "Child task titles and ids must retain hierarchy.");
        Check(!details.Details.Contains("Updated"), "Tab details rebuild only on report changes, so they must not carry an age that goes stale.");
        Check(details.Details.Contains("Approve changes") && !details.Details.Contains('\u202e') && !details.Details.Contains('\u2066'), "Direction overrides must not escape into tab details.");
        Check(details.Details.IndexOf("Claude Code · Working", StringComparison.Ordinal) >= 0 && !details.Details.Contains("claude-code"), "Known agents must use their product name, and nested tasks must not repeat the parent app.");
        Check(details.Details.IndexOf("Claude Code · Working", StringComparison.Ordinal) < details.Details.IndexOf("Review ·", StringComparison.Ordinal), "Parent must precede child even when reports arrive in reverse order.");
        var titled = ProgramStatusPresentation.Create([parent with { Title = "Fix login bug" }, otherPane with { Title = "PowerShell" }]).Details;
        Check(titled.Contains("Pane 1 · Fix login bug · PowerShell") && titled.Contains("Pane 2 · PowerShell") && !titled.Contains("PowerShell · PowerShell"),
            "Panes must use the tab's program title and keep the shell only when the title replaced it.");
        var hierarchy = ProgramStatusPresentation.Create(new[] { "build", "build-ui", "build.x", "build/test" }
            .Select(id => Entry(ProgramState.Working, id) with { Status = Entry(ProgramState.Working, id).Status with { Title = id } }).ToArray()).Details;
        Check(hierarchy.IndexOf("build/test ·", StringComparison.Ordinal) < hierarchy.IndexOf("build-ui ·", StringComparison.Ordinal)
            && hierarchy.IndexOf("build/test ·", StringComparison.Ordinal) < hierarchy.IndexOf("build.x ·", StringComparison.Ordinal), "Child records must precede prefix-sharing siblings.");
        var retained = entries.Select(entry => entry with { Status = entry.Status with { IsActive = false } }).ToArray();
        var history = ProgramStatusPresentation.Create(retained);
        Check(history.Glyph == "" && history.Details.Contains("Answer needed"), "Retained outcomes must remain readable without replacing the shell icon.");
        var idle = Entry(ProgramState.Idle, pane: 2);
        Check(ProgramStatusPresentation.ActivePrimary([.. retained, idle]) == idle.Status, "An active pane must own identity instead of a retained urgent outcome.");
        var empty = ProgramStatusPresentation.Create([]);
        Check(empty.Glyph == "" && empty.Summary == "" && empty.Details == "", "No reports must restore an ordinary shell tab.");
        Console.WriteLine("Program status presentation checks passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
