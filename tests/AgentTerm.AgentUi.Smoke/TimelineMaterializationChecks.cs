using AgentTerm.Agents;
using AgentTerm_App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

internal static class TimelineMaterializationChecks
{
    public static async Task RunAsync(Window window)
    {
        var harness = new Program.Harness();
        await using var controller = new AgentSessionController(harness);
        await controller.SubmitAsync("Timeline materialization fixture", new(Environment.CurrentDirectory));
        var turn = controller.CurrentTask!.Turns.Single();
        for (var i = 0; i < 64; i++) controller.Timeline.Apply(new ToolStarted(turn, i.ToString(), "command", ToolCategory.Command, "Run command", "echo " + i));
        var view = new AgentTimelineView();
        window.Content = view;
        window.Activate();
        await Program.WaitAsync(() => view.IsLoaded);
        view.Update(controller, controller.CurrentTask, true);
        Check(Tools(view).Length == 0, "Collapsed multi-tool groups must not construct hidden tool cards.");
        var groupHeader = LogicalChildren(view).OfType<DisclosureButton>().Single();
        groupHeader.Toggle();
        var tools = Tools(view);
        Check(tools.Length == 64, "Expanding creates every retained tool control.");
        tools[0].Toggle();
        controller.Timeline.Apply(new ToolOutput(turn, "0", "first output"));
        view.Update(controller, controller.CurrentTask, true);
        Check(LogicalChildren(view).OfType<TextBlock>().Any(t => t.Text == "first output"), "Expanded cards show streamed output.");
        groupHeader.Toggle();
        controller.Timeline.Apply(new ToolOutput(turn, "0", " plus hidden output"));
        controller.Timeline.Apply(new ToolCompleted(turn, "0", WorkStatus.Succeeded));
        view.Update(controller, controller.CurrentTask, true);
        groupHeader.Toggle();
        Check(LogicalChildren(view).OfType<TextBlock>().Any(t => t.Text == "first output plus hidden output"), "Reopening a group shows changes received while collapsed.");
        Check(tools.SequenceEqual(Tools(view)), "Expansion and unchanged updates reuse controls and disclosure state.");
        for (var i = 0; i < 20; i++) view.Update(controller, controller.CurrentTask, true);
        Check(tools.SequenceEqual(Tools(view)), "Unchanged updates must not reconstruct cards.");
        var firstApproval = Guid.NewGuid();
        var secondApproval = Guid.NewGuid();
        harness.Session.Emit(new ApprovalRequest(turn, firstApproval, "first permission", "1"));
        harness.Session.Emit(new ApprovalRequest(turn, secondApproval, "second permission", "2"));
        await Program.WaitAsync(() => controller.AwaitsApproval("2"));
        view.Update(controller, controller.CurrentTask, true);
        Check(AutomationProperties.GetName(tools[1]).EndsWith("awaiting approval", StringComparison.Ordinal), "Queued approvals appear on the corresponding tool.");
        await controller.RespondAsync(new(firstApproval, Approved: true));
        view.Update(controller, controller.CurrentTask, true);
        Check(AutomationProperties.GetName(tools[1]).EndsWith("pending", StringComparison.Ordinal) &&
            AutomationProperties.GetName(tools[2]).EndsWith("awaiting approval", StringComparison.Ordinal), "Resolving an earlier approval updates its card even while the last gated call is unchanged.");
        await controller.RespondAsync(new(secondApproval, Approved: true));

        // A single tool is deliberately shown inline, even when its model has Expanded=false.
        controller.Timeline.Message(turn, MessageRole.Assistant, "next step");
        controller.Timeline.Apply(new ToolStarted(turn, "single", "command", ToolCategory.Command, "Run single", "echo single"));
        view.Update(controller, controller.CurrentTask, true);
        Check(Tools(view).Length == 65, "Single-tool groups remain visible without an extra disclosure action.");
        window.Content = null;
        Console.WriteLine("Timeline materialization checks passed. Fresh collapsed 64-tool group realizes 0 tool cards; expansion realizes 64. Hidden updates, unchanged controls, single-tool visibility preserved.");
    }

    private static DisclosureButton[] Tools(DependencyObject root) => LogicalChildren(root).OfType<DisclosureButton>()
        .Where(button => button.ContextFlyout is MenuFlyout menu && menu.Items.OfType<MenuFlyoutItem>().Any(item => item.Text == "Copy command")).ToArray();

    // Traverse owned content, including collapsed controls whose templates were never loaded.
    private static IEnumerable<DependencyObject> LogicalChildren(DependencyObject root)
    {
        yield return root;
        IEnumerable<DependencyObject> children = root switch
        {
            Panel panel => panel.Children,
            Border { Child: { } child } => [child],
            ContentControl { Content: DependencyObject child } => [child],
            _ => [],
        };
        foreach (var child in children)
            foreach (var descendant in LogicalChildren(child)) yield return descendant;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
