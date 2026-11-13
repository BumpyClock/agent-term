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

public sealed partial class MainPage
{
    private readonly AgentSessionController _agent;
    private readonly AgentTimelineView _timelineView = new();
    private readonly Dictionary<AgentTask, (TerminalSession Session, TerminalAnchor Anchor, AgentTaskCard Card)> _cards = [];
    private readonly AgentStatusBar _agentBar = new();
    private bool _replyIme;
    private const string PausedReply = "Agent replies paused until this turn finishes";
    private string? _stopTip;
    private bool _surfaceOpen;
    private AgentTask? _surfaceTask;
    private string? _promptFailure;
    private bool _startingTask;
    private PromptInputOwner _inputOwner = PromptInputOwner.Terminal;
    private string? _promptFont;
    private CoalescedUpdate? _agentUpdate;
    private readonly List<AgentTask> _expiredCards = [];
    private bool _followOutput = true;
    private (string Heading, string? Tip)? _surfaceTitle;
    private ChromeState? _chrome;
    private readonly record struct ChromeState(ShellPromptState Shell, bool Owns, bool EntryUnavailable, string? Placeholder, string? Hint,
        bool ReadOnly, FocusState Focus, bool CanSend, bool ApprovalPending, bool Busy, bool CanInterrupt, AgentState Agent, bool TaskShown, bool Animate);

    private DateTimeOffset _agentStatusUpdatedAt = DateTimeOffset.UtcNow;
    private ProgramStatus? AgentProgramStatus => !_agent.HasActiveTurn ? null : new(
        "@agent", _agent.State is AgentState.WaitingForApproval or AgentState.WaitingForInput ? ProgramState.Blocked : ProgramState.Working,
        _agent.State switch { AgentState.WaitingForApproval => "permission", AgentState.WaitingForInput => "question", _ => null },
        null, "agentterm", "agentterm", _agent.RunningTask?.Title ?? "Agent task", null, _agentStatusUpdatedAt);

    private bool AgentBusy => _agent.HasActiveTurn;
    internal void AttachAgentControls(PaneHeader header) => header.AgentBar = _agentBar;
    private void AgentReply_Changing(TextBox sender, TextBoxTextChangingEventArgs args) => _promptFailure = null;
    private void AgentReply_Changed(object sender, RoutedEventArgs e) { if (AgentReply is not null) RefreshAgentChrome(); }
    private void AgentReply_FocusChanged(object sender, RoutedEventArgs e) => UpdatePromptGlyph();
    private void UpdatePromptGlyph() =>
        PromptGlyph.Opacity = AgentReply.IsReadOnly ? 0 : AgentReply.FocusState != FocusState.Unfocused ? 1 : 0.6;
    private void AgentReply_CompositionStarted(TextBox sender, TextCompositionStartedEventArgs args) => _replyIme = true;
    private void AgentReply_CompositionEnded(TextBox sender, TextCompositionEndedEventArgs args) => _replyIme = false;

    private void ApplyDisplayMode()
    {
        var bottom = TerminalPreferences.Current.TerminalStartPosition == TerminalStartPosition.Bottom;
        Terminal.SetBottomAligned(bottom);
        if (_session is not { } session || _state is not (SessionState.Starting or SessionState.Running or SessionState.Idle)) return;
        session.SetBottomAnchored(bottom);
        if (_state != SessionState.Starting) RefreshOutput(session);
    }

