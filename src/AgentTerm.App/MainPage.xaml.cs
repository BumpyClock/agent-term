using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using AgentTerm.Terminal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Microsoft.UI.Input;
using Windows.ApplicationModel.DataTransfer;

using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Input;
using PointerPoint = Microsoft.UI.Input.PointerPoint;
using PointerUpdateKind = Microsoft.UI.Input.PointerUpdateKind;
using VirtualKey = Windows.System.VirtualKey;

namespace AgentTerm_App;

public sealed partial class MainPage : Page, IAsyncDisposable
{
    private enum SessionState { Idle, Starting, Running, Stopping, Disposed }

    private readonly DispatcherQueueTimer _chromeTimer;
    private readonly DispatcherQueueTimer _cursorTimer;
    private readonly DispatcherQueueTimer _synchronizedRefreshTimer;
    private readonly DispatcherQueueTimer _gridResizeTimer;
    private readonly TrailingThrottle _gridResize;
    private TerminalGrid? _sentGrid;
    private readonly DispatcherQueue _dispatcher;
    private TerminalSession? _session;
    private SessionState _state;
    private Task _startup = Task.CompletedTask;
    private Task _release = Task.CompletedTask;
    private Task? _dispose;
    private ushort _columns = 80;
    private ushort _rows = 24;
    private readonly TerminalKeyboardInput _keyboard = new();
    private readonly TerminalCompositionInput _composition = new();
    public bool IsComposing => _composition.IsComposing || _replyIme;
    private bool _clearingInput;
    private TerminalKeyboardInput.PhysicalKey? _textKey;
    private readonly long _blinkStarted = Environment.TickCount64;
    private bool _renderingSubscribed;
    private bool _frameRefreshQueued;
    private bool _frameRefreshPending;
    private bool _captureInFlight;
    private bool _captureRequested;
    private bool _captureCards;
    private bool _agentHandoffReadRunning;
    private (TerminalSession Session, string Request)? _pendingAgentHandoff;
    private int _outputRefreshQueued;
    private volatile bool _acceptOutputNotifications;
    private TerminalFrame? _presentedFrame;
    private TerminalPointerMode _pointerMode;
    private (double Maximum, double ViewportSize, double LargeChange, double Value, bool Enabled)? _scrollBarState;
    private readonly UISettings _uiSettings = new();
    private bool _animationsEnabled;
    private readonly DispatcherQueueTimer _selectionTimer;
    private bool _updatingScrollBar;
    private int _zoomWheelDelta;
    private int _applicationWheelDelta;
    private int _applicationHorizontalWheelDelta;
    private readonly HashSet<TerminalMouseButton> _applicationMouseButtons = [];
    private Point _applicationMousePosition;
    private int _fontSize;
    private TerminalAppearance _appearance;
    private bool _selecting;
    private bool _selectionMoved;
    private Point _selectionPointer;
    private int _selectionClicks;
    private long _lastClickTime;
    private int _lastClickColumn = -1, _lastClickRow = -1;
    private int _clickCount;
    private bool _viewActive = true;
    private bool _inputActive;

    internal void SetInputActive(bool active) => _inputActive = active;
    private bool _loadedOnce;
    private TerminalColor? _terminalBackground;
    private System.Collections.Generic.IReadOnlyList<ProgramStatus>? _terminalPrograms;
    private System.Collections.Generic.IReadOnlyList<ProgramStatus> _combinedPrograms = [];
    private ProgramStatus? _reportedAgentStatus;
    private TerminalSession? _metadataSession;
    private bool _metadataReadInFlight;
    private long _metadataReadStarted;
    private string _metadataTitle = "";
    private System.Collections.Generic.IReadOnlyList<ProgramStatus> _metadataPrograms = [];
    internal System.Collections.Generic.IReadOnlyList<ProgramStatus> ReadProgramStatuses()
    {
        if (_session is { } session) RefreshSessionMetadata(session);
        var records = _metadataSession == _session ? _metadataPrograms : [];
        if (AgentProgramStatus is not { } agent) return records;
        if (!ReferenceEquals(records, _terminalPrograms) || agent != _reportedAgentStatus)
        {
            _terminalPrograms = records;
            _reportedAgentStatus = agent;
            _combinedPrograms = [.. records, agent];
        }
        return _combinedPrograms;
    }

    public ShellProfile Profile { get; }
    internal string ProgramTitle
    {
        get
        {
            if (_session is { } session) RefreshSessionMetadata(session);
            return _metadataSession == _session ? _metadataTitle : "";
        }
    }
    internal PaneStatus.PaneStatusMonitor PaneStatus { get; } = new();
    internal TerminalColor TerminalBackground => _terminalBackground ?? CurrentTheme.Background;
    private TerminalTheme CurrentTheme => TerminalThemes.Resolve(_appearance.ColorScheme, _appearance.LightThemeId, _appearance.DarkThemeId,
        ActualTheme == ElementTheme.Light);
    internal event Action? TerminalBackgroundChanged;
    public event Action? SessionReady;
    public bool CanStopSession => _state is SessionState.Starting or SessionState.Running;
    public bool CanRestartSession => _state == SessionState.Idle;

    public MainPage() : this(ShellProfile.Default) { }

