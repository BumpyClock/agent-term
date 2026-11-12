using AgentTerm.Agents;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AgentTerm_App;

/// <summary>
/// The agent's pending approval or question, shown directly above the reply field. It owns its own focus: when the
/// shown request changes, a focused control inside the panel is about to be hidden or removed, so focus moves to the
/// reply field first. Left to the framework, that focus can land on a collapsed element where no visible control
/// owns the keyboard.
/// </summary>
public sealed partial class AgentRequestPanel : UserControl
{
    private AgentSessionController? _agent;
    private Control? _reply;

    public AgentRequestPanel() => InitializeComponent();

    internal AgentEvent? Shown { get; private set; }
    internal FontFamily DetailFont { set => RequestText.FontFamily = value; }

    internal void Attach(AgentSessionController agent, Control reply)
    {
        _agent = agent;
        _reply = reply;
    }

    internal void Update()
    {
        var request = _agent?.Request;
        if (request != Shown)
        {
            // Hand off before any control below is collapsed or removed, so the framework never picks a successor.
            if (_reply is not null && XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused && Contains(focused))
                _reply.Focus(focused is Control { FocusState: FocusState.Keyboard } ? FocusState.Keyboard : FocusState.Programmatic);
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
        Visibility = request is null ? Visibility.Collapsed : Visibility.Visible;
        RequestTitle.Text = approval is not null ? "Allow the agent to run this?" : asked?.Question ?? "";
        RequestText.Text = approval?.Description ?? "";
        RequestDetail.Visibility = approval is not null ? Visibility.Visible : Visibility.Collapsed;
        ApproveRequest.Visibility = DenyRequest.Visibility = approval is not null ? Visibility.Visible : Visibility.Collapsed;
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

    private async void ApproveRequest_Click(object sender, RoutedEventArgs e) => await ApproveAsync();
    private async void DenyRequest_Click(object sender, RoutedEventArgs e) => await DenyAsync();
}