    private void RefreshAgentChrome()
    {
        if (AgentReply is null || _agent is null) return;
        var session = _session;
        if (_promptFont != Terminal.TerminalFontFamily)
        {
            _promptFont = Terminal.TerminalFontFamily;
            var font = new FontFamily(_promptFont + ", Cascadia Mono, Consolas");
            AgentReply.FontFamily = PromptGlyph.FontFamily = PromptHint.FontFamily = RequestPanel.DetailFont = _timelineView.Mono = font;
        }
        var request = _agent.Request;
        var text = AgentReply.Text;
        var busy = AgentBusy;
        var readOnly = busy && request is not QuestionRequest { AllowFreeform: true };
        var state = new ChromeState(session?.PromptState ?? ShellPromptState.Unknown, session?.AgentOwnsTerminal == true, session?.AgentEntryAvailable == false,
            !_surfaceOpen ? null : request switch
            {
                QuestionRequest { AllowFreeform: true } => "Answer the agent",
                QuestionRequest => "Pick an answer above",
                _ when busy => PausedReply,
                _ when _agent.CurrentTask?.Turns.Count == 0 => "Ask the agent",
                _ => "Ask a follow up",
            },
            !_surfaceOpen ? null : _promptFailure ?? (request switch
            {
                QuestionRequest { AllowFreeform: true } => "Enter answer · Shift+Enter new line",
                QuestionRequest => "",
                _ when busy => text.Length > 0 ? PausedReply : "",
                _ => "Enter send · Shift+Enter new line",
            }),
            readOnly, AgentReply.FocusState, CanSend(text), request is ApprovalRequest, busy, _agent.CanInterrupt, _agent.State, !_surfaceOpen, _viewActive && IsLoaded);
        if (_chrome == state) return;
        _chrome = state;
        if (state.Placeholder is { } placeholder) AgentReply.PlaceholderText = placeholder;
        if (state.Hint is { } hint)
        {
            PromptHint.Text = hint;
            PromptHint.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        AgentReply.IsReadOnly = readOnly;
        UpdatePromptGlyph();
        SubmitPrompt.IsEnabled = state.CanSend;
        SubmitPrompt.Opacity = readOnly ? 0 : 1;
        SubmitPrompt.IsHitTestVisible = !readOnly;

        _agentBar.ShowInteract(state.Owns);
        _agentBar.ShowShellStatus(state.Shell, state.EntryUnavailable);

        StopShortcut.Visibility = state.ApprovalPending ? Visibility.Collapsed : Visibility.Visible;
        var stopTip = state.ApprovalPending ? "Stop the agent's current turn" : "Stop the agent's current turn (Ctrl+C)";
        if (_stopTip != stopTip) { _stopTip = stopTip; ToolTipService.SetToolTip(StopAgentButton, stopTip); }
        StopAgentButton.Opacity = busy ? 1 : 0;
        StopAgentButton.IsEnabled = state.CanInterrupt;
        StopAgentButton.IsHitTestVisible = busy;
        AutomationProperties.SetAccessibilityView(StopAgentButton, busy ? AccessibilityView.Content : AccessibilityView.Raw);
        ToolTipService.SetToolTip(LeaveSurfaceButton, busy ? "Back to the terminal. The agent keeps working."
            : "Back to the terminal. This conversation is saved in its task card.");
        _agentBar.ShowTask(state.Agent, state.TaskShown);
    }

    private bool CanSend(string text)
    {
        if (!_surfaceOpen || _startingTask || string.IsNullOrWhiteSpace(text)) return false;
        if (PromptRouter.Parse(text) is AgentIntent) return true;
        return _agent.Request is QuestionRequest { AllowFreeform: true } || (_agent.CanSubmit && _agent.Request is null);
    }

    private CoalescedUpdate AgentUpdate => LazyInitializer.EnsureInitialized(ref _agentUpdate, () => new(
        () => DispatcherQueue.TryEnqueue(() => CompositionTarget.Rendering += AgentUpdate_Rendering),
        () =>
        {
            if (_state == SessionState.Disposed) return;
            _agentStatusUpdatedAt = DateTimeOffset.UtcNow;
            if (_viewActive) RefreshAgent(IsLoaded);
        }));
    private void AgentChanged() => AgentUpdate.Request();
    private void AgentUpdate_Rendering(object? sender, object args)
    {
        CompositionTarget.Rendering -= AgentUpdate_Rendering;
        AgentUpdate.Run();
    }

    private void SetAgentAnimation(bool active) => RefreshAgent(active);

    private void RefreshAgent(bool active)
    {
        if (_surfaceOpen) UpdateSurface(active);
        foreach (var (task, entry) in _cards.ToArray())
        {
            if (!_agent.Tasks.Contains(task)) RemoveCard(task);
            else entry.Card.Update(_agent, active);
        }
        RefreshAgentChrome();
    }

    private void UpdateSurface(bool active)
    {
        if (!AgentTimelineHost.Children.Contains(_timelineView))
        {
            AgentTimelineHost.Children.Add(_timelineView);
            AgentOutput.VerticalAnchorRatio = 1;
            AgentOutput.ViewChanged += (_, _) => _followOutput = AgentOutput.ScrollableHeight - AgentOutput.VerticalOffset < 24;
            AgentTimelineHost.SizeChanged += (_, _) => { if (_followOutput) AgentOutput.ChangeView(null, AgentOutput.ScrollableHeight, null, true); };
        }
        var task = _agent.CurrentTask;
        _timelineView.Update(_agent, task, active);
        var heading = task is { Turns.Count: > 0 } ? "Agent task" : "New agent task";
        if (_surfaceTitle != (heading, task?.Title))
        {
            _surfaceTitle = (heading, task?.Title);
            SurfaceTitle.Text = heading;
            ToolTipService.SetToolTip(SurfaceTitle, task?.Title);
        }
        RequestPanel.Update();
    }

    private bool PaneInputFocused()
    {
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot);
        return ReferenceEquals(focused, AgentReply) || ReferenceEquals(focused, Terminal.Input);
    }

    private void UpdateInputOwner(bool handoff)
    {
        var next = PromptFocusPolicy.Owner(_surfaceOpen);
        var transfer = PromptFocusPolicy.ShouldTransfer(_inputOwner, next, _viewActive && _inputActive,
            !_replyIme && FindBar.Visibility != Visibility.Visible, handoff);
        _inputOwner = next;
        if (!transfer) return;
        UpdateLayout();
        FocusTerminal();
    }

