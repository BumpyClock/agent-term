namespace AgentTerm.Agents;

public enum ToolCategory { Command, ReadFile, WriteFile, Other }
public enum WorkStatus { Running, Succeeded, Failed, Cancelled }
public enum MessageRole { User, Assistant, Failure, Cancelled, Notice }
public abstract class TimelineEntry(Guid turn)
{
    public Guid Id { get; } = Guid.NewGuid();
    public Guid Turn { get; } = turn;
}
public sealed class TimelineMessage(Guid turn, MessageRole role, string text) : TimelineEntry(turn)
{
    public MessageRole Role { get; } = role;
    public string Text { get; private set; } = AgentTimeline.Bound(text);
    internal void Append(string text) { Text = AgentTimeline.Bound(Text + text); }
}
public sealed class TimelineTool(string callId, ToolCategory category, string action, string command)
{
    public string CallId { get; } = callId;
    public ToolCategory Category { get; } = category;
    public string Action { get; } = AgentTimeline.Bound(action);
    public string Command { get; } = AgentTimeline.Bound(command);
    public string Output { get; internal set; } = "";
    public WorkStatus Status { get; internal set; } = WorkStatus.Running;
    /// <summary>The harness reported actual execution. A running call without it may still wait for approval or input.</summary>
    public bool Executing { get; internal set; }
    public bool OutputExpanded { get; set; }
}
public sealed class TimelineWorkGroup(Guid turn) : TimelineEntry(turn)
{
    private readonly List<TimelineTool> _tools = [];
    public IReadOnlyList<TimelineTool> Tools => _tools;
    public bool Expanded { get; set; }
    public bool Active => _tools.Any(t => t.Status == WorkStatus.Running);
    public string? Reasoning { get; internal set; }
    public string Summary
    {
        get
        {
            var counts = new List<string>();
            foreach (var category in Enum.GetValues<ToolCategory>())
            {
                var n = _tools.Count(t => t.Category == category);
                if (n > 0) counts.Add(category switch
                {
                    ToolCategory.Command => $"Ran {n} command{(n == 1 ? "" : "s")}",
                    ToolCategory.ReadFile => $"Read {n} file{(n == 1 ? "" : "s")}",
                    ToolCategory.WriteFile => $"Changed {n} file{(n == 1 ? "" : "s")}",
                    _ => $"Used {n} tool{(n == 1 ? "" : "s")}",
                });
            }
            var settled = counts.Count > 1 ? string.Join(", ", counts[..^1]) + " and " + char.ToLowerInvariant(counts[^1][0]) + counts[^1][1..] : counts.FirstOrDefault() ?? "";
            var active = _tools.LastOrDefault(t => t.Status == WorkStatus.Running);
            var label = active is null ? settled : active.Executing ? $"Running {active.Action}" : $"Pending {active.Action}";
            var failed = _tools.Count(t => t.Status == WorkStatus.Failed);
            var cancelled = _tools.Count(t => t.Status == WorkStatus.Cancelled);
            if (failed > 0) label += $", {failed} failed";
            if (cancelled > 0) label += $", {cancelled} cancelled";
            return label;
        }
    }
    internal void Add(TimelineTool tool) { _tools.Add(tool); }
}

/// <summary>One /agent invocation and its follow-ups: a view over timeline turns. The app pairs it with a history anchor.</summary>
public sealed class AgentTask
{
    private readonly List<Guid> _turns = [];
    public Guid Id { get; } = Guid.NewGuid();
    /// <summary>The first request's first line. Empty until a request is sent.</summary>
    public string Title { get; private set; } = "";
    public IReadOnlyList<Guid> Turns => _turns;
    public WorkStatus? LastOutcome { get; internal set; }
    public bool Contains(Guid turn) => _turns.Contains(turn);
    internal void Add(Guid turn, string request)
    {
        var line = request.Trim().Split((char)10)[0].Trim();
        if (Title.Length == 0) Title = line.Length > 120 ? line[..119] + "…" : line;
        _turns.Add(turn);
        LastOutcome = null;
    }
}

