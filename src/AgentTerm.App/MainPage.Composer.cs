using AgentTerm.Agents;
using AgentTerm.Terminal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
namespace AgentTerm_App;

/// <summary>
/// Display mode, agent surface and task cards. The shell's own prompt is the only command input in both modes.
/// Smart draws the same terminal bottom-aligned and Legacy draws it top-aligned; neither writes to the shell.
/// A /agent line accepted by the shell reaches the app through the session at Ready. The app reserves two
/// history rows for its card, then opens the agent surface, which replaces the terminal view without resizing it.
/// </summary>
public sealed partial class MainPage
{
    private readonly AgentSessionController _agent;
    private readonly AgentTimelineView _timelineView = new();
    private readonly Dictionary<AgentTask, (TerminalSession Session, TerminalAnchor Anchor, AgentTaskCard Card)> _cards = [];
    private readonly ActivityLabel _taskStatus = new();
    private bool _replyIme;
    // Per pane. Smart is the default for every shell, because it is presentation only.
    private bool _legacy;
    private (bool Legacy, string Detail)? _modeShown;
    private const string ModeTip = "Smart keeps the shell prompt on the bottom row with history above it. Legacy draws from the top like a conventional terminal. "
        + "Both are the same shell, and switching never interrupts it. Type /agent and a request at the prompt to ask the agent. F6 focuses this control. "
        + "In the terminal, drag to select. Ctrl+Shift+C copies and Ctrl+Shift+V pastes.";
    private const string PausedReply = "Agent replies paused until this turn finishes";
    private string? _stopTip;
    private bool _surfaceOpen;
    private string _agentDraft = "";
    private bool _startingTask;
    private PromptInputOwner _inputOwner = PromptInputOwner.Terminal;
    private string? _promptFont;

    private bool AgentBusy => _agent.HasActiveTurn;
    internal void AttachPromptModeControls(ContentControl host)
    {
        ((Panel)PromptModeControls.Parent).Children.Remove(PromptModeControls);
        host.Content = PromptModeControls;
    }
    private void AgentReply_Changed(object sender, RoutedEventArgs e) { if (AgentReply is not null) RefreshAgentChrome(); }
    private void AgentReply_FocusChanged(object sender, RoutedEventArgs e) => UpdatePromptGlyph();
    // A paused field keeps keyboard focus for Enter, Ctrl+C and Escape, but drops the input glyph so it reads as status.
    private void UpdatePromptGlyph() =>
        PromptGlyph.Opacity = AgentReply.IsReadOnly ? 0 : AgentReply.FocusState != FocusState.Unfocused ? 1 : 0.6;
    private void AgentReply_CompositionStarted(TextBox sender, TextCompositionStartedEventArgs args) => _replyIme = true;
    private void AgentReply_CompositionEnded(TextBox sender, TextCompositionEndedEventArgs args) => _replyIme = false;

    /// <summary>Presentation only. Nothing is written to the shell and the grid size is unchanged, so the switch is instant and always legal.</summary>
    private void ApplyDisplayMode()
    {
        Terminal.SetBottomAligned(!_legacy);
        if (_session is not { } session || _state is not (SessionState.Starting or SessionState.Running or SessionState.Idle)) return;
        session.SetBottomAnchored(!_legacy);
        if (_state != SessionState.Starting) RefreshOutput(session);
    }