    private void OpenSurface(AgentTask task, bool explicitHandoff = true)
    {
        _agent.ActivateTask(task);
        if (_surfaceTask != task)
        {
            if (_surfaceTask is { } previous) previous.ReplyDraft = AgentReply.Text;
            _surfaceTask = task;
            AgentReply.Text = task.ReplyDraft;
            _promptFailure = null;
        }
        if (!_surfaceOpen)
        {
            var handoff = explicitHandoff || PaneInputFocused();
            StopSmoothScroll(false);
            _surfaceOpen = true;
            Terminal.SuspendRendering();
            _cursorTimer.Stop();
            _synchronizedRefreshTimer.Stop();
            if (_renderingSubscribed)
            {
                CompositionTarget.Rendering -= Refresh_Rendering;
                _renderingSubscribed = false;
            }
            Terminal.Visibility = HistoryScrollBar.Visibility = TaskCardLayer.Visibility = Visibility.Collapsed;
            AgentSurface.Visibility = Visibility.Visible;
            UpdateInputOwner(handoff);
        }
        UpdateSurface(_viewActive && IsLoaded);
        _followOutput = true;
        AgentOutput.UpdateLayout();
        AgentOutput.ChangeView(null, AgentOutput.ScrollableHeight, null, true);
        RefreshAgentChrome();
    }

    private void LeaveSurface()
    {
        if (!_surfaceOpen) return;
        _surfaceOpen = false;
        Terminal.ResumeRendering();
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
        _promptFailure = null;
        if (_agent.Request is QuestionRequest { AllowFreeform: true }) await SendToAgentAsync(text);
        else if (PromptRouter.Parse(text) is AgentIntent agent)
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
        AgentReply.Text = "";
        await _agent.SubmitAsync(text, new(directory.Path, directory.WslDistribution));
    }

    private async Task StartTaskAsync(string request, bool explicitHandoff = true)
    {
        if (AgentBusy)
        {
            if (_agent.RunningTask is not { } running) return;
            OpenSurface(running, explicitHandoff);
            if (request.Length > 0)
                AgentReply.Text = AgentReply.Text.Length == 0 ? request : AgentReply.Text + Environment.NewLine + request;
            return;
        }
        if (_startingTask) return;
        _startingTask = true;
        try
        {
            var task = _agent.BeginTask();
            if (_session is { } session && session.ReserveRows() is { } anchor)
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
            OpenSurface(task, explicitHandoff);
            if (request.Length > 0)
            {
                AgentReply.Text = request;
                await SendToAgentAsync(request);
            }
        }
        finally { _startingTask = false; RefreshAgentChrome(); }
    }

    private void RefreshTaskCards(TerminalFrame frame)
    {
        if (_cards.Count == 0) return;
        var geometry = Terminal.GeometryFor(frame);
        var cell = geometry.CellHeight;
        var shown = !_surfaceOpen && !frame.Viewport.IsAlternateScreen;
        var cursorRow = frame.Viewport.Offset + frame.Cursor.Row;
        var width = Math.Max(0, Math.Min(TaskCardLayer.ActualWidth, geometry.Columns * geometry.CellWidth));
        var height = Math.Max(0, 2 * cell - 4);
        var bottom = TaskCardLayer.ActualHeight;
        foreach (var (task, entry) in _cards)
        {
            if (entry.Session != _session) { _expiredCards.Add(task); continue; }
            if (!shown) { entry.Card.Place(false, 0, 0, 0); continue; }
            if (entry.Session.ReadAnchor(entry.Anchor) is not { } row) { _expiredCards.Add(task); continue; }
            var frameRow = row - frame.Viewport.Offset;
            var top = geometry.RowTop(frameRow);
            var overlapsPrompt = row <= cursorRow && cursorRow < row + 2;
            entry.Card.Place(!overlapsPrompt && top > -2 * cell && top < bottom && geometry.RowsBlank(frame, frameRow, 2), top + 2, width, height);
        }
        foreach (var task in _expiredCards) RemoveCard(task);
        _expiredCards.Clear();
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

    private void AllowAgentInput()
    {
        if (_session is not { AgentOwnsTerminal: true } session) return;
        LeaveSurface();
        session.AllowAgentInteractiveInput();
        RefreshAgentChrome();
        FocusTerminal();
    }

    private async void InterruptAgent_Click(object sender, RoutedEventArgs e)
    {
        _promptFailure = null;
        try { await _agent.InterruptAsync(); }
        catch (Exception ex) { ShowPromptHint(ex.Message); }
    }

    private void ShowPromptHint(string text)
    {
        _promptFailure = text;
        PromptHint.Text = text;
        PromptHint.Visibility = Visibility.Visible;
    }
}
