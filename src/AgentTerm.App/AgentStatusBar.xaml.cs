using AgentTerm.Agents;
using AgentTerm.Terminal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AgentTerm_App;

public sealed partial class AgentStatusBar : UserControl
{
    internal const double CompactButtonWidth = 24;
    private readonly TextBlock _taskLabel = new() { Style = AgentUi.Style("AgentLabelText") };
    private string _status = "";
    private string _detail = "";
    private string _task = "";
    private bool _interact;
    private bool _compact;
    internal event Action? Changed;
    internal event Action? TaskRequested;
    internal event Action? InteractRequested;

    public AgentStatusBar()
    {
        InitializeComponent();
        TaskStatusLabelHost.Children.Add(_taskLabel);
    }

    internal bool Compact
    {
        get => _compact;
        set
        {
            if (_compact == value) return;
            _compact = value;
            Apply();
        }
    }

    internal void ShowShellStatus(ShellPromptState prompt, bool entryUnavailable)
    {
        var (status, detail) = prompt switch
        {
            ShellPromptState.Initializing => ("Starting shell", "Waiting for shell integration. /agent works once the prompt is ready."),
            ShellPromptState.Exited => ("Shell exited", "The shell exited. Restart the session to continue."),
            ShellPromptState.Unknown => ("/agent unavailable",
                "This shell has no AgentTerm integration, so /agent lines run as ordinary commands. /agent needs PowerShell with PSReadLine."),
            _ when entryUnavailable => ("/agent unavailable",
                "AgentTerm's Enter handler is not active in this shell, for example because your PowerShell profile binds Enter itself. /agent lines run as ordinary commands here."),
            _ => ("", ""),
        };
        if (_status == status && _detail == detail) return;
        _status = status;
        _detail = detail;
        ToolTipService.SetToolTip(ShellStatus, detail.Length > 0 ? detail : null);
        AutomationProperties.SetName(ShellStatus, detail);
        Apply();
        Changed?.Invoke();
    }

    internal void ShowTask(AgentState state, bool visible)
    {
        var label = state switch
        {
            AgentState.WaitingForApproval => "Agent needs approval",
            AgentState.WaitingForInput => "Agent needs an answer",
            AgentState.Starting or AgentState.Running or AgentState.Interrupting => "Agent working",
            _ => "",
        };
        _taskLabel.Text = state is AgentState.Starting or AgentState.Running or AgentState.Interrupting ? "" : label;
        var shown = visible ? label : "";
        if (_task == shown) return;
        _task = shown;
        AutomationProperties.SetName(TaskStatusButton, label + ". Open the agent task");
        ToolTipService.SetToolTip(TaskStatusButton, label + ". Open the agent task (Ctrl+Shift+Y)");
        Apply();
        Changed?.Invoke();
    }

    internal void ShowInteract(bool visible)
    {
        if (_interact == visible) return;
        _interact = visible;
        Apply();
        Changed?.Invoke();
    }

    private void Apply()
    {
        ShellStatus.Text = _compact ? "\uE946" : _status;
        ShellStatus.FontFamily = _compact ? new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons") : FontFamily;
        ShellStatus.Visibility = Shown(_status.Length > 0);
        TaskStatusButton.Visibility = Shown(_task.Length > 0);
        TaskStatusLabelHost.Visibility = Shown(!_compact && _taskLabel.Text.Length > 0);
        InteractButton.Visibility = Shown(_interact);
        InteractLabel.Visibility = Shown(!_compact);
        InteractIcon.Visibility = Shown(_compact);
        TaskStatusButton.Width = _compact || _taskLabel.Text.Length == 0 ? CompactButtonWidth : double.NaN;
        InteractButton.Width = _compact ? CompactButtonWidth : double.NaN;
    }

    private static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    private void Task_Click(object sender, RoutedEventArgs e) => TaskRequested?.Invoke();
    private void Interact_Click(object sender, RoutedEventArgs e) => InteractRequested?.Invoke();
}