/// <summary>Harness-neutral, bounded presentation state. Call on the controller's context.</summary>
public sealed class AgentTimeline
{
    public const int TextLimit = 8192;
    public const int EntryLimit = 128;
    private readonly List<TimelineEntry> _entries = [];
    public IReadOnlyList<TimelineEntry> Entries => _entries;
    public bool HasTrimmed { get; private set; }
    private const string Omission = "[Earlier text omitted]\n";
    internal static string Bound(string text) => text.Length <= TextLimit ? text : Omission + text[^(TextLimit - Omission.Length)..];
    public void Message(Guid turn, MessageRole role, string text)
    {
        if (role == MessageRole.Assistant && _entries.LastOrDefault() is TimelineMessage last && last.Turn == turn && last.Role == role) last.Append(text);
        // Harnesses send empty or blank assistant messages around tool calls. They are not replies and get no row.
        else if (role != MessageRole.Assistant || !string.IsNullOrWhiteSpace(text)) Add(new TimelineMessage(turn, role, text));
    }
    private void Add(TimelineEntry entry)
    {
        _entries.Add(entry);
        while (_entries.Count > EntryLimit || _entries.OfType<TimelineWorkGroup>().Sum(g => g.Tools.Count) > 256) { _entries.RemoveAt(0); HasTrimmed = true; }
    }
    public void Apply(AgentEvent item)
    {
        switch (item)
        {
            case TextDelta text: Message(text.Turn, MessageRole.Assistant, text.Text); break;
            case ToolStarted start:
                if (Find(start.Turn, start.CallId) is not null) break;
                var group = _entries.LastOrDefault(e => e is TimelineWorkGroup || e is TimelineMessage { Role: MessageRole.User or MessageRole.Assistant }) as TimelineWorkGroup;
                if (group is null || group.Turn != start.Turn || group.Tools.Count >= 64) { group = new(start.Turn); Add(group); }
                group.Add(new(start.CallId, start.Category, start.Action, start.Detail));
                while (_entries.OfType<TimelineWorkGroup>().Sum(g => g.Tools.Count) > 256) { _entries.RemoveAt(0); HasTrimmed = true; }
                break;
            case ToolOutput output:
                if (Find(output.Turn, output.CallId) is { } found) { found.Tool.Output = Bound(found.Tool.Output + output.Text); }
                break;
            case ToolExecuting executing:
                if (Find(executing.Turn, executing.CallId) is { } executed) executed.Tool.Executing = true;
                break;
            case ToolCompleted completed:
                if (Find(completed.Turn, completed.CallId) is { } call)
                {
                    call.Tool.Status = completed.Status;
                    if (completed.Status is WorkStatus.Failed or WorkStatus.Cancelled)
                        Message(item.Turn, completed.Status == WorkStatus.Cancelled ? MessageRole.Cancelled : MessageRole.Failure,
                            (completed.Status == WorkStatus.Cancelled ? $"{call.Tool.Action} was cancelled" : $"{call.Tool.Action} failed") + (call.Tool.Output.Length > 0 ? "\n" + call.Tool.Output : ""));
                }
                break;
            case ReasoningSummary summary:
                var work = _entries.LastOrDefault() as TimelineWorkGroup;
                if (work is null || work.Turn != item.Turn) { work = new(item.Turn); Add(work); }
                work.Reasoning = Bound(summary.Text); break;
        }
    }
    private (TimelineWorkGroup Group, TimelineTool Tool)? Find(Guid turn, string id)
    {
        foreach (var group in _entries.OfType<TimelineWorkGroup>().Where(g => g.Turn == turn))
            if (group.Tools.FirstOrDefault(t => t.CallId == id) is { } tool) return (group, tool);
        return null;
    }
    public void Settle(Guid turn)
    {
        foreach (var group in _entries.OfType<TimelineWorkGroup>().Where(g => g.Turn == turn))
        { foreach (var tool in group.Tools.Where(t => t.Status == WorkStatus.Running)) tool.Status = WorkStatus.Cancelled; }
    }
}
