using AgentTerm.Agents;
using AgentTerm.Terminal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
namespace AgentTerm_App;

/// <summary>
/// Smart prompt, agent surface and task cards. Prompt text runs in the shell unchanged; only a leading /agent
/// starts an agent task. The task's surface replaces the terminal view in place without resizing the shell,
/// and its collapsed card stays on two history rows reserved where it was invoked.
/// </summary>
public sealed partial class MainPage
{
    private readonly AgentSessionController _agent;
    private readonly AgentTimelineView _timelineView = new();
    private readonly Dictionary<AgentTask, (TerminalSession Session, TerminalAnchor Anchor, AgentTaskCard Card)> _cards = [];
    private readonly ActivityLabel _taskStatus = new();
    private bool _composerIme;
    private bool _rawMode;
    private bool _surfaceOpen;
    private bool _startingTask;
    private AgentEvent? _shownRequest;
    private ShellPromptState _lastPrompt;
    private string? _promptFont;

    private bool AgentBusy => _agent.State is AgentState.Starting or AgentState.Running or AgentState.WaitingForApproval or AgentState.WaitingForInput or AgentState.Interrupting;
    private void Composer_Changed(object sender, RoutedEventArgs e) { if (Composer is not null) RefreshComposer(); }
    private void Composer_FocusChanged(object sender, RoutedEventArgs e) => RefreshPromptGlyph();
    private void Composer_CompositionStarted(TextBox sender, TextCompositionStartedEventArgs args) => _composerIme = true;
    private void Composer_CompositionEnded(TextBox sender, TextCompositionEndedEventArgs args) => _composerIme = false;

    private void RefreshComposer()
    {
        if (Composer is null || _agent is null) return;
        var session = _session;
        var state = session?.PromptState ?? ShellPromptState.Unknown;
        if (_rawMode && state == ShellPromptState.Ready) session?.RestoreRawPrompt();
        if (state == ShellPromptState.Raw && !_rawMode) session?.EnableManagedPrompt();
        if (state == ShellPromptState.Ready && _lastPrompt != state && !_rawMode && _viewActive) Composer.Focus(FocusState.Programmatic);
        _lastPrompt = state;
        Terminal.SetCursorSuppressed(state == ShellPromptState.Ready && !_rawMode);
        if (_promptFont != Terminal.TerminalFontFamily)
        {
            _promptFont = Terminal.TerminalFontFamily;
            var font = new FontFamily(_promptFont + ", Cascadia Mono, Consolas");
            Composer.FontFamily = PromptGlyph.FontFamily = PromptHint.FontFamily = RequestText.FontFamily = _timelineView.Mono = font;
        }
        var owns = session?.AgentOwnsTerminal == true;
        var request = _agent.Request;
        if (_surfaceOpen)
        {
            Composer.PlaceholderText = request switch
            {
                ApprovalRequest => "Enter runs it. Ctrl+C rejects it.",
                QuestionRequest { AllowFreeform: true } => "Answer the agent",
                QuestionRequest => "Pick an answer above",
                _ when AgentBusy => "The agent is working. Esc returns to the terminal.",
                _ => "Ask a follow up, or Esc for terminal",
            };
            PromptHint.Text = "esc terminal · enter send · shift+enter new line" + (AgentBusy ? " · ctrl+c stop" : "");
        }
        else
        {
            // One line that never changes with shell state, so the prompt height and the PTY size stay put during a command.
            Composer.PlaceholderText = _rawMode ? "Native prompt is active. Click Smart prompt to return." : "Run a command, or /agent to ask the agent";
            PromptHint.Text = "/agent ask the agent" + (_agent.CurrentTask is not null ? " · ctrl+shift+y open last task" : "") + " · esc native prompt";
        }
        SubmitPrompt.IsEnabled = CanSend(Composer.Text, state, owns);
        var raw = owns ? "Type in terminal" : _rawMode ? "Smart prompt" : "Native prompt";
        if (RawTerminalButton.Content as string != raw)
        {
            RawTerminalButton.Content = raw;
            ToolTipService.SetToolTip(RawTerminalButton, owns ? "Let keystrokes reach the command the agent is running"
                : _rawMode ? "Return to the smart prompt" : "Use the shell's own prompt (Esc)");
        }
        RawTerminalButton.Visibility = owns || !_surfaceOpen ? Visibility.Visible : Visibility.Collapsed;
        StopAgentButton.Visibility = AgentBusy ? Visibility.Visible : Visibility.Collapsed;
        RefreshTaskStatus();
        RefreshPromptGlyph();
    }