    private void RefreshAgentChrome()
    {
        if (AgentReply is null || _agent is null) return;
        var session = _session;
        var state = session?.PromptState ?? ShellPromptState.Unknown;
        var owns = session?.AgentOwnsTerminal == true;
        if (_promptFont != Terminal.TerminalFontFamily)
        {
            _promptFont = Terminal.TerminalFontFamily;
            var font = new FontFamily(_promptFont + ", Cascadia Mono, Consolas");
            AgentReply.FontFamily = PromptGlyph.FontFamily = PromptHint.FontFamily = RequestPanel.DetailFont = _timelineView.Mono = font;
        }
        var request = _agent.Request;
        if (_surfaceOpen)
        {
            // The approval card owns Run and Reject with their keys. The field below states only that replies wait.
            AgentReply.PlaceholderText = request switch
            {
                QuestionRequest { AllowFreeform: true } => "Answer the agent",
                QuestionRequest => "Pick an answer above",
                _ when AgentBusy => PausedReply,
                _ when _agent.CurrentTask?.Turns.Count == 0 => "Ask the agent",
                _ => "Ask a follow up",
            };
            PromptHint.Text = request switch
            {
                QuestionRequest { AllowFreeform: true } => "Enter answer · Shift+Enter new line",
                QuestionRequest => "",
                // A /agent request typed during a turn waits here as text, which hides the placeholder.
                _ when AgentBusy => AgentReply.Text.Length > 0 ? PausedReply : "",
                _ => "Enter send · Shift+Enter new line",
            };
            PromptHint.Visibility = PromptHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        AgentReply.IsReadOnly = AgentBusy && request is not QuestionRequest { AllowFreeform: true };
        UpdatePromptGlyph();
        SubmitPrompt.IsEnabled = CanSend(AgentReply.Text);
        SubmitPrompt.Opacity = AgentReply.IsReadOnly ? 0 : 1;
        SubmitPrompt.IsHitTestVisible = !AgentReply.IsReadOnly;

        InteractButton.Visibility = owns ? Visibility.Visible : Visibility.Collapsed;

        // Agent entry depends on the shell. The terminal itself works the same in every case.
        var (status, detail) = state switch
        {
            ShellPromptState.Initializing => ("Starting shell", "Waiting for shell integration. /agent works once the prompt is ready."),
            ShellPromptState.Exited => ("Shell exited", "The shell exited. Restart the session to continue."),
            ShellPromptState.Unknown => ("/agent unavailable",
                "This shell has no AgentTerm integration, so /agent lines run as ordinary commands. /agent needs PowerShell with PSReadLine."),
            _ when session?.AgentEntryAvailable == false => ("/agent unavailable",
                "AgentTerm's Enter handler is not active in this shell, for example because your PowerShell profile binds Enter itself. /agent lines run as ordinary commands here."),
            _ => ("", ""),
        };
        // Runs every refresh tick. Touch tooltips and names only when the content changes, so an open tooltip stays open.
        var shown = (_legacy, detail);
        if (_modeShown != shown)
        {
            _modeShown = shown;
            var mode = _legacy ? "Legacy" : "Smart";
            PromptModeStatus.Text = status;
            PromptModeStatus.Visibility = status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(PromptModeStatus, detail.Length > 0 ? detail : null);
            AutomationProperties.SetName(PromptModeStatus, detail);
            RawTerminalButton.Content = mode + " ⇄";
            // The mode button is keyboard reachable with F6, so it also carries any /agent limitation.
            var reason = detail.Length > 0 ? " " + detail : "";
            AutomationProperties.SetName(RawTerminalButton, $"{mode} display. Switch to {(_legacy ? "Smart" : "Legacy")}. F6 focuses this control.{reason}");
            ToolTipService.SetToolTip(RawTerminalButton, ModeTip + reason);
        }

        // While an approval waits, Ctrl+C in the reply field rejects it, so only the Reject button advertises the key.
        var approvalPending = request is ApprovalRequest;
        StopShortcut.Visibility = approvalPending ? Visibility.Collapsed : Visibility.Visible;
        var stopTip = approvalPending ? "Stop the agent's current turn" : "Stop the agent's current turn (Ctrl+C)";
        if (_stopTip != stopTip) { _stopTip = stopTip; ToolTipService.SetToolTip(StopAgentButton, stopTip); }
        StopAgentButton.Opacity = AgentBusy ? 1 : 0;
        StopAgentButton.IsEnabled = _agent.CanInterrupt;
        StopAgentButton.IsHitTestVisible = AgentBusy;
        AutomationProperties.SetAccessibilityView(StopAgentButton, AgentBusy ? AccessibilityView.Content : AccessibilityView.Raw);
        ToolTipService.SetToolTip(LeaveSurfaceButton, AgentBusy ? "Back to the terminal. The agent keeps working."
            : "Back to the terminal. This conversation is saved in its task card.");
        RefreshTaskStatus();
    }

    private bool CanSend(string text)
    {
        if (!_surfaceOpen || _startingTask || string.IsNullOrWhiteSpace(text)) return false;
        if (PromptRouter.Parse(text) is AgentIntent) return true;
        return _agent.Request is QuestionRequest { AllowFreeform: true } || (_agent.CanSubmit && _agent.Request is null);
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
        if (AutomationProperties.GetName(TaskStatusButton) != name) AutomationProperties.SetName(TaskStatusButton, name);
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
            RefreshAgentChrome();
        });
    }

    /// <summary>Called on activation changes so shimmer runs only in a visible pane.</summary>
    private void SetAgentAnimation(bool active)
    {
        if (_surfaceOpen) _timelineView.Update(_agent, _agent.CurrentTask, active);
        foreach (var entry in _cards.Values) entry.Card.Update(_agent, active);
        RefreshAgentChrome();
    }

    private void UpdateSurface(bool follow)
    {
        if (!AgentTimelineHost.Children.Contains(_timelineView)) AgentTimelineHost.Children.Add(_timelineView);
        var task = _agent.CurrentTask;
        _timelineView.Update(_agent, task, _viewActive && IsLoaded);
        SurfaceTitle.Text = task is { Turns.Count: > 0 } ? "Agent task" : "New agent task";
        ToolTipService.SetToolTip(SurfaceTitle, task?.Title);
        RequestPanel.Update();
        if (!follow) return;
        AgentOutput.UpdateLayout();
        AgentOutput.ChangeView(null, AgentOutput.ScrollableHeight, null, true);
    }

    private bool PaneInputFocused()
    {
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot);
        return ReferenceEquals(focused, AgentReply) || ReferenceEquals(focused, Terminal.Input);
    }

    /// <summary>
    /// Input moves only with the surface. An explicit open or leave hands it over; a shell-driven open does so
    /// only while this pane's input still holds focus, so passive updates never steal it.
    /// </summary>
    private void UpdateInputOwner(bool handoff)
    {
        var next = PromptFocusPolicy.Owner(_surfaceOpen);
        var transfer = PromptFocusPolicy.ShouldTransfer(_inputOwner, next, _viewActive && _inputActive,
            !_replyIme && FindBar.Visibility != Visibility.Visible, handoff);
        _inputOwner = next;
        if (!transfer) return;
        // The target was collapsed until this call. Measure it first so Focus can land.
        UpdateLayout();
        FocusTerminal();
    }

    private void OpenSurface(AgentTask task, bool explicitHandoff = true)
    {
        _agent.ActivateTask(task);
        if (!_surfaceOpen)
        {
            // Read focus before collapsing the terminal, which drops it.
            var handoff = explicitHandoff || PaneInputFocused();
            _surfaceOpen = true;
            AgentReply.Text = _agentDraft;
            // Collapsing the terminal reports a zero size, which the grid sizing ignores, so the shell is never resized.
            Terminal.Visibility = HistoryScrollBar.Visibility = TaskCardLayer.Visibility = Visibility.Collapsed;
            AgentSurface.Visibility = Visibility.Visible;
            UpdateInputOwner(handoff);
        }
        UpdateSurface(true);
        RefreshAgentChrome();
    }

    private void LeaveSurface()
    {
        if (!_surfaceOpen) return;
        _agentDraft = AgentReply.Text;
        _surfaceOpen = false;
        AgentSurface.Visibility = Visibility.Collapsed;
        Terminal.Visibility = HistoryScrollBar.Visibility = TaskCardLayer.Visibility = Visibility.Visible;
        if (_session is { } session)
        {
            session.ScrollToBottom();
            RefreshOutput(session);
        }
        UpdateInputOwner(true);
        RefreshAgentChrome();
    }

    private void LeaveSurface_Click(object sender, RoutedEventArgs e) => LeaveSurface();
    internal bool TryLeaveAgentSurface()
    {
        if (!_surfaceOpen || IsComposing) return false;
        LeaveSurface();
        return true;
    }
    private void TaskStatus_Click(object sender, RoutedEventArgs e) => OpenLatestTask();
    private bool OpenLatestTask()
    {
        if ((_agent.RunningTask ?? _agent.CurrentTask) is not { } task) return false;
        OpenSurface(task);
        return true;
    }

    private async void SubmitPrompt_Click(object sender, RoutedEventArgs e) => await SubmitReplyAsync();
    private async Task SubmitReplyAsync()
    {
        var text = AgentReply.Text;
        if (!CanSend(text)) return;
        if (PromptRouter.Parse(text) is AgentIntent agent)
        {
            AgentReply.Text = "";
            await StartTaskAsync(agent.Request);
        }
        else await SendToAgentAsync(text);
        RefreshAgentChrome();
    }

    private async Task SendToAgentAsync(string text)
    {
        if (RequestPanel.Shown is QuestionRequest { AllowFreeform: true } question)
        {
            if (await _agent.RespondAsync(new(question.Request, Answer: text)) && AgentReply.Text == text) AgentReply.Text = "";
            return;
        }
        if (!_agent.CanSubmit) return;
        if (_session?.ReadWorkingDirectory() is not { } directory) { ShowPromptHint("The agent needs the shell to report a local working directory."); return; }
        // The request appears in the conversation at once. A failure to start is reported there, not by restoring the draft.
        AgentReply.Text = "";
        await _agent.SubmitAsync(text, new(directory.Path, directory.WslDistribution));
    }

    /// <summary>
    /// Starts a task for a /agent request. Two blank history rows are reserved at the shell prompt first so the
    /// task's card stays where it was invoked. While a turn runs, the running task opens instead and the request
    /// waits in its reply field, so the text is never lost.
    /// </summary>
    private async Task StartTaskAsync(string request, bool explicitHandoff = true)
    {
        if (AgentBusy)
        {
            if (_agent.RunningTask is not { } running) return;
            OpenSurface(running, explicitHandoff);
            if (request.Length > 0 && AgentReply.Text.Length == 0) AgentReply.Text = request;
            return;
        }
        if (_startingTask) return;
        _startingTask = true;
        try
        {
            var task = _agent.BeginTask();
            _agentDraft = "";
            if (_session is { } session && session.ReserveRows() is { } anchor)
            {
                var card = new AgentTaskCard(task);
                card.Click += (_, _) => OpenSurface(task);
                card.Update(_agent, false);
                TaskCardLayer.Children.Add(card);
                _cards[task] = (session, anchor, card);
                // The reservation is a shell command. The agent's first tool needs the prompt back.
                using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { while (session.PromptState != ShellPromptState.Ready) await Task.Delay(16, ready.Token); }
                catch (OperationCanceledException) { }
            }
            OpenSurface(task, explicitHandoff);
            if (request.Length > 0) await SendToAgentAsync(request);
        }
        finally { _startingTask = false; RefreshAgentChrome(); }
    }

    /// <summary>
    /// Positions cards from their anchors on every frame through the surface's grid mapping. A card shows only over
    /// its reserved rows while they are blank. If shell text occupies them the card hides rather than cover it, and the
    /// task stays reachable from the task status button and Ctrl+Shift+Y. An expired anchor removes its card.
    /// </summary>
    private void RefreshTaskCards(TerminalFrame frame)
    {
        if (_cards.Count == 0) return;
        var geometry = Terminal.GeometryFor(frame);
        var cell = geometry.CellHeight;
        foreach (var (task, entry) in _cards.ToArray())
        {
            var row = entry.Session == _session ? entry.Session.ReadAnchor(entry.Anchor) : null;
            if (row is null) { RemoveCard(task); continue; }
            var frameRow = row.Value - frame.Viewport.Offset;
            var top = geometry.RowTop(frameRow);
            // Cards own reserved history rows, never the live prompt, even during a redraw.
            var cursorRow = frame.Viewport.Offset + frame.Cursor.Row;
            var overlapsPrompt = row.Value <= cursorRow && cursorRow < row.Value + 2;
            var visible = !_surfaceOpen && !frame.Viewport.IsAlternateScreen && !overlapsPrompt
                && top > -2 * cell && top < TaskCardLayer.ActualHeight && geometry.RowsBlank(frame, frameRow, 2);
            entry.Card.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) continue;
            Canvas.SetTop(entry.Card, top + 2);
            entry.Card.Width = Math.Max(0, Math.Min(TaskCardLayer.ActualWidth, geometry.Columns * geometry.CellWidth));
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

    private async void AgentReply_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_replyIme) return;
        var ctrl = Down(VirtualKey.Control);
        var shift = Down(VirtualKey.Shift);
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                LeaveSurface();
                return;
            case VirtualKey.C when ctrl && !shift && AgentReply.SelectionLength == 0:
                if (_agent.Request is ApprovalRequest) { e.Handled = true; await RequestPanel.DenyAsync(); }
                else if (AgentBusy) { e.Handled = true; InterruptAgent_Click(sender, e); }
                return;
            case VirtualKey.Back when AgentReply.Text.Length == 0:
                e.Handled = true;
                LeaveSurface();
                return;
            case VirtualKey.Enter when !shift:
                e.Handled = true;
                if (RequestPanel.Shown is ApprovalRequest && AgentReply.Text.Length == 0)
                {
                    if (!e.KeyStatus.WasKeyDown) await RequestPanel.ApproveAsync();
                }
                else await SubmitReplyAsync();
                return;
        }
    }

    internal bool FocusPromptMode() => RawTerminalButton.Focus(FocusState.Keyboard);

    private void RawTerminal_Click(object sender, RoutedEventArgs e)
    {
        _legacy = !_legacy;
        ApplyDisplayMode();
        // Show the result. The surface, if open, keeps its conversation and draft.
        LeaveSurface();
        RefreshAgentChrome();
        // An explicit activation hands input to the terminal. Passive updates never move focus.
        FocusTerminal();
    }

    private void Interact_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not { AgentOwnsTerminal: true } session) return;
        LeaveSurface();
        session.AllowAgentInteractiveInput();
        RefreshAgentChrome();
        FocusTerminal();
    }

    private async void InterruptAgent_Click(object sender, RoutedEventArgs e)
    {
        try { await _agent.InterruptAsync(); }
        catch (Exception ex) { ShowPromptHint(ex.Message); }
    }

    private void ShowPromptHint(string text)
    {
        PromptHint.Text = text;
        PromptHint.Visibility = Visibility.Visible;
    }
}
