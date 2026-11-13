using System;
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
    private int _outputRefreshQueued;
    private volatile bool _acceptOutputNotifications;
    private TerminalFrame? _presentedFrame;
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
    internal System.Collections.Generic.IReadOnlyList<ProgramStatus> ReadProgramStatuses()
    {
        var records = _session?.ReadProgramStatuses() ?? [];
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
    internal string ProgramTitle => _session?.Title ?? "";
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
        _synchronizedRefreshTimer = DispatcherQueue.CreateTimer();
        _synchronizedRefreshTimer.IsRepeating = false;
        _synchronizedRefreshTimer.Tick += (_, _) => RequestFrameRefresh();
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
            session.SetTheme(CurrentTheme);
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
            session.Resize(_columns, _rows, HostCellWidth, HostCellHeight);
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
            EffectsSessionEnded();
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
        if (!_dispatcher.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _outputRefreshQueued, 0);
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
        PaneStatus.Refresh(session.ReadWorkingDirectory(), _viewActive && _state == SessionState.Running, DateTimeOffset.UtcNow);
    }

    private void RefreshTerminalFrame(TerminalSession session, bool refreshCards = false)
    {
        if (!_startingTask && session.PromptState == ShellPromptState.Ready && session.TakeAgentRequest() is { } request)
            _ = StartTaskAsync(request, explicitHandoff: false);
        _frameRefreshPending = false;
        var frame = session.ReadFrame();
        _synchronizedRefreshTimer.Stop();
        if (CanRefreshFrames)
        {
            if (!_renderingSubscribed)
            {
                CompositionTarget.Rendering += Refresh_Rendering;
                _renderingSubscribed = true;
            }
            if (session.PendingFrameRefreshDelay is { } delay)
            {
                _synchronizedRefreshTimer.Interval = delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1);
                _synchronizedRefreshTimer.Start();
            }
        }
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
        try { UpdateGridSize(); }
        catch (Exception exception)
        {
            if (_state == SessionState.Running) await FailSessionAsync(exception);
            else if (_state == SessionState.Idle) ShowFailure(exception);
        }
    }

    private void UpdateGridSize()
    {
        if (Terminal.ActualWidth <= 0 || Terminal.ActualHeight <= 0) return;
        var columns = (ushort)Math.Clamp(Math.Floor(Terminal.ActualWidth / Terminal.CellWidth), 2, 1000);
        var rows = (ushort)Math.Clamp(Math.Floor(Terminal.ActualHeight / Terminal.CellHeight), 1, 1000);
        if (columns != _columns || rows != _rows || HostCellWidth != _hostCellWidth || HostCellHeight != _hostCellHeight)
        {
            StopSmoothScroll(false);
            _columns = columns;
            _rows = rows;
            _hostCellWidth = HostCellWidth;
            _hostCellHeight = HostCellHeight;
            if (_state is SessionState.Running or SessionState.Idle && _session is not null)
            {
                _session.Resize(columns, rows, _hostCellWidth, _hostCellHeight);
                RefreshOutput(_session);
            }
        }
        GridSizeText.Text = $"{_columns} x {_rows}";
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
            session.SetTheme(CurrentTheme);
            if (_viewActive) RefreshOutput(session);
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
            session.ScrollTo((long)Math.Round(e.NewValue));
            RefreshOutput(session);

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
            if (_state == SessionState.Running && !TerminalScrollAnimation.UseHistoryScrolling(session.IsMouseTrackingEnabled, Down(VirtualKey.Shift)))
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

    private bool SendApplicationMouse(Point point, TerminalMouseAction action, TerminalMouseButton? button)
    {
        if (_state != SessionState.Running || _session is not { } session) return false;
        var geometry = Terminal.Geometry;
        var position = action == TerminalMouseAction.Press && button is not (TerminalMouseButton.WheelUp or TerminalMouseButton.WheelDown
            or TerminalMouseButton.WheelLeft or TerminalMouseButton.WheelRight)
            ? geometry.ToFrame(point) : geometry.ToFrameClamped(point);
        var modifiers = Modifiers();
        if (action == TerminalMouseAction.Release && button is { } released && _applicationMouseButtons.Contains(released))
            modifiers &= ~TerminalModifiers.Shift;
        if (!session.SendMouse(new(action, button, modifiers, (float)position.X, (float)position.Y), MouseGeometry()))
            return false;
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
            foreach (var button in _applicationMouseButtons)
                session.SendMouse(new(TerminalMouseAction.Release, button, Modifiers() & ~TerminalModifiers.Shift,
                    (float)_applicationMousePosition.X, (float)_applicationMousePosition.Y), MouseGeometry());
        _applicationMouseButtons.Clear();
    }

    private async void Terminal_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        StopSmoothScroll();
        try
        {
            if (_session is not { } session) return;
            var point = e.GetCurrentPoint(Terminal);
            if (HandleLinkPress(e, point)) return;
            var button = ChangedMouseButton(point);
            if (Down(VirtualKey.Shift) && _applicationMouseButtons.Count != 0) ReleaseApplicationMouse();
            if (!_selecting && button is not null && SendApplicationMouse(point.Position, TerminalMouseAction.Press, button))
            {
                FocusTerminal(FocusState.Pointer);
                var grid = Terminal.Geometry;
                if (point.Position.Y >= grid.Top && point.Position.Y < grid.Bottom)
                {
                    _applicationMouseButtons.Add(button.Value);
                    Terminal.CapturePointer(e.Pointer);
                }
                e.Handled = true;
                return;
            }
            if (point.Properties.IsRightButtonPressed)
            {
                FocusTerminal(FocusState.Pointer);
                e.Handled = true;
                if (TryShowLinkMenu(point.Position)) return;
                if (_presentedFrame?.HasSelection == true) CopySelection(session);
                else await PasteAsync(session);
                return;
            }
            if (!point.Properties.IsLeftButtonPressed || (button is not null && button != TerminalMouseButton.Left)) return;
            FocusTerminal(FocusState.Pointer);
            if (!Terminal.Geometry.TryCellAt(point.Position, out var column, out var row))
            {
                if (!Down(VirtualKey.Shift) && _presentedFrame?.HasSelection == true) { session.ClearSelection(); RefreshOutput(session); }
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
            if (Down(VirtualKey.Shift) && _presentedFrame?.HasSelection == true)
            {
                session.ExtendSelection(column, row);
                _selectionMoved = true;
            }
            else
                session.BeginSelection(column, row, _clickCount, Down(VirtualKey.Menu));
            Terminal.CapturePointer(e.Pointer);
            _selectionTimer.Start();
            RefreshOutput(session);
            e.Handled = true;

        }
        catch (Exception exception) { ShowInteractionFailure(exception); }
    }

    private void Terminal_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (_session is not { } session) return;
            if (_linkPress is not null) { e.Handled = true; return; }
            var point = e.GetCurrentPoint(Terminal);
            if (!_selecting)
            {
                TerminalMouseButton? button = point.Properties.IsLeftButtonPressed ? TerminalMouseButton.Left
                    : point.Properties.IsRightButtonPressed ? TerminalMouseButton.Right
                    : point.Properties.IsMiddleButtonPressed ? TerminalMouseButton.Middle
                    : point.Properties.IsXButton1Pressed ? TerminalMouseButton.Back
                    : point.Properties.IsXButton2Pressed ? TerminalMouseButton.Forward : null;
                if (SendApplicationMouse(point.Position, TerminalMouseAction.Motion, button)) e.Handled = true;
                return;
            }
            _selectionPointer = point.Position;
            var (column, row) = Terminal.Geometry.ClampedCellAt(_selectionPointer);
            if (column != _lastClickColumn || row != _lastClickRow) _selectionMoved = true;
            if (_selectionMoved)
            {
                session.ExtendSelection(column, row);
                RefreshOutput(session);
            }
            e.Handled = true;

        }
        catch (Exception exception) { ShowInteractionFailure(exception); }
    }

    private void Terminal_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (_session is not { } session) return;
            var releasedPoint = e.GetCurrentPoint(Terminal);
            if (HandleLinkRelease(e, releasedPoint)) return;
            if (_selecting && ChangedMouseButton(releasedPoint) is { } releasedButton && releasedButton != TerminalMouseButton.Left)
            {
                e.Handled = true;
                return;
            }
            if (!_selecting)
            {
                var point = e.GetCurrentPoint(Terminal);
                var button = ChangedMouseButton(point);
                if (button is not null && _applicationMouseButtons.Contains(button.Value))
                {
                    if (SendApplicationMouse(point.Position, TerminalMouseAction.Release, button)) e.Handled = true;
                    _applicationMouseButtons.Remove(button.Value);
                }
                if (_applicationMouseButtons.Count == 0) Terminal.ReleasePointerCapture(e.Pointer);
                return;
            }
            _selecting = false;
            _selectionTimer.Stop();
            session.EndSelection();
            if (!_selectionMoved && _selectionClicks == 1) session.ClearSelection();
            Terminal.ReleasePointerCapture(e.Pointer);
            RefreshOutput(session);
            e.Handled = true;

        }
        catch (Exception exception) { ShowInteractionFailure(exception); }
    }

    private void Terminal_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            CancelLinkPress();
            ReleaseApplicationMouse();
            if (!_selecting) return;
            _selecting = false;
            _selectionTimer.Stop();
            _session?.EndSelection();
            if (_session is { } session) RefreshOutput(session);

        }
        catch (Exception exception) { ShowInteractionFailure(exception); }
    }

    private void SelectionTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            if (!_selecting || _session is not { } session) return;
            var direction = _selectionPointer.Y < Terminal.Geometry.Top ? -1 : _selectionPointer.Y >= Terminal.ActualHeight ? 1 : 0;
            if (direction == 0) return;
            session.ScrollLines(direction);
            RefreshOutput(session);
            var (column, row) = Terminal.Geometry.ClampedCellAt(_selectionPointer);
            session.ExtendSelection(column, row);
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
        var mods = Modifiers();
        const TerminalModifiers ctrlAlt = TerminalModifiers.Control | TerminalModifiers.Alt;
        var altGr = (mods & ctrlAlt) == ctrlAlt && Down(VirtualKey.RightMenu);
        var ctrlShift = mods == (TerminalModifiers.Control | TerminalModifiers.Shift);
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
            if (shortcut || (status.WasKeyDown && _keyboard.IsShortcut(physical)))
            {
                _keyboard.KeyDown(physical, (uint)key, mods, status.WasKeyDown, TerminalKeyboardInput.Route.Shortcut);
                e.Handled = true;
                if (status.WasKeyDown && !historyShortcut && !zoomShortcut) return;
                if (promptShortcut)
                {
                    session.NavigatePrompt(key == VirtualKey.Up ? -1 : 1);
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
                    switch (key)
                    {
                        case VirtualKey.Up: session.ScrollLines(-WheelLines()); break;
                        case VirtualKey.Down: session.ScrollLines(WheelLines()); break;
                        case VirtualKey.PageUp: session.ScrollLines(-page); break;
                        case VirtualKey.PageDown: session.ScrollLines(page); break;
                        case VirtualKey.Home: session.ScrollTo(0); break;
                        case VirtualKey.End: session.ScrollToBottom(); break;
                    }
                    RefreshOutput(session);
                    return;
                }
                if (key == VirtualKey.Escape)
                {
                    session.ClearSelection();
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
            var special = virtualKey is < 0x20 or (>= 0x21 and <= 0x2F) or (>= 0x60 and <= 0x87 and not 0x6C)
                or 0x5D or 0x90 or 0x91 || IsSidedModifier(key);
            var direct = special || (mods & TerminalModifiers.Super) != 0 || (!altGr && (mods & ctrlAlt) != 0);
            var action = _keyboard.KeyDown(physical, virtualKey, mods, status.WasKeyDown,
                direct ? TerminalKeyboardInput.Route.Direct : TerminalKeyboardInput.Route.Text,
                !direct && altGr ? ctrlAlt : TerminalModifiers.None);
            _textKey = direct ? null : physical;
            if (direct)
            {
                e.Handled = true;
                session.SendKey(virtualKey, mods, action);
                RefreshLocalInput(session);
            }
        }
        catch (Exception exception) { await FailSessionAsync(exception); }
    }

    private static bool IsSidedModifier(VirtualKey key) => key is VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.LeftMenu or VirtualKey.RightMenu
        or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private void CopySelection(TerminalSession session)
    {
        try
        {
            var text = session.ReadSelectionText();
            if (text is null) return;
            if (text.Length == 0)
            {
                session.ClearSelection();
                RefreshOutput(session);
                return;
            }
            var data = new DataPackage();
            data.SetText(text);
            Clipboard.SetContent(data);
            session.ClearSelection();
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
                session.Paste(text);
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
            if (_keyboard.KeyUp(physical, Modifiers()) is { } key && _state == SessionState.Running)
                session.SendKey(key.Key, key.Modifiers, key.Action);
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
            session.SendKey(0, TerminalModifiers.None, TerminalKeyAction.Press, text);
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
                session.SendKey(input.Key, input.Modifiers, input.Action, input.Text);
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
        if (_state == SessionState.Running) _session?.SetFocus(true);
        Terminal.RefreshCursor();
        UpdateCursorBlink();
    }

    private async void Terminal_LostFocus(object sender, RoutedEventArgs e)
    {
        _cursorTimer.Stop();
        _composition.Cancel();
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
                session.SendKey(key.Key, key.Modifiers, key.Action);
            session.SetFocus(false);
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
            _session?.EndSelection();
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
        CancelFind();
        StopRefresh();
        CancelSelectionGesture();
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