    private bool CanSend(string text, ShellPromptState state, bool owns)
    {
        if (_startingTask) return false;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (PromptRouter.Parse(text) is AgentIntent) return true;
        if (_surfaceOpen) return _agent.Request is QuestionRequest { AllowFreeform: true } || (_agent.CanSubmit && _agent.Request is null);
        return state == ShellPromptState.Ready && !owns;
    }

    private void RefreshPromptGlyph()
    {
        var style = AgentUi.Style(_surfaceOpen ? "AgentPromptGlyphAgent" : "AgentPromptGlyph");
        if (PromptGlyph.Style != style) PromptGlyph.Style = style;
        PromptGlyph.Opacity = Composer.FocusState != FocusState.Unfocused || _surfaceOpen ? 1 : 0.6;
    }

    /// <summary>Keeps background agent work discoverable from the terminal view.</summary>
    private void RefreshTaskStatus()
    {
        if (TaskStatusLabelHost.Children.Count == 0) TaskStatusLabelHost.Children.Add(_taskStatus);
        var label = _agent.State switch
        {
            AgentState.WaitingForApproval => "Agent needs approval",
            AgentState.WaitingForInput => "Agent needs an answer",
            AgentState.Starting or AgentState.Running or AgentState.Interrupting => "Agent working",
            _ => "",
        };
        TaskStatusButton.Visibility = !_surfaceOpen && label.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _taskStatus.Update(label, _viewActive && IsLoaded && _agent.State is AgentState.Running or AgentState.Starting);
        var name = label + ". Open the agent task";
        if (Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(TaskStatusButton) != name) Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TaskStatusButton, name);
    }

    private void AgentChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_state == SessionState.Disposed) return;
            if (_surfaceOpen) UpdateSurface(AgentOutput.ScrollableHeight - AgentOutput.VerticalOffset < 24);
            foreach (var (task, entry) in _cards.ToArray())
            {
                if (!_agent.Tasks.Contains(task)) RemoveCard(task);
                else entry.Card.Update(_agent, _viewActive && IsLoaded);
            }
            RefreshComposer();
        });
    }

    /// <summary>Called on activation changes so shimmer runs only in a visible pane.</summary>
    private void SetAgentAnimation(bool active)
    {
        if (_surfaceOpen) _timelineView.Update(_agent, _agent.CurrentTask, active);
        foreach (var entry in _cards.Values) entry.Card.Update(_agent, active);
        RefreshComposer();
    }

    private void UpdateSurface(bool follow)
    {
        if (!AgentTimelineHost.Children.Contains(_timelineView)) AgentTimelineHost.Children.Add(_timelineView);
        var task = _agent.CurrentTask;
        _timelineView.Update(_agent, task, _viewActive && IsLoaded);
        SurfaceTitle.Text = task is { Title.Length: > 0 } ? task.Title : "New agent task";
        var request = _agent.Request;
        var approval = request as ApprovalRequest;
        var question = request as QuestionRequest;
        RequestPanel.Visibility = request is null ? Visibility.Collapsed : Visibility.Visible;
        RequestTitle.Text = approval is not null ? "Allow the agent to run this?" : question?.Question ?? "";
        RequestText.Text = approval?.Description ?? "";
        RequestDetail.Visibility = approval is not null ? Visibility.Visible : Visibility.Collapsed;
        ApproveRequest.Visibility = DenyRequest.Visibility = approval is not null ? Visibility.Visible : Visibility.Collapsed;
        RequestChoices.Visibility = question?.Choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_shownRequest != request)
        {
            _shownRequest = request;
            RequestChoices.Children.Clear();
            foreach (var choice in question?.Choices ?? [])
            {
                var button = new Button { Content = new TextBlock { Text = choice, TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 3, 10, 3), MinHeight = 0 };
                var requestId = question!.Request;
                button.Click += async (_, _) => await _agent.RespondAsync(new(requestId, Answer: choice));
                RequestChoices.Children.Add(button);
            }
        }
        if (!follow) return;
        AgentOutput.UpdateLayout();
        AgentOutput.ChangeView(null, AgentOutput.ScrollableHeight, null, true);
    }

    private void OpenSurface(AgentTask task)
    {
        _agent.ActivateTask(task);
        if (!_surfaceOpen)
        {
            _surfaceOpen = true;
            // Collapsing the terminal reports a zero size, which the grid sizing ignores, so the shell is never resized.
            Terminal.Visibility = HistoryScrollBar.Visibility = TaskCardLayer.Visibility = Visibility.Collapsed;
            AgentSurface.Visibility = Visibility.Visible;
        }
        UpdateSurface(true);
        RefreshComposer();
        Composer.Focus(FocusState.Programmatic);
    }

    private void LeaveSurface()
    {
        if (!_surfaceOpen) return;
        _surfaceOpen = false;
        AgentSurface.Visibility = Visibility.Collapsed;
        Terminal.Visibility = HistoryScrollBar.Visibility = TaskCardLayer.Visibility = Visibility.Visible;
        if (_session is { } session) RefreshTaskCards(session.ReadFrame());
        RefreshComposer();
        Composer.Focus(FocusState.Programmatic);
    }

    private void LeaveSurface_Click(object sender, RoutedEventArgs e) => LeaveSurface();
    internal bool TryLeaveAgentSurface()
    {
        if (!_surfaceOpen || IsComposing) return false;
        LeaveSurface();
        return true;
    }
    private void TaskStatus_Click(object sender, RoutedEventArgs e) { if ((_agent.RunningTask ?? _agent.CurrentTask) is { } task) OpenSurface(task); }

    private async void SubmitPrompt_Click(object sender, RoutedEventArgs e) => await SubmitComposerAsync();
    private async Task SubmitComposerAsync()
    {
        var text = Composer.Text;
        if (_session is not { } session || !CanSend(text, session.PromptState, session.AgentOwnsTerminal)) return;
        if (PromptRouter.Parse(text) is AgentIntent agent) await StartTaskAsync(agent.Request);
        else if (_surfaceOpen) await SendToAgentAsync(text);
        else
        {
            _lastPrompt = ShellPromptState.Executing;
            session.SubmitCommand(text);
            Composer.Text = "";
            Terminal.FocusInput(FocusState.Programmatic);
        }
        RefreshComposer();
    }

    private async Task SendToAgentAsync(string text)
    {
        if (_shownRequest is QuestionRequest { AllowFreeform: true } question)
        {
            if (await _agent.RespondAsync(new(question.Request, Answer: text)) && Composer.Text == text) Composer.Text = "";
            return;
        }
        if (!_agent.CanSubmit) return;
        if (_session?.ReadWorkingDirectory() is not { } directory) { PromptHint.Text = "The agent needs the shell to report a local working directory."; return; }
        // The request appears in the conversation at once. A failure to start is reported there, not by restoring the draft.
        Composer.Text = "";
        await _agent.SubmitAsync(text, new(directory.Path, directory.WslDistribution));
    }

    /// <summary>
    /// /agent opens a new task. Two blank history rows are reserved first so its card stays where it was invoked.
    /// While a turn runs, /agent reopens that task instead and leaves the draft in place.
    /// </summary>
    private async Task StartTaskAsync(string request)
    {
        if (AgentBusy) { if (_agent.RunningTask is { } running) OpenSurface(running); return; }
        if (_startingTask) return;
        _startingTask = true;
        try
        {
            var task = _agent.BeginTask();
            Composer.Text = "";
            if (_session is { } session && !_rawMode && session.ReserveRows() is { } anchor)
            {
                var card = new AgentTaskCard(task);
                card.Click += (_, _) => OpenSurface(task);
                card.Update(_agent, false);
                TaskCardLayer.Children.Add(card);
                _cards[task] = (session, anchor, card);
                using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { while (session.PromptState != ShellPromptState.Ready) await Task.Delay(16, ready.Token); }
                catch (OperationCanceledException) { }
            }
            OpenSurface(task);
            if (request.Length > 0) await SendToAgentAsync(request);
        }
        finally { _startingTask = false; RefreshComposer(); }
    }

    /// <summary>Positions cards from their anchors on every frame. An expired anchor removes its card; the task remains reachable from the prompt.</summary>
    private void RefreshTaskCards(TerminalFrame frame)
    {
        if (_cards.Count == 0) return;
        var cell = Terminal.CellHeight;
        foreach (var (task, entry) in _cards.ToArray())
        {
            var row = entry.Session == _session ? entry.Session.ReadAnchor(entry.Anchor) : null;
            if (row is null) { RemoveCard(task); continue; }
            var top = (row.Value - frame.Viewport.Offset) * cell;
            var visible = !_surfaceOpen && !frame.Viewport.IsAlternateScreen && top > -2 * cell && top < TaskCardLayer.ActualHeight;
            entry.Card.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) continue;
            Canvas.SetTop(entry.Card, top + 2);
            entry.Card.Width = Math.Max(0, TaskCardLayer.ActualWidth);
            entry.Card.Height = Math.Max(0, 2 * cell - 4);
        }
    }

    private void RemoveCard(AgentTask task)
    {
        if (!_cards.Remove(task, out var entry)) return;
        TaskCardLayer.Children.Remove(entry.Card);
        if (entry.Session == _session) entry.Session.ReleaseAnchor(entry.Anchor);
    }

    private void TaskCardLayer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        TaskCardLayer.Clip = new RectangleGeometry { Rect = new(0, 0, e.NewSize.Width, e.NewSize.Height) };
        if (_session is { } session && _state is SessionState.Running or SessionState.Idle) RefreshTaskCards(session.ReadFrame());
    }
    private void TaskCardLayer_PointerWheelChanged(object sender, PointerRoutedEventArgs e) => Terminal_PointerWheelChanged(Terminal, e);

    private async void Composer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_composerIme) return;
        var ctrl = Down(VirtualKey.Control);
        var shift = Down(VirtualKey.Shift);
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                if (_surfaceOpen) LeaveSurface(); else RawTerminal_Click(sender, e);
                return;
            case VirtualKey.Y when ctrl && shift && (_agent.RunningTask ?? _agent.CurrentTask) is { } task:
                e.Handled = true;
                OpenSurface(task);
                return;
            case VirtualKey.C when ctrl && !shift && _surfaceOpen && Composer.SelectionLength == 0:
                if (_agent.Request is ApprovalRequest) { e.Handled = true; DenyRequest_Click(sender, e); }
                else if (AgentBusy) { e.Handled = true; InterruptAgent_Click(sender, e); }
                return;
            case VirtualKey.Back when _surfaceOpen && Composer.Text.Length == 0:
                e.Handled = true;
                LeaveSurface();
                return;
            case VirtualKey.Enter when !shift:
                e.Handled = true;
                if (_surfaceOpen && _shownRequest is ApprovalRequest && Composer.Text.Length == 0)
                {
                    if (!e.KeyStatus.WasKeyDown) ApproveRequest_Click(sender, e);
                }
                else await SubmitComposerAsync();
                return;
        }
    }

    private void RawTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (_session?.AgentOwnsTerminal == true)
        {
            LeaveSurface();
            _session.AllowAgentInteractiveInput();
            Terminal.FocusInput(FocusState.Programmatic);
            return;
        }
        _rawMode = !_rawMode;
        if (_rawMode) { _session?.RestoreRawPrompt(); Terminal.FocusInput(FocusState.Programmatic); }
        else Composer.Focus(FocusState.Programmatic);
        RefreshComposer();
    }

    private async void InterruptAgent_Click(object sender, RoutedEventArgs e)
    {
        try { await _agent.InterruptAsync(); }
        catch (Exception ex) { PromptHint.Text = ex.Message; }
    }
    private async void ApproveRequest_Click(object sender, RoutedEventArgs e)
    { if (_shownRequest is ApprovalRequest request) await _agent.RespondAsync(new(request.Request, true)); }
    private async void DenyRequest_Click(object sender, RoutedEventArgs e)
    { if (_shownRequest is ApprovalRequest request) await _agent.RespondAsync(new(request.Request)); }
}