    public MainPage(ShellProfile profile)
    {
        Profile = profile;
        _appearance = TerminalPreferences.Current.ResolveAppearance(profile.Id);
        _agent = new(App.AgentHarness, () => _session);
        InitializeComponent();
        _dispatcher = DispatcherQueue;
        InitializeEffects();
        RequestPanel.Attach(_agent, AgentReply);
        _agentBar.TaskRequested += () => OpenLatestTask();
        _agentBar.InteractRequested += AllowAgentInput;
        _agent.Changed += AgentChanged;
        Terminal.Input.TextCompositionStarted += Input_CompositionStarted;
        InitializeTerminalAutomation();
        Terminal.Input.TextCompositionEnded += Input_CompositionEnded;
        Terminal.Input.TextChanging += Input_TextChanging;
        Terminal.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(Terminal_KeyUp), true);
        Terminal.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Terminal_PointerPressed), true);
        Terminal.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Terminal_PointerMoved), true);
        Terminal.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Terminal_PointerReleased), true);
        Terminal.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(Terminal_PointerCaptureLost), true);
        Terminal.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Terminal_PointerWheelChanged), true);
        InitializeLinks();
        ShellText.Text = profile.Name;
        _chromeTimer = DispatcherQueue.CreateTimer();
        _chromeTimer.Interval = TimeSpan.FromMilliseconds(250);
        _chromeTimer.Tick += Chrome_Tick;
        _cursorTimer = DispatcherQueue.CreateTimer();
        _cursorTimer.Interval = TimeSpan.FromMilliseconds(528);
        _cursorTimer.Tick += (_, _) => Terminal.SetCursorPhase(((Environment.TickCount64 - _blinkStarted) / 528) % 2 == 0);
        Terminal.BlinkingTextChanged += UpdateCursorBlink;
        _synchronizedRefreshTimer = DispatcherQueue.CreateTimer();
        _synchronizedRefreshTimer.IsRepeating = false;
        _synchronizedRefreshTimer.Tick += (_, _) => RequestFrameRefresh();
        _gridResizeTimer = DispatcherQueue.CreateTimer();
        _gridResizeTimer.IsRepeating = false;
        _gridResizeTimer.Interval = TimeSpan.FromMilliseconds(25);
        _gridResize = new(_gridResizeTimer.Start, _gridResizeTimer.Stop, SendSessionGrid);
        _gridResizeTimer.Tick += (_, _) => _gridResize.Flush();
        Terminal.MetricsChanged += UpdateGridSize;
        ConfigureHostUi();
        SetFontSize(_appearance.FontSize);
        Terminal.SetAcrylicBackground(_appearance.UseAcrylic);
        TerminalPreferences.Current.Changed += PreferencesChanged;
        _selectionTimer = DispatcherQueue.CreateTimer();
        _selectionTimer.Interval = TimeSpan.FromMilliseconds(50);
        _selectionTimer.Tick += SelectionTimer_Tick;
        _animationsEnabled = _uiSettings.AnimationsEnabled;
        _uiSettings.AnimationsEnabledChanged += UISettings_AnimationsEnabledChanged;
        ActualThemeChanged += Page_ActualThemeChanged;
        ApplyDisplayMode();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_loadedOnce)
        {
            _loadedOnce = true;
            await StartSessionAsync();
        }
        else if (_viewActive) SetActive(true);
    }

    public void SetActive(bool visible)
    {
        _viewActive = visible;
        SetAgentAnimation(visible && IsLoaded);
        if (_state == SessionState.Disposed) return;
        if (!visible)
        {
            CancelFind();
            StopRefresh();
            Terminal.SuspendRendering();
            PaneStatus.Refresh(PaneStatus.Directory, false, DateTimeOffset.UtcNow);
            CancelSelectionGesture();
            return;
        }
        if (_surfaceOpen) StopSmoothScroll(false);
        if (!_surfaceOpen) Terminal.ResumeRendering();
        EffectsViewed();
        if (_session is { } session && _state is SessionState.Running or SessionState.Idle)
        {
            var theme = CurrentTheme;
            QueueSessionUpdate(session, s => s.SetTheme(theme));
            UpdateGridSize();
            RefreshOutput(session);
            if (_state == SessionState.Running) StartRefresh();
        }
    }

    public void FocusTerminal() => FocusTerminal(FocusState.Programmatic);

    private void FocusTerminal(FocusState focusState)
    {
        if (!_viewActive || (focusState != FocusState.Pointer && !_inputActive) ||
            _state is not (SessionState.Starting or SessionState.Running or SessionState.Idle)) return;
        if (_inputOwner == PromptInputOwner.Composer) AgentReply.Focus(focusState);
        else Terminal.FocusInput(focusState);
    }

    internal void FocusUnclaimedInput()
    {
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot);
        if (focused is null || PaneInputFocused()) FocusTerminal();
    }

    private async void NewSession_Click(object sender, RoutedEventArgs e) => await RestartSessionAsync();
    public Task RestartSessionAsync() => StartSessionAsync();

    private async Task StartSessionAsync()
    {
        if (_state != SessionState.Idle || !_release.IsCompleted)
            return;

        if (_session is not null)
            await ReleaseSessionAsync();
        if (_state != SessionState.Idle) return;
        SetState(SessionState.Starting);
        StatusText.Text = $"Starting {Profile.Name}...";

        ErrorBanner.IsOpen = false;

        try
        {
            UpdateGridSize();
            var session = new TerminalSession(_columns, _rows, Profile);
            _session = session;
            session.OutputAvailable += Session_OutputAvailable;
            AttachEffects(session);
            session.SetBottomAnchored(TerminalPreferences.Current.TerminalStartPosition == TerminalStartPosition.Bottom);
            session.SetTheme(CurrentTheme);
            _startup = Task.Run(session.Start);
            await _startup;
            if (_session != session || _state != SessionState.Starting)
                return;

            ShellText.Text = session.ShellName;
            SetState(SessionState.Running);
            StatusText.Text = "Running · Local session";
            UpdateGridSize();
            RefreshOutput(session);
            if (_viewActive) StartRefresh();
            SessionReady?.Invoke();
            _ = ObserveExitAsync(session);
        }
        catch (Exception exception)
        {
            if (_state != SessionState.Disposed && _session is not null)
                await FailSessionAsync(exception);
            else if (_state == SessionState.Starting)
            {
                SetState(SessionState.Idle);
                ShowFailure(exception);
            }
        }
    }

    private async Task ObserveExitAsync(TerminalSession session)
    {
        try
        {
            var exitCode = await session.Completion;
            if (_session != session || _state != SessionState.Running)
                return;

            SetState(SessionState.Stopping);
            CancelSelectionGesture();
            RefreshOutput(session);
            await session.StopAsync();
            if (_session == session && _state == SessionState.Stopping)
            {
                RefreshOutput(session);
                SetState(SessionState.Idle);
                StatusText.Text = $"{Profile.Name} exited with code {exitCode} · Restart session to continue";
            }
        }
        catch (Exception exception)
        {
            if (_session == session && _state != SessionState.Disposed)
                await FailSessionAsync(exception);
        }
    }

    private async void Chrome_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_state != SessionState.Running || _session is not { } session)
            return;

        try
        {
            RefreshChrome(session);
            UpdateCursorBlink();
        }
        catch (Exception exception)
        {
            await FailSessionAsync(exception);
        }
    }

    private void StartRefresh()
    {
        if (!_viewActive || !IsLoaded || _state != SessionState.Running) return;
        _acceptOutputNotifications = true;
        RequestFrameRefresh();
        _chromeTimer.Start();
        UpdateCursorBlink();
    }

    private void StopRefresh()
    {
        StopSmoothScroll(false);
        _acceptOutputNotifications = false;
        Interlocked.Exchange(ref _outputRefreshQueued, 0);
        _frameRefreshPending = false;
        _chromeTimer.Stop();
        _cursorTimer.Stop();
        _synchronizedRefreshTimer.Stop();
        if (!_renderingSubscribed) return;
        CompositionTarget.Rendering -= Refresh_Rendering;
        _renderingSubscribed = false;
    }

    private void Session_OutputAvailable()
    {
        if (!_acceptOutputNotifications || Volatile.Read(ref _surfaceOpen) || Interlocked.Exchange(ref _outputRefreshQueued, 1) != 0) return;
        var queued = PerformanceCapture.Enabled ? Stopwatch.GetTimestamp() : 0;
        if (!_dispatcher.TryEnqueue(() =>
        {
            if (queued != 0) PerformanceCapture.Record(PerformanceMetric.OutputQueueDelay, Stopwatch.GetElapsedTime(queued).TotalMilliseconds, Terminal.CapturePaneId);
            if (!CanRefreshFrames)
            {
                Interlocked.Exchange(ref _outputRefreshQueued, 0);
                return;
            }
            StopSmoothScroll(false);
            RequestFrameRefresh();
        })) Interlocked.Exchange(ref _outputRefreshQueued, 0);
    }

    private bool CanRefreshFrames => _viewActive && IsLoaded && !_surfaceOpen && _state == SessionState.Running;

    private void RequestFrameRefresh()
    {
        if (_frameRefreshQueued || !CanRefreshFrames) return;
        if (_renderingSubscribed) _frameRefreshPending = true;
        else RefreshFrameNow();
    }

    private void UISettings_AnimationsEnabledChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args) =>
        _dispatcher.TryEnqueue(() =>
        {
            _animationsEnabled = sender.AnimationsEnabled;
            if (!_animationsEnabled) StopSmoothScroll();
            UpdateCursorBlink();
        });

    private void UpdateCursorBlink()
    {
        if (CanRefreshFrames && _animationsEnabled &&
            (Terminal.HasBlinkingText ||
             _presentedFrame?.Cursor is { Visible: true, Blink: true } && Terminal.Input.FocusState != FocusState.Unfocused))
        {
            if (!_cursorTimer.IsRunning) _cursorTimer.Start();
        }
        else
        {
            _cursorTimer.Stop();
            Terminal.SetCursorPhase(true);
        }
    }

    private void Refresh_Rendering(object? sender, object args)
    {
        CompositionTarget.Rendering -= Refresh_Rendering;
        _renderingSubscribed = false;
        if ((!_frameRefreshPending && !_scrollAnimation.IsActive) || _frameRefreshQueued || !CanRefreshFrames) return;
        _frameRefreshPending = false;
        _frameRefreshQueued = DispatcherQueue.TryEnqueue(RefreshQueuedFrame);
    }

    private void RefreshQueuedFrame()
    {
        _frameRefreshQueued = false;
        RefreshFrameNow();
    }

    private async void RefreshFrameNow()
    {
        if (!CanRefreshFrames || _session is not { } session) return;
        try
        {
            var scrolling = _scrollTick != 0;
            AdvanceSmoothScroll(session);
            RefreshTerminalFrame(session, refreshCards: scrolling);
            UpdateCursorBlink();
        }
        catch (Exception exception) { await FailSessionAsync(exception); }
    }

    private void RefreshLocalInput(TerminalSession session)
    {
        if (_presentedFrame is { } frame && (frame.HasSelection || !frame.Viewport.IsAtBottom)) RefreshTerminalFrame(session);
    }

    private void RefreshOutput(TerminalSession session)
    {
        RefreshChrome(session);
        RefreshTerminalFrame(session, refreshCards: true);
    }

    private void RefreshChrome(TerminalSession session)
    {
        RefreshFindHistory(session);
        RefreshAgentChrome();
        RefreshSessionMetadata(session);
    }

    private async void RefreshSessionMetadata(TerminalSession session)
    {
        if (_session != session || _state == SessionState.Disposed || _metadataReadInFlight) return;
        var now = Stopwatch.GetTimestamp();
        if (_metadataSession == session && Stopwatch.GetElapsedTime(_metadataReadStarted, now).TotalMilliseconds < 250) return;
        _metadataReadInFlight = true;
        _metadataReadStarted = now;
        try
        {
            var metadata = await session.DispatchAsync(owner =>
                (Title: owner.Title, Programs: owner.ReadProgramStatuses(), Directory: owner.ReadWorkingDirectory()));
            if (_session != session || _state == SessionState.Disposed) return;
            _metadataSession = session;
            _metadataTitle = metadata.Title;
            _metadataPrograms = metadata.Programs;
            PaneStatus.Refresh(metadata.Directory, _viewActive && _state == SessionState.Running, DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            if (_session == session && _state != SessionState.Disposed) await FailSessionAsync(exception);
        }
        finally { _metadataReadInFlight = false; }
    }

    private async void RefreshTerminalFrame(TerminalSession session, bool refreshCards = false)
    {
        if (_session != session || _state == SessionState.Disposed) return;
        _captureCards |= refreshCards;
        if (_captureInFlight) { _captureRequested = true; return; }
        _captureInFlight = true;
        _captureRequested = false;
        var cards = _captureCards;
        _captureCards = false;
        _frameRefreshPending = false;
        Interlocked.Exchange(ref _outputRefreshQueued, 0);
        var started = PerformanceCapture.Enabled ? Stopwatch.GetTimestamp() : 0;
        try
        {
            var snapshot = await session.CaptureFrameAsync();
            if (started != 0) PerformanceCapture.Record(PerformanceMetric.CaptureLatency, Stopwatch.GetElapsedTime(started).TotalMilliseconds, Terminal.CapturePaneId);
            if (_session != session || _state == SessionState.Disposed || !_viewActive || !IsLoaded || _surfaceOpen) return;
            _pointerMode = snapshot.PointerMode;
            var presenting = PerformanceCapture.Enabled ? Stopwatch.GetTimestamp() : 0;
            try { PresentTerminalFrame(snapshot.Frame, cards); }
            finally
            {
                if (presenting != 0) PerformanceCapture.Record(PerformanceMetric.FrameRefresh, Stopwatch.GetElapsedTime(presenting).TotalMilliseconds, Terminal.CapturePaneId);
            }
            _synchronizedRefreshTimer.Stop();
            if (CanRefreshFrames)
            {
                if (!_renderingSubscribed)
                {
                    CompositionTarget.Rendering += Refresh_Rendering;
                    _renderingSubscribed = true;
                }
                if (snapshot.PendingRefreshDelay is { } delay)
                {
                    _synchronizedRefreshTimer.Interval = delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1);
                    _synchronizedRefreshTimer.Start();
                }
            }
            DrainAgentHandoff();
            if (snapshot.PromptState == ShellPromptState.Ready) ReadAgentHandoff(session);
        }
        catch (TerminalOperationQueueFullException)
        {
            _captureCards |= cards;
            await Task.Delay(16);
            _captureRequested = true;
        }
        catch (Exception exception)
        {
            if (_session == session && _state != SessionState.Disposed) await FailSessionAsync(exception);
        }
        finally
        {
            _captureInFlight = false;
            if (_captureRequested && _session is { } current && _state != SessionState.Disposed && _viewActive && IsLoaded && !_surfaceOpen)
            {
                if (CanRefreshFrames) RequestFrameRefresh();
                else RefreshTerminalFrame(current, _captureCards);
            }
        }
    }

    private void DrainAgentHandoff()
    {
        if (_pendingAgentHandoff is not { } pending) return;
        if (_session != pending.Session || _state == SessionState.Disposed)
        {
            _pendingAgentHandoff = null;
            return;
        }
        if (_startingTask || _state != SessionState.Running) return;
        _pendingAgentHandoff = null;
        _ = StartTaskAsync(pending.Request, explicitHandoff: false);
    }

    private async void ReadAgentHandoff(TerminalSession session)
    {
        if (_agentHandoffReadRunning || _startingTask || _pendingAgentHandoff is not null) return;
        _agentHandoffReadRunning = true;
        try
        {
            var request = await session.DispatchAsync(s => s.PromptState == ShellPromptState.Ready ? s.TakeAgentRequest() : null);
            if (_session != session || request is null) return;
            _pendingAgentHandoff = (session, request);
            DrainAgentHandoff();
        }
        catch (Exception error)
        {
            if (_session == session && _state != SessionState.Disposed) await FailSessionAsync(error);
        }
        finally { _agentHandoffReadRunning = false; }
    }

    private void PresentTerminalFrame(TerminalFrame frame, bool refreshCards)
    {
        var previous = _presentedFrame;
        if (frame.OverscanBelow.Length == 0) Terminal.SetScrollFraction(0);
        if (!ReferenceEquals(previous, frame))
        {
            _presentedFrame = frame;
            Terminal.SetFrame(frame);
            UpdateCursorBlink();
            RefreshTaskCards(frame);
            if (_terminalBackground != frame.Background)
            {
                _terminalBackground = frame.Background;
                TerminalBackgroundChanged?.Invoke();
            }
        }
        else if (refreshCards) RefreshTaskCards(frame);
        RefreshLinkFeedback();
        if (!refreshCards && previous?.Viewport == frame.Viewport) return;
        var viewport = frame.Viewport;
        (double Maximum, double ViewportSize, double LargeChange, double Value, bool Enabled) state =
            (viewport.MaximumOffset, viewport.Rows, Math.Max(1, viewport.Rows - 1), viewport.Offset, viewport.MaximumOffset > 0);
        if (_scrollBarState == state) return;
        var written = _scrollBarState;
        _scrollBarState = state;
        _updatingScrollBar = true;
        try
        {
            if (written?.Maximum != state.Maximum) HistoryScrollBar.Maximum = state.Maximum;
            if (written?.ViewportSize != state.ViewportSize) HistoryScrollBar.ViewportSize = state.ViewportSize;
            if (written?.LargeChange != state.LargeChange) HistoryScrollBar.LargeChange = state.LargeChange;
            if (written is null) HistoryScrollBar.SmallChange = 1;
            if (written?.Value != state.Value) HistoryScrollBar.Value = state.Value;
            if (written?.Enabled != state.Enabled) HistoryScrollBar.IsEnabled = state.Enabled;
        }
        finally { _updatingScrollBar = false; }
    }

    private async void Output_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        try { QueueGridSize(); }
        catch (Exception exception)
        {
            if (_state == SessionState.Running) await FailSessionAsync(exception);
            else if (_state == SessionState.Idle) ShowFailure(exception);
        }
    }

    private void UpdateGridSize()
    {
        if (MeasureGrid()) _gridResize.Flush();
    }

    private void QueueGridSize()
    {
        if (MeasureGrid()) _gridResize.Request();
    }

    private bool MeasureGrid()
    {
        if (Terminal.ActualWidth <= 0 || Terminal.ActualHeight <= 0) return false;
        var columns = (ushort)Math.Clamp(Math.Floor(Terminal.ActualWidth / Terminal.CellWidth), 2, 1000);
        var rows = (ushort)Math.Clamp(Math.Floor(Terminal.ActualHeight / Terminal.CellHeight), 1, 1000);
        if (columns != _columns || rows != _rows || HostCellWidth != _hostCellWidth || HostCellHeight != _hostCellHeight)
        {
            StopSmoothScroll(false);
            _columns = columns;
            _rows = rows;
            _hostCellWidth = HostCellWidth;
            _hostCellHeight = HostCellHeight;
        }
        GridSizeText.Text = $"{_columns} x {_rows}";
        return true;
    }

    private void SendSessionGrid()
    {
        TerminalGrid grid = new(_columns, _rows, _hostCellWidth, _hostCellHeight);
        if (_state is not (SessionState.Running or SessionState.Idle) || _session is not { } session || grid == _sentGrid) return;
        _sentGrid = grid;
        QueueSessionUpdate(session, s => s.Resize(grid.Columns, grid.Rows, grid.CellWidth, grid.CellHeight), refresh: true);
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint parameter, out uint value, uint flags);

    private int WheelLines()
    {
        if (!SystemParametersInfo(0x0068, 0, out var lines, 0)) return 3;
        return lines == uint.MaxValue ? Math.Max(1, _rows - 1) : (int)Math.Min(lines, 1000);
    }

    private static bool Down(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private static TerminalModifiers Modifiers()
    {
        var mods = TerminalModifiers.None;
        if (Down(VirtualKey.Shift)) mods |= TerminalModifiers.Shift;
        if (Down(VirtualKey.Control)) mods |= TerminalModifiers.Control;
        if (Down(VirtualKey.Menu)) mods |= TerminalModifiers.Alt;
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) mods |= TerminalModifiers.Super;
        return mods;
    }

    private static TerminalModifiers KeyboardModifiers()
    {
        var mods = Modifiers();
        if (Down(VirtualKey.RightShift)) mods |= TerminalModifiers.ShiftRight;
        if (Down(VirtualKey.RightControl)) mods |= TerminalModifiers.ControlRight;
        if (Down(VirtualKey.RightMenu)) mods |= TerminalModifiers.AltRight;
        if (Down(VirtualKey.RightWindows)) mods |= TerminalModifiers.SuperRight;
        if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.CapitalLock) & CoreVirtualKeyStates.Locked) != 0)
            mods |= TerminalModifiers.CapsLock;
        if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.NumberKeyLock) & CoreVirtualKeyStates.Locked) != 0)
            mods |= TerminalModifiers.NumLock;
        return mods;
    }

    private void PreferencesChanged()
    {
        var previous = _appearance;
        _appearance = TerminalPreferences.Current.ResolveAppearance(Profile.Id);
        if (previous.FontSize != _appearance.FontSize)
            SetFontSize(_appearance.FontSize);
        else if (previous.FontFamily != _appearance.FontFamily)
            SetFontSize(_fontSize);
        Terminal.SetAcrylicBackground(_appearance.UseAcrylic);
        ApplyEffectOptions();
        ApplyDisplayMode();
        Page_ActualThemeChanged(this, EventArgs.Empty);
    }

    private void Page_ActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_state == SessionState.Disposed || _session is not { } session) return;
        try
        {
            var theme = CurrentTheme;
            QueueSessionUpdate(session, s => s.SetTheme(theme), refresh: _viewActive);
        }
        catch (Exception exception) { ShowInteractionFailure(exception); }
    }

    private void SetFontSize(int size)
    {
        _fontSize = Math.Clamp(size, 8, 36);
        Terminal.SetTerminalFont(_fontSize, _appearance.FontFamily);
    }

    public void ShowFind()
    {
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus(FocusState.Keyboard);
        FindBox.SelectAll();
        FindMatch(true);
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e) => FindMatch(true);
    private void FindMatchCase_Changed(object sender, RoutedEventArgs e) => FindMatch(true);
    private void FindPrevious_Click(object sender, RoutedEventArgs e) => FindMatch(false);
    private void FindNext_Click(object sender, RoutedEventArgs e) => FindMatch(true);
    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFind();

    private void FindBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseFind();
        }
        else if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            FindMatch(!Down(VirtualKey.Shift));
        }
    }

    private void CloseFind()
    {
        CancelFind();
        ReleaseFind();
        FindBar.Visibility = Visibility.Collapsed;
        FocusTerminal();
    }

    private void HistoryScrollBar_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        try
        {
            if (_updatingScrollBar || _session is not { } session) return;
            StopSmoothScroll(false);
            var offset = (long)Math.Round(e.NewValue);
            QueueSessionUpdate(session, s => s.ScrollTo(offset), refresh: true);

        }
        catch (Exception exception) { ShowInteractionFailure(exception); }
    }

    private void Terminal_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (_session is not { } session) return;
            var point = e.GetCurrentPoint(Terminal);
            var delta = point.Properties.MouseWheelDelta;
            if (Down(VirtualKey.Control))
            {
                StopSmoothScroll();
                _zoomWheelDelta += delta;
                var zoomNotches = _zoomWheelDelta / 120;
                _zoomWheelDelta %= 120;
                if (zoomNotches != 0) SetFontSize(_fontSize + zoomNotches);
                e.Handled = true;
                return;
            }
            if (_state == SessionState.Running && !TerminalScrollAnimation.UseHistoryScrolling(_pointerMode.MouseTracking, Down(VirtualKey.Shift)))
            {
                StopSmoothScroll();
                bool horizontal = point.Properties.IsHorizontalMouseWheel;
                ref int remainder = ref (horizontal ? ref _applicationHorizontalWheelDelta : ref _applicationWheelDelta);
                remainder += delta;
                var applicationNotches = remainder / 120;
                remainder %= 120;
                var button = horizontal
                    ? applicationNotches > 0 ? TerminalMouseButton.WheelRight : TerminalMouseButton.WheelLeft
                    : applicationNotches > 0 ? TerminalMouseButton.WheelUp : TerminalMouseButton.WheelDown;
                for (var index = 0; index < Math.Abs(applicationNotches); index++)
                    SendApplicationMouse(point.Position, TerminalMouseAction.Press, button);
                e.Handled = true;
                return;
            }
            if (point.Properties.IsHorizontalMouseWheel) return;
            ScrollHistoryWheel(session, -delta / 120.0 * WheelLines());
            e.Handled = true;

        }
        catch (Exception exception) { ShowInteractionFailure(exception); }
    }

    private TerminalMouseGeometry MouseGeometry() => new(
        (uint)(_columns * Terminal.CellWidth), (uint)(_rows * Terminal.CellHeight),
        (uint)Terminal.CellWidth, (uint)Terminal.CellHeight,
        (float)(Terminal.XamlRoot?.RasterizationScale ?? 1));

    private bool SendApplicationMouse(Point point, TerminalMouseAction action, TerminalMouseButton? button, TerminalModifiers? capturedModifiers = null)
    {
        if (_state != SessionState.Running || _session is not { } session) return false;
        var geometry = Terminal.Geometry;
        var position = action == TerminalMouseAction.Press && button is not (TerminalMouseButton.WheelUp or TerminalMouseButton.WheelDown
            or TerminalMouseButton.WheelLeft or TerminalMouseButton.WheelRight)
            ? geometry.ToFrame(point) : geometry.ToFrameClamped(point);
        var modifiers = capturedModifiers ?? Modifiers();
        if (action == TerminalMouseAction.Release && button is { } released && _applicationMouseButtons.Contains(released))
            modifiers &= ~TerminalModifiers.Shift;
        if ((modifiers & TerminalModifiers.Shift) != 0 || !_pointerMode.MouseTracking) return false;
        var mouse = new TerminalMouseEvent(action, button, modifiers, (float)position.X, (float)position.Y);
        var mouseGeometry = MouseGeometry();
        var displayed = _presentedFrame;
        QueueSessionUpdate(session, s => s.SendMouse(mouse, mouseGeometry, displayed));
        _applicationMousePosition = position;
        if (action == TerminalMouseAction.Press) RefreshLocalInput(session);
        return true;
    }

    private static TerminalMouseButton? ChangedMouseButton(PointerPoint point) => point.Properties.PointerUpdateKind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => TerminalMouseButton.Left,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => TerminalMouseButton.Right,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => TerminalMouseButton.Middle,
        PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton1Released => TerminalMouseButton.Back,
        PointerUpdateKind.XButton2Pressed or PointerUpdateKind.XButton2Released => TerminalMouseButton.Forward,
        _ => null
    };

    private void ReleaseApplicationMouse()
    {
        if (_state == SessionState.Running && _session is { } session)
        {
            var modifiers = Modifiers() & ~TerminalModifiers.Shift;
            var position = _applicationMousePosition;
            var geometry = MouseGeometry();
            var displayed = _presentedFrame;
            foreach (var button in _applicationMouseButtons)
                QueueSessionUpdate(session, s => s.SendMouse(new(TerminalMouseAction.Release, button, modifiers,
                    (float)position.X, (float)position.Y), geometry, displayed));
        }
        _applicationMouseButtons.Clear();
    }

    private readonly SemaphoreSlim _pointerGestureGate = new(1, 1);
    private (PointerPoint Point, TerminalModifiers Modifiers, TerminalSession? Session)? _pendingPointerMotion;
    private long _pointerCaptureGeneration;

    private async void Terminal_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_session is not { } pressSession) return;
        var point = e.GetCurrentPoint(Terminal);
        var modifiers = Modifiers();
        e.Handled = true;
        await _pointerGestureGate.WaitAsync();
        try
        {
            StopSmoothScroll();
            if (_session != pressSession) return;
            var session = pressSession;
            if (point.Properties.IsLeftButtonPressed &&
                TerminalKeyboardInput.ShortcutModifiers(modifiers) == (TerminalModifiers.Control | TerminalModifiers.Shift))
            {
                ReleaseApplicationMouse();
                FocusTerminal(FocusState.Pointer);
                e.Handled = true;
                if (_presentedFrame is { } frame && Terminal.Geometry.TryCellAt(point.Position, out var outputColumn, out var outputRow))
                    QueueSessionUpdate(session, s => s.TrySelectFrameCommandOutput(frame, outputColumn, outputRow), refresh: true);
                return;
            }
            if (await HandleLinkPressAsync(e, point, modifiers)) return;
            if (_session != session) return;
            var button = ChangedMouseButton(point);
            if (modifiers.HasFlag(TerminalModifiers.Shift) && _applicationMouseButtons.Count != 0) ReleaseApplicationMouse();
            if (!_selecting && button is not null && SendApplicationMouse(point.Position, TerminalMouseAction.Press, button, modifiers))
            {
                FocusTerminal(FocusState.Pointer);
                var grid = Terminal.Geometry;
                if (point.Position.Y >= grid.Top && point.Position.Y < grid.Bottom)
                {
                    _applicationMouseButtons.Add(button.Value);
                    if (Terminal.CapturePointer(e.Pointer)) _pointerCaptureGeneration++;
                }
                e.Handled = true;
                return;
            }
            if (point.Properties.IsRightButtonPressed)
            {
                FocusTerminal(FocusState.Pointer);
                e.Handled = true;
                if (await TryShowLinkMenuAsync(point.Position)) return;
                if (_session != session) return;
                if (_presentedFrame?.HasSelection == true) CopySelection(session);
                else _ = PasteAsync(session);
                return;
            }
            if (!point.Properties.IsLeftButtonPressed || (button is not null && button != TerminalMouseButton.Left)) return;
            FocusTerminal(FocusState.Pointer);
            if (!Terminal.Geometry.TryCellAt(point.Position, out var column, out var row))
            {
                if (!modifiers.HasFlag(TerminalModifiers.Shift) && _presentedFrame?.HasSelection == true) { QueueSessionUpdate(session, s => s.ClearSelection()); RefreshOutput(session); }
                e.Handled = true;
                return;
            }
            _selectionPointer = point.Position;
            var now = Environment.TickCount64;
            _clickCount = now - _lastClickTime <= 500 && column == _lastClickColumn && row == _lastClickRow
                ? Math.Min(_clickCount + 1, 3) : 1;
            _lastClickTime = now;
            _lastClickColumn = column;
            _lastClickRow = row;
            _selectionClicks = _clickCount;
            _selectionMoved = false;
            _selecting = true;
            if (modifiers.HasFlag(TerminalModifiers.Shift) && _presentedFrame?.HasSelection == true)
            {
                QueueSessionUpdate(session, s => s.ExtendSelection(column, row));
                _selectionMoved = true;
            }
            else
            {
                var clicks = _clickCount;
                var rectangular = modifiers.HasFlag(TerminalModifiers.Alt);
                QueueSessionUpdate(session, s => s.BeginSelection(column, row, clicks, rectangular));
            }
            if (Terminal.CapturePointer(e.Pointer)) _pointerCaptureGeneration++;
            _selectionTimer.Start();
            RefreshOutput(session);
            e.Handled = true;

        }
        catch (Exception exception) { ShowInteractionFailure(exception); }
        finally
        {
            try { FlushPendingPointerMotion(); }
            finally { _pointerGestureGate.Release(); }
        }
    }

    private async void Terminal_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Terminal);
        if (_pointerGestureGate.CurrentCount == 0)
        {
            _pendingPointerMotion = (point, Modifiers(), _session);
            e.Handled = true;
            return;
        }
        await _pointerGestureGate.WaitAsync();
        try
        {
            if (ProcessPointerMotion(point, Modifiers()))
            {
                e.Handled = true;
                if (_session is { } session) await session.DispatchAsync(_ => { });
            }
        }
        catch (Exception error) { ShowInteractionFailure(error); }
        finally
        {
            try { FlushPendingPointerMotion(); }
            finally { _pointerGestureGate.Release(); }
        }
    }

    private void FlushPendingPointerMotion()
    {
        if (_pendingPointerMotion is not { } motion) return;
        _pendingPointerMotion = null;
        if (_session == motion.Session) ProcessPointerMotion(motion.Point, motion.Modifiers);
    }

    private bool ProcessPointerMotion(PointerPoint point, TerminalModifiers modifiers)
    {
        try
        {
            if (_session is not { } session) return false;
            if (_linkPress is not null) return true;
            if (!_selecting)
            {
                TerminalMouseButton? button = point.Properties.IsLeftButtonPressed ? TerminalMouseButton.Left
                    : point.Properties.IsRightButtonPressed ? TerminalMouseButton.Right
                    : point.Properties.IsMiddleButtonPressed ? TerminalMouseButton.Middle
                    : point.Properties.IsXButton1Pressed ? TerminalMouseButton.Back
                    : point.Properties.IsXButton2Pressed ? TerminalMouseButton.Forward : null;
                return SendApplicationMouse(point.Position, TerminalMouseAction.Motion, button, modifiers);
            }
            _selectionPointer = point.Position;
            var (column, row) = Terminal.Geometry.ClampedCellAt(_selectionPointer);
            if (column != _lastClickColumn || row != _lastClickRow) _selectionMoved = true;
            if (_selectionMoved)
            {
                QueueSessionUpdate(session, s => s.ExtendSelection(column, row));
                RefreshOutput(session);
            }
            return true;

        }
        catch (Exception exception) { ShowInteractionFailure(exception); }
        return false;
    }

    private async void Terminal_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var releaseSession = _session;
        var releasedPoint = e.GetCurrentPoint(Terminal);
        var modifiers = Modifiers();
        e.Handled = true;
        await _pointerGestureGate.WaitAsync();
        try
        {
            FlushPendingPointerMotion();
            if (_session is not { } session || session != releaseSession) return;
            if (await HandleLinkReleaseAsync(e, releasedPoint)) return;
            if (_selecting && ChangedMouseButton(releasedPoint) is { } releasedButton && releasedButton != TerminalMouseButton.Left)
            {
                e.Handled = true;
                return;
            }
            if (!_selecting)
            {
                var point = releasedPoint;
                var button = ChangedMouseButton(point);
                if (button is not null && _applicationMouseButtons.Contains(button.Value))
                {
                    if (SendApplicationMouse(point.Position, TerminalMouseAction.Release, button, modifiers)) e.Handled = true;
                    _applicationMouseButtons.Remove(button.Value);
                }
                if (_applicationMouseButtons.Count == 0) Terminal.ReleasePointerCapture(e.Pointer);
                return;
            }
            ProcessPointerMotion(releasedPoint, modifiers);
            _selecting = false;
            _selectionTimer.Stop();
            QueueSessionUpdate(session, s => s.EndSelection());
            if (!_selectionMoved && _selectionClicks == 1) QueueSessionUpdate(session, s => s.ClearSelection());
            Terminal.ReleasePointerCapture(e.Pointer);
            RefreshOutput(session);
            e.Handled = true;

        }
        catch (Exception exception) { ShowInteractionFailure(exception); }
        finally { _pointerGestureGate.Release(); }
    }

    private async void Terminal_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        var generation = _pointerCaptureGeneration;
        var captureSession = _session;
        await _pointerGestureGate.WaitAsync();
        try
        {
            if (generation != _pointerCaptureGeneration || captureSession != _session) return;
            _pendingPointerMotion = null;
            CancelLinkPress();
            ReleaseApplicationMouse();
            if (!_selecting) return;
            _selecting = false;
            _selectionTimer.Stop();
            if (_session is { } selectingSession) QueueSessionUpdate(selectingSession, s => s.EndSelection());
            if (_session is { } session) RefreshOutput(session);

        }
        catch (Exception exception) { ShowInteractionFailure(exception); }
        finally { _pointerGestureGate.Release(); }
    }

    private void SelectionTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            if (_pointerGestureGate.CurrentCount == 0 || !_selecting || _session is not { } session) return;
            var direction = _selectionPointer.Y < Terminal.Geometry.Top ? -1 : _selectionPointer.Y >= Terminal.ActualHeight ? 1 : 0;
            if (direction == 0) return;
            QueueSessionUpdate(session, s => s.ScrollLines(direction));
            RefreshOutput(session);
            var (column, row) = Terminal.Geometry.ClampedCellAt(_selectionPointer);
            QueueSessionUpdate(session, s => s.ExtendSelection(column, row));
            _selectionMoved = true;
            RefreshOutput(session);

        }
        catch (Exception exception) { ShowInteractionFailure(exception); }
    }

    private void ShowInteractionFailure(Exception exception)
    {
        _selecting = false;
        _selectionTimer.Stop();
        ErrorBanner.Title = "Terminal interaction unavailable";
        ErrorBanner.Message = exception.Message;
        ErrorBanner.IsOpen = true;
    }

    private async void Terminal_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_session is not { } session || _state is not (SessionState.Running or SessionState.Idle)) return;
        StopSmoothScroll();
        if (_composition.IsComposing) return;
        if (TryShowLinkMenuFromKeyboard(e)) return;
        var status = e.KeyStatus;
        var physical = new TerminalKeyboardInput.PhysicalKey(status.ScanCode, status.IsExtendedKey);
        var key = IsSidedModifier(e.OriginalKey) ? e.OriginalKey : e.Key;
        var rawMods = KeyboardModifiers();
        var mods = TerminalKeyboardInput.ShortcutModifiers(rawMods);
        const TerminalModifiers ctrlAlt = TerminalModifiers.Control | TerminalModifiers.Alt;
        var altGr = (mods & ctrlAlt) == ctrlAlt && Down(VirtualKey.RightMenu);
        var ctrlShift = mods == (TerminalModifiers.Control | TerminalModifiers.Shift);
        if (HandleKeyboardSelection(session, e, physical, key, mods)) return;
        var hasSelection = _presentedFrame?.HasSelection == true;
        var selectionCopy = (key == VirtualKey.C && mods == TerminalModifiers.Control && hasSelection) ||
            (key == VirtualKey.Insert && mods == TerminalModifiers.Control && hasSelection) ||
            (key == VirtualKey.Enter && mods == TerminalModifiers.None && hasSelection);
        var findShortcut = ctrlShift && key == VirtualKey.F;
        var taskShortcut = ctrlShift && key == VirtualKey.Y && (_agent.RunningTask ?? _agent.CurrentTask) is not null;
        var keyCode = (uint)key;
        var zoomShortcut = (mods & TerminalModifiers.Control) != 0 && (mods & (TerminalModifiers.Alt | TerminalModifiers.Super)) == 0 &&
            (keyCode is 0xBB or 0xBD or 0x6B or 0x6D or 0x30 or 0x60);
        var historyShortcut = ctrlShift && key is VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End;
        var promptShortcut = mods == (TerminalModifiers.Control | TerminalModifiers.Alt | TerminalModifiers.Shift) && key is VirtualKey.Up or VirtualKey.Down;
        var shortcut = promptShortcut || findShortcut || taskShortcut || zoomShortcut || (ctrlShift && (key is VirtualKey.Tab or VirtualKey.V || (key == VirtualKey.C && hasSelection))) ||
            ((mods == TerminalModifiers.Shift && key == VirtualKey.Insert) || (mods == TerminalModifiers.Control && key == VirtualKey.V)) || selectionCopy || historyShortcut ||
            (key == VirtualKey.Escape && mods == TerminalModifiers.None && hasSelection);
        try
        {
            if ((shortcut && !_keyboard.IsForwarded(physical)) || (status.WasKeyDown && _keyboard.IsShortcut(physical)))
            {
                _keyboard.KeyDown(physical, (uint)key, mods, status.WasKeyDown, TerminalKeyboardInput.Route.Shortcut);
                e.Handled = true;
                if (status.WasKeyDown && !historyShortcut && !zoomShortcut) return;
                if (promptShortcut)
                {
                    var direction = key == VirtualKey.Up ? -1 : 1;
                    QueueSessionUpdate(session, s => s.NavigatePrompt(direction));
                    RefreshOutput(session);
                    return;
                }
                if (findShortcut)
                {
                    ShowFind();
                    return;
                }
                if (taskShortcut)
                {
                    OpenLatestTask();
                    return;
                }
                if (zoomShortcut)
                {
                    if (keyCode is 0x30 or 0x60) SetFontSize(_appearance.FontSize);
                    else SetFontSize(_fontSize + (keyCode is 0xBB or 0x6B ? 1 : -1));
                    return;
                }
                if (key == VirtualKey.Tab)
                {
                    if (SessionHeader.Visibility == Visibility.Visible) NewSessionButton.Focus(FocusState.Keyboard);
                    else FocusManager.TryMoveFocus(FocusNavigationDirection.Previous);
                    return;
                }
                if (historyShortcut)
                {
                    var page = (int)_rows;
                    var wheelLines = WheelLines();
                    switch (key)
                    {
                        case VirtualKey.Up: QueueSessionUpdate(session, s => s.ScrollLines(-wheelLines)); break;
                        case VirtualKey.Down: QueueSessionUpdate(session, s => s.ScrollLines(wheelLines)); break;
                        case VirtualKey.PageUp: QueueSessionUpdate(session, s => s.ScrollLines(-page)); break;
                        case VirtualKey.PageDown: QueueSessionUpdate(session, s => s.ScrollLines(page)); break;
                        case VirtualKey.Home: QueueSessionUpdate(session, s => s.ScrollTo(0)); break;
                        case VirtualKey.End: QueueSessionUpdate(session, s => s.ScrollToBottom()); break;
                    }
                    RefreshOutput(session);
                    return;
                }
                if (key == VirtualKey.Escape)
                {
                    QueueSessionUpdate(session, s => s.ClearSelection());
                    SetKeyboardMarkMode(false);
                    RefreshOutput(session);
                    return;
                }
                if (key == VirtualKey.V || (key == VirtualKey.Insert && mods == TerminalModifiers.Shift))
                {
                    await PasteAsync(session);
                    return;
                }
                CopySelection(session);
                return;
            }
            if (_state != SessionState.Running) return;

            var virtualKey = (uint)key;
            if (!IsSidedModifier(key) && virtualKey is not (0x10 or 0x11 or 0x12)) SetKeyboardMarkMode(false);
            var special = virtualKey is < 0x20 or (>= 0x21 and <= 0x2F) or (>= 0x60 and <= 0x87 and not 0x6C)
                or 0x5D or 0x90 or 0x91 || IsSidedModifier(key);
            var direct = special || (mods & TerminalModifiers.Super) != 0 || (!altGr && (mods & ctrlAlt) != 0);
            var layoutCodepoint = TerminalKeyboardLayout.UnshiftedCodepoint(virtualKey, physical.ScanCode);
            var action = _keyboard.KeyDown(physical, virtualKey, rawMods, status.WasKeyDown,
                direct ? TerminalKeyboardInput.Route.Direct : TerminalKeyboardInput.Route.Text,
                !direct && altGr ? ctrlAlt : TerminalModifiers.None, layoutCodepoint);
            _textKey = direct ? null : physical;
            if (direct)
            {
                e.Handled = true;
                ForwardKey(session, new(virtualKey, rawMods, action, null, physical, TerminalModifiers.None, layoutCodepoint));
                RefreshLocalInput(session);
            }
        }
        catch (Exception exception) { await FailSessionAsync(exception); }
    }

    private static bool IsSidedModifier(VirtualKey key) => key is VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.LeftMenu or VirtualKey.RightMenu
        or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private async void CopySelection(TerminalSession session)
    {
        try
        {
            var text = await session.DispatchAsync(s => s.ReadSelectionText());
            if (text is null) return;
            SetKeyboardMarkMode(false);
            if (text.Length == 0)
            {
                QueueSessionUpdate(session, s => s.ClearSelection());
                RefreshOutput(session);
                return;
            }
            var data = new DataPackage();
            data.SetText(text);
            Clipboard.SetContent(data);
            QueueSessionUpdate(session, s => s.ClearSelection());
            RefreshOutput(session);
        }
        catch (Exception exception) { ShowClipboardFailure(exception); }
    }

    private async Task PasteAsync(TerminalSession session)
    {
        if (_state != SessionState.Running) return;
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
            var text = await content.GetTextAsync();
            if (_session == session && _state == SessionState.Running)
            {
                await session.DispatchAsync(s => s.Paste(text));
                RefreshLocalInput(session);
            }
        }
        catch (Exception exception) { ShowClipboardFailure(exception); }
    }

    private void ShowClipboardFailure(Exception exception)
    {
        ErrorBanner.Title = "Clipboard unavailable";
        ErrorBanner.Message = exception.Message;
        ErrorBanner.IsOpen = true;
    }

    private async void Terminal_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_session is not { } session || _state is not (SessionState.Running or SessionState.Idle)) return;
        try
        {
            var status = e.KeyStatus;
            var physical = new TerminalKeyboardInput.PhysicalKey(status.ScanCode, status.IsExtendedKey);
            if (_textKey == physical) _textKey = null;
            if (_keyboard.KeyUp(physical, KeyboardModifiers()) is { } key && _state == SessionState.Running)
                ForwardKey(session, key);
        }
        catch (Exception exception) { await FailSessionAsync(exception); }
    }

    private void Input_CompositionStarted(TextBox sender, TextCompositionStartedEventArgs args)
    {
        StopSmoothScroll();
        _composition.Start(sender.Text);
        _textKey = null;
        Terminal.SetCompositionVisible(true);
    }

    private async void Input_CompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
    {
        var text = _composition.Finish(sender.Text);
        Terminal.SetCompositionVisible(false);
        ClearInput();
        if (text.Length == 0 || _state != SessionState.Running || _session is not { } session) return;
        try
        {
            ForwardKey(session, new(0, TerminalModifiers.None, TerminalKeyAction.Press, text));
            RefreshLocalInput(session);
        }
        catch (Exception exception) { await FailSessionAsync(exception); }
    }

    private async void Input_TextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
    {
        StopSmoothScroll();
        if (_clearingInput || _composition.IsComposing) return;
        var text = sender.Text;
        var physical = _textKey;
        _textKey = null;
        ClearInput();
        if (text.Length == 0 || _state != SessionState.Running || _session is not { } session) return;
        try
        {
            if (_keyboard.Text(physical, text) is { } input)
            {
                ForwardKey(session, input);
                RefreshLocalInput(session);
            }
        }
        catch (Exception exception) { await FailSessionAsync(exception); }
    }

    private void ClearInput()
    {
        _clearingInput = true;
        try { Terminal.Input.Text = string.Empty; }
        finally { _clearingInput = false; }
    }

    private void Terminal_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_state == SessionState.Running && _session is { } session) QueueSessionUpdate(session, s => s.SetFocus(true));
        Terminal.RefreshCursor();
        UpdateCursorBlink();
    }

    private async void Terminal_LostFocus(object sender, RoutedEventArgs e)
    {
        _cursorTimer.Stop();
        _composition.Cancel();
        SetKeyboardMarkMode(false);
        _textKey = null;
        Terminal.SetCompositionVisible(false);
        ClearInput();
        var releases = _keyboard.Reset();
        Terminal.RefreshCursor();
        if (_state != SessionState.Running || _session is not { } session) return;
        try
        {
            ReleaseApplicationMouse();
            foreach (var key in releases)
                ForwardKey(session, key);
            QueueSessionUpdate(session, s => s.SetFocus(false));
        }
        catch (Exception exception) { await FailSessionAsync(exception); }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopSessionAsync();

    public async Task StopSessionAsync()
    {
        if (!CanStopSession) return;
        SetState(SessionState.Stopping);
        CancelSelectionGesture();
        StatusText.Text = $"Stopping {Profile.Name}...";
        try
        {
            if (_session is { } session)
            {
                await _startup;
                await session.StopAsync();
                if (_session == session) RefreshOutput(session);
            }
        }
        catch (Exception exception)
        {
            await FailSessionAsync(exception);
            return;
        }
        if (_state != SessionState.Disposed)
        {
            SetState(SessionState.Idle);
            StatusText.Text = "Session stopped · New session to continue";
        }
    }

    private async Task FailSessionAsync(Exception exception)
    {
        if (exception is TerminalOperationQueueFullException)
        {
            _sentGrid = null;
            ShowInteractionFailure(exception);
            return;
        }
        SetState(SessionState.Stopping);
        await ReleaseSessionAsync();
        if (_state != SessionState.Disposed)
        {
            SetState(SessionState.Idle);
            ShowFailure(exception);
        }
    }

    private void ShowFailure(Exception exception)
    {
        StatusText.Text = "Session unavailable · New session to retry";
        ErrorBanner.Title = "Shell session unavailable";
        ErrorBanner.Message = exception.Message;
        ErrorBanner.IsOpen = true;
    }

    private void SetState(SessionState state)
    {
        _state = state;
        if (state != SessionState.Running)
        {
            StopRefresh();
            PaneStatus.Refresh(state == SessionState.Starting ? null : PaneStatus.Directory, false, DateTimeOffset.UtcNow);
        }
        Terminal.IsEnabled = state is SessionState.Starting or SessionState.Running || (state == SessionState.Idle && _session is not null);
        Terminal.Input.IsReadOnly = state != SessionState.Running;
        SessionHeader.Visibility = state == SessionState.Idle ? Visibility.Visible : Visibility.Collapsed;
        SessionFooter.Visibility = state == SessionState.Running ? Visibility.Collapsed : Visibility.Visible;
        StopButton.IsEnabled = state is SessionState.Starting or SessionState.Running;
        NewSessionButton.IsEnabled = state == SessionState.Idle;
    }

    private void CancelSelectionGesture()
    {
        ResetLinkInteraction();
        ReleaseApplicationMouse();
        _selectionTimer.Stop();
        if (_selecting)
        {
            _selecting = false;
            if (_session is { } selectingSession) QueueSessionUpdate(selectingSession, s => s.EndSelection());
            Terminal.ReleasePointerCaptures();
        }
        _zoomWheelDelta = 0;
        _applicationWheelDelta = 0;
        _applicationHorizontalWheelDelta = 0;
        _lastClickTime = 0;
        _lastClickColumn = _lastClickRow = -1;
        _clickCount = 0;
    }

    private Task ReleaseSessionAsync()
    {
        _pointerMode = default;
        _pendingAgentHandoff = null;
        CancelFind();
        StopRefresh();
        CancelSelectionGesture();
        _gridResize.Cancel();
        _sentGrid = null;
        if (_session is not { } session)
            return _release;

        session.OutputAvailable -= Session_OutputAvailable;
        DetachEffects(session);
        _session = null;
        return _release = ReleaseCoreAsync(session, _startup);
    }

    private async Task ReleaseCoreAsync(TerminalSession session, Task startup)
    {
        try { await startup; }
        catch { }

        try { await session.DisposeAsync(); }
        catch (Exception exception)
        {
            if (_state != SessionState.Disposed)
                ShowFailure(exception);
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            CancelFind();
            StopRefresh();
            Terminal.SuspendRendering();
        }
        SetAgentAnimation(false);
        CancelSelectionGesture();
    }

    public ValueTask DisposeAsync() => new(_dispose ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        SetState(SessionState.Disposed);
        await _agent.DisposeAsync();
        PaneStatus.Dispose();
        _selectionTimer.Stop();
        await ReleaseSessionAsync();
        DisposeEffects();
        TerminalPreferences.Current.Changed -= PreferencesChanged;
        ActualThemeChanged -= Page_ActualThemeChanged;
        _uiSettings.AnimationsEnabledChanged -= UISettings_AnimationsEnabledChanged;
        Terminal.ReleaseResources();
    }
}

internal readonly record struct TerminalGrid(ushort Columns, ushort Rows, uint CellWidth, uint CellHeight);
