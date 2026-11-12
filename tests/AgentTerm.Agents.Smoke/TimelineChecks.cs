using AgentTerm.Agents;

static class TimelineChecks
{
    public static void Run()
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        var timeline = new AgentTimeline();
        var turn = Guid.NewGuid();
        timeline.Message(turn, MessageRole.User, "bounded calls");
        for (var i = 0; i < 300; i++)
        {
            timeline.Apply(new ToolStarted(turn, i.ToString(), "fixture", ToolCategory.Command, "command " + i, "command " + i));
            timeline.Apply(new ToolOutput(turn, i.ToString(), new string('x', 20000)));
            timeline.Apply(new ToolCompleted(turn, i.ToString(), WorkStatus.Succeeded));
        }
        var groups = timeline.Entries.OfType<TimelineWorkGroup>().ToArray();
        Check(timeline.HasTrimmed && groups.Sum(g => g.Tools.Count) <= 256 && groups.All(g => g.Tools.Count <= 64), "Tool retention is bounded and omission is visible");
        Check(groups.SelectMany(g => g.Tools).All(t => t.Output.Length <= AgentTimeline.TextLimit), "Every tool output is bounded");
        timeline.Apply(new ToolOutput(turn, "0", "late old output"));
        Check(!groups.SelectMany(g => g.Tools).Any(t => t.Output.Contains("late old output")), "Trimmed call output cannot overwrite retained calls");
        var next = Guid.NewGuid();
        timeline.Message(next, MessageRole.User, "next turn");
        timeline.Apply(new ToolStarted(next, "0", "fixture", ToolCategory.Command, "next", "next"));
        timeline.Apply(new ToolCompleted(next, "0", WorkStatus.Cancelled));
        Check(timeline.Entries.OfType<TimelineMessage>().Any(m => m.Role == MessageRole.Cancelled), "Cancellation stays visible outside the group and distinct from failure");
        timeline.Apply(new ReasoningSummary(next, "A public harness summary"));
        var thinking = timeline.Entries.OfType<TimelineWorkGroup>().Last();
        thinking.Expanded = true;
        timeline.Apply(new ReasoningSummary(next, "Updated public summary"));
        Check(thinking.Expanded && thinking.Reasoning == "Updated public summary", "Supported summaries preserve expansion without inventing reasoning");

        var blank = Guid.NewGuid();
        timeline.Message(blank, MessageRole.User, "blank replies");
        var before = timeline.Entries.Count;
        timeline.Apply(new TextDelta(blank, ""));
        timeline.Apply(new TextDelta(blank, " \n"));
        Check(timeline.Entries.Count == before, "Blank assistant text creates no reply row");
        timeline.Apply(new TextDelta(blank, "real reply"));
        timeline.Apply(new TextDelta(blank, "\n"));
        Check(timeline.Entries.Last() is TimelineMessage { Role: MessageRole.Assistant, Text: " \nreal reply\n" }, "Whitespace inside a streaming reply is kept");
        timeline.Apply(new ToolStarted(blank, "late", "fixture", ToolCategory.Command, "late", "late"));
        timeline.Apply(new TextDelta(blank, "    "));
        timeline.Apply(new ToolStarted(blank, "boundary", "fixture", ToolCategory.Command, "boundary", "boundary"));
        timeline.Apply(new TextDelta(blank, "after boundary"));
        Check(timeline.Entries.Last() is TimelineMessage { Text: "after boundary" }, "Blank-only tool-boundary content is not prepended to a later reply");
        timeline.Apply(new ToolExecuting(blank, "missing"));
        var late = timeline.Entries.OfType<TimelineWorkGroup>().Last();
        Check(late.Summary == "Pending boundary" && !late.Tools[0].Executing, "Execution for another call does not mark this one");
    }
}

static class TaskChecks
{
    public static async Task RunAsync(Func<AgentSessionController> create, AgentLocation location, Func<Func<bool>, Task> wait)
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception("Tasks: " + message); }
        await using var controller = create();
        var first = controller.BeginTask();
        Check(first.Title == "" && controller.CurrentTask == first, "bare /agent opens an empty task");
        Check(await controller.SubmitAsync("size of downloads" + (char)10 + "details", location), "first request");
        Check(first.Title == "size of downloads" && first.Turns.Count == 1 && controller.RunningTask == first, "title is the first request line");
        Check(controller.Timeline.Entries.OfType<TimelineMessage>().Any(m => m.Role == MessageRole.User && first.Contains(m.Turn)), "the request renders in its task");
        Check(!await controller.SubmitAsync("busy", location), "no second turn while running");
        await controller.InterruptAsync();
        await wait(() => controller.State == AgentState.Idle);
        var second = controller.BeginTask();
        controller.ActivateTask(first);
        Check(controller.CurrentTask == first, "reopening a card routes follow-ups to that task");
        Check(controller.Tasks.SequenceEqual([first, second]), "tasks keep invocation order");
        for (var i = 0; i < AgentSessionController.TaskLimit + 3; i++) controller.BeginTask();
        Check(controller.Tasks.Count == AgentSessionController.TaskLimit && !controller.Tasks.Contains(first), "task history is bounded");
    }
}
