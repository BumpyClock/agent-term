using AgentTerm.Agents;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AgentTerm_App;

/// <summary>A collapsed agent task in terminal history. It reopens the same conversation.</summary>
internal sealed class AgentTaskCard : Button
{
    private readonly TextBlock _title = AgentUi.Text("AgentCommandText");
    private readonly ActivityLabel _status = new();
    private readonly FontIcon _mark = AgentUi.Icon(AgentUi.Cross, "AgentCriticalIcon");
    public AgentTask Task { get; }

    public AgentTaskCard(AgentTask task)
    {
        Task = task;
        Style = AgentUi.Style("AgentTaskCardStyle");
        _title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var agent = AgentUi.Icon(AgentUi.Agent, "AgentAccentIcon");
        agent.FontSize = 13;
        var chevron = AgentUi.Icon(AgentUi.Chevron);
        chevron.FontSize = 10;
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { _mark, _status, chevron } };
        var row = new Grid { ColumnSpacing = 10, ColumnDefinitions = { new() { Width = GridLength.Auto }, new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } } };
        row.Children.Add(agent);
        Grid.SetColumn(_title, 1); row.Children.Add(_title);
        Grid.SetColumn(trailing, 2); row.Children.Add(trailing);
        Content = row;
        ToolTipService.SetToolTip(this, "Open this agent task (Enter)");
    }

    public void Update(AgentSessionController controller, bool animate)
    {
        var running = controller.RunningTask == Task;
        var failed = controller.Timeline.Entries.OfType<TimelineMessage>().LastOrDefault(m => Task.Contains(m.Turn)) is { Role: MessageRole.Failure };
        var status = !running ? (failed ? "Failed" : "") : controller.State switch
        {
            AgentState.WaitingForApproval => "Needs approval",
            AgentState.WaitingForInput => "Needs an answer",
            AgentState.Interrupting => "Stopping",
            _ => "Working",
        };
        var title = Task.Title.Length > 0 ? Task.Title : "New agent task";
        if (_title.Text != title) _title.Text = title;
        _status.Update(status, animate && running && controller.State is AgentState.Running or AgentState.Starting);
        _status.Visibility = status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _mark.Visibility = failed && !running ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(this, $"Agent task: {title}{(status.Length > 0 ? ". " + status : "")}. Open conversation");
    }
}
