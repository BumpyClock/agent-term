using AgentTerm.Agents;
using AgentTerm.Terminal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AgentTerm_App;

public sealed partial class PromptModeBar : UserControl
{
    internal const double CompactButtonWidth = 24;
    internal const double CompactModeWidth = 56;
    private const string ModeTip = "Smart keeps the shell prompt on the bottom row with history above it. Legacy draws from the top like a conventional terminal. "
        + "Both are the same shell, and switching never interrupts it. Type /agent and a request at the prompt to ask the agent. F6 focuses this control. "
        + "In the terminal, drag to select. Ctrl+Shift+C copies and Ctrl+Shift+V pastes.";
    private readonly ActivityLabel _taskLabel = new();
    private string _mode = "";
    private string _status = "";
    private string _detail = "";
    private string _task = "";
    private bool _interact;
    private bool _compact;
    internal event Action? Changed;
    internal event Action? ModeRequested;
    internal event Action? TaskRequested;
    internal event Action? InteractRequested;

    public PromptModeBar()
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

    internal void ShowMode(bool legacy, ShellPromptState prompt, bool entryUnavailable)
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
        var mode = legacy ? "Legacy" : "Smart";
        if (_mode == mode && _status == status && _detail == detail) return;
        _mode = mode;
        _status = status;
        _detail = detail;
        ToolTipService.SetToolTip(PromptModeStatus, detail.Length > 0 ? detail : null);
        AutomationProperties.SetName(PromptModeStatus, detail);
        var reason = detail.Length > 0 ? " " + detail : "";
        AutomationProperties.SetName(RawTerminalButton, $"{mode} display. Switch to {(legacy ? "Smart" : "Legacy")}. F6 focuses this control.{reason}");
        ToolTipService.SetToolTip(RawTerminalButton, ModeTip + reason);
        Apply();
        Changed?.Invoke();
    }

    internal void ShowTask(AgentState state, bool visible, bool animate)
    {
        var label = state switch
        {
            AgentState.WaitingForApproval => "Agent needs approval",
            AgentState.WaitingForInput => "Agent needs an answer",
            AgentState.Starting or AgentState.Running or AgentState.Interrupting => "Agent working",
            _ => "",
        };
        _taskLabel.Update(label, animate && state is AgentState.Running or AgentState.Starting);
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

    internal bool FocusMode() => RawTerminalButton.Focus(FocusState.Keyboard);

    private void Apply()
    {
        PromptModeStatus.Text = _status;
        PromptModeStatus.Visibility = Shown(_status.Length > 0 && !_compact);
        TaskStatusButton.Visibility = Shown(_task.Length > 0);
        TaskStatusLabelHost.Visibility = Shown(!_compact);
        InteractButton.Visibility = Shown(_interact);
        InteractLabel.Visibility = Shown(!_compact);
        InteractIcon.Visibility = Shown(_compact);
        TaskStatusButton.Width = InteractButton.Width = _compact ? CompactButtonWidth : double.NaN;
        ModeLabel.Text = _compact ? _mode : _mode + " ⇄";
        RawTerminalButton.Padding = _compact ? new Thickness(6, 2, 6, 2) : new Thickness(8, 2, 8, 2);
        RawTerminalButton.MaxWidth = _compact ? CompactModeWidth : double.PositiveInfinity;
    }

    private static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    private void Mode_Click(object sender, RoutedEventArgs e) => ModeRequested?.Invoke();
    private void Task_Click(object sender, RoutedEventArgs e) => TaskRequested?.Invoke();
    private void Interact_Click(object sender, RoutedEventArgs e) => InteractRequested?.Invoke();
}
