using AgentTerm.Agents;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AgentTerm_App;

public sealed partial class AgentRequestPanel : UserControl
{
    private AgentSessionController? _agent;
    private Control? _reply;
    private bool _applied;

    public AgentRequestPanel() => InitializeComponent();

    /// <summary>The request this view may answer. Null while the pending request belongs to another task.</summary>
    internal AgentEvent? Shown { get; private set; }
    /// <summary>Raised by Show task. The host opens the request's owning task.</summary>
    internal event Action? ShowTaskRequested;
    private string? _notice;
    internal FontFamily DetailFont { set => RequestText.FontFamily = value; }

    internal void Attach(AgentSessionController agent, Control reply)
    {
        _agent = agent;
        _reply = reply;
    }

    /// <param name="viewed">The task whose view hosts this panel. Another task's request shows only a notice, never its actions.</param>
    internal void Update(AgentTask? viewed)
    {
        var request = _agent?.RequestFor(viewed);
        var notice = request is null && _agent?.Request is { } pending
            ? $"{(_agent.RunningTask?.Title is { Length: > 0 } title ? $"“{title}”" : "Another task")} {(pending is ApprovalRequest ? "is waiting for your approval." : "is waiting for your answer.")}"
            : null;
        if (_applied && request == Shown && notice == _notice) return;
        _applied = true;
        if ((request != Shown || (notice is null) != (_notice is null))
            && _reply is not null && XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused && Contains(focused))
            _reply.Focus(focused is Control { FocusState: FocusState.Keyboard } ? FocusState.Keyboard : FocusState.Programmatic);
        _notice = notice;
        if (request != Shown)
        {
            Shown = request;
            RequestChoices.Children.Clear();
            if (request is QuestionRequest question)
                foreach (var choice in question.Choices)
                {
                    var button = new Button { Content = new TextBlock { Text = choice, TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 3, 10, 3), MinHeight = 0 };
                    button.Click += async (_, _) => await RespondAsync(new(question.Request, Answer: choice));
                    RequestChoices.Children.Add(button);
                }
        }
        var approval = request as ApprovalRequest;
        var asked = request as QuestionRequest;
        Visibility = request is null && notice is null ? Visibility.Collapsed : Visibility.Visible;
        RequestTitle.Text = notice ?? (approval is not null ? "Allow the agent to run this?" : asked?.Question ?? "");
        RequestText.Text = approval?.Description ?? "";
        RequestDetail.Visibility = approval is not null ? Visibility.Visible : Visibility.Collapsed;
        ApproveRequest.Visibility = DenyRequest.Visibility = approval is not null ? Visibility.Visible : Visibility.Collapsed;
        ShowOwner.Visibility = notice is not null ? Visibility.Visible : Visibility.Collapsed;
        RequestChoices.Visibility = asked?.Choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal Task ApproveAsync() => Shown is ApprovalRequest request ? RespondAsync(new(request.Request, true)) : Task.CompletedTask;
    internal Task DenyAsync() => Shown is ApprovalRequest request ? RespondAsync(new(request.Request)) : Task.CompletedTask;
    private Task RespondAsync(AgentResponse response) => _agent?.RespondAsync(response) ?? Task.CompletedTask;

    private bool Contains(DependencyObject element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, this)) return true;
        return false;
    }

    private async void Approval_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        if (e.Key != Windows.System.VirtualKey.C || !ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
            || shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        e.Handled = true;
        await DenyAsync();
    }

    private async void ApproveRequest_Click(object sender, RoutedEventArgs e) => await ApproveAsync();
    private async void DenyRequest_Click(object sender, RoutedEventArgs e) => await DenyAsync();
    private void ShowOwner_Click(object sender, RoutedEventArgs e) => ShowTaskRequested?.Invoke();
}

public sealed partial class RequestHeaderLayout : Panel
{
    private const double MinimumTitleWidth = 120;
    private const double ColumnSpacing = 10;
    private const double ActionSpacing = 6;
    private const double RowSpacing = 8;

    private enum Arrangement { Inline, ActionRow, Stacked }

    private double _actionsWidth;

    private Arrangement Current { get; set; }

    protected override Size MeasureOverride(Size available)
    {
        var (icon, title, actions) = Parts();
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        icon.Measure(unbounded);
        foreach (var action in actions) action.Measure(unbounded);
        _actionsWidth = actions.Sum(action => action.DesiredSize.Width) + ActionSpacing * Math.Max(0, actions.Length - 1);
        var lead = icon.DesiredSize.Width + ColumnSpacing;
        var inlineActions = actions.Length == 0 ? 0 : ColumnSpacing + _actionsWidth;
        if (double.IsPositiveInfinity(available.Width))
        {
            title.Measure(unbounded);
            Current = Arrangement.Inline;
            return new(lead + title.DesiredSize.Width + inlineActions, Math.Max(Math.Max(icon.DesiredSize.Height, title.DesiredSize.Height), Tallest(actions)));
        }

        var width = available.Width;
        Current = actions.Length == 0 || width - lead - inlineActions >= MinimumTitleWidth ? Arrangement.Inline
            : _actionsWidth <= width ? Arrangement.ActionRow
            : Arrangement.Stacked;
        title.Measure(new(Math.Max(0, width - lead - (Current == Arrangement.Inline ? inlineActions : 0)), double.PositiveInfinity));
        var head = Math.Max(icon.DesiredSize.Height, title.DesiredSize.Height);
        return Current switch
        {
            Arrangement.Inline => new(width, Math.Max(head, Tallest(actions))),
            Arrangement.ActionRow => new(width, head + RowSpacing + Tallest(actions)),
            _ => new(width, head + actions.Sum(action => RowSpacing + action.DesiredSize.Height)),
        };
    }

    protected override Size ArrangeOverride(Size final)
    {
        var (icon, title, actions) = Parts();
        var lead = icon.DesiredSize.Width + ColumnSpacing;
        var inline = Current == Arrangement.Inline;
        var head = inline ? final.Height : Math.Max(icon.DesiredSize.Height, title.DesiredSize.Height);
        var titleWidth = Math.Max(0, final.Width - lead - (inline && actions.Length > 0 ? ColumnSpacing + _actionsWidth : 0));
        icon.Arrange(new(0, (head - icon.DesiredSize.Height) / 2, icon.DesiredSize.Width, icon.DesiredSize.Height));
        title.Arrange(new(lead, (head - title.DesiredSize.Height) / 2, titleWidth, title.DesiredSize.Height));

        var x = final.Width - _actionsWidth;
        var y = inline ? 0 : head + RowSpacing;
        foreach (var action in actions)
        {
            var size = action.DesiredSize;
            switch (Current)
            {
                case Arrangement.Stacked:
                    var width = Math.Min(size.Width, final.Width);
                    action.Arrange(new(final.Width - width, y, width, size.Height));
                    y += size.Height + RowSpacing;
                    break;
                default:
                    var row = inline ? final.Height : Tallest(actions);
                    action.Arrange(new(x, y + (row - size.Height) / 2, size.Width, size.Height));
                    x += size.Width + ActionSpacing;
                    break;
            }
        }
        return final;
    }

    private (UIElement Icon, UIElement Title, UIElement[] Actions) Parts() =>
        (Children[0], Children[1], Children.Skip(2).Where(child => child.Visibility == Visibility.Visible).ToArray());

    private static double Tallest(UIElement[] elements) => elements.Length == 0 ? 0 : elements.Max(element => element.DesiredSize.Height);
}
