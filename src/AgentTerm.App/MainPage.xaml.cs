using System;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using AgentTerm.Terminal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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

    private readonly DispatcherQueueTimer _refreshTimer;
    private TerminalSession? _session;
    private SessionState _state;
    private Task _startup = Task.CompletedTask;
    private Task _release = Task.CompletedTask;
    private Task? _dispose;
    private ushort _columns = 80;
    private ushort _rows = 24;
    private readonly TerminalKeyboardInput _keyboard = new();
    private int _blinkTicks;
    private readonly UISettings _uiSettings = new();
    private readonly DispatcherQueueTimer _selectionTimer;
    private bool _updatingScrollBar;
    private int _wheelDelta;
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
    private bool _loadedOnce;
    public ShellProfile Profile { get; }
    public event Action? SessionReady;
    public bool CanStopSession => _state is SessionState.Starting or SessionState.Running;
    public bool CanRestartSession => _state == SessionState.Idle;

    public MainPage() : this(ShellProfile.Default) { }

    public MainPage(ShellProfile profile)
    {
        Profile = profile;
        _appearance = TerminalPreferences.Current.ResolveAppearance(profile.Id);
        InitializeComponent();
        ShellText.Text = profile.Name;
        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(33);
        _refreshTimer.Tick += Refresh_Tick;
        Terminal.MetricsChanged += UpdateGridSize;
        SetFontSize(_appearance.FontSize);
        Terminal.SetAcrylicBackground(_appearance.UseAcrylic);
        TerminalPreferences.Current.Changed += PreferencesChanged;
        _selectionTimer = DispatcherQueue.CreateTimer();
        _selectionTimer.Interval = TimeSpan.FromMilliseconds(50);
        _selectionTimer.Tick += SelectionTimer_Tick;
        ActualThemeChanged += Page_ActualThemeChanged;
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
        if (_state == SessionState.Disposed) return;
        if (!visible)
        {
            _refreshTimer.Stop();
            CancelSelectionGesture();
            return;
        }
        if (_session is { } session && _state is SessionState.Running or SessionState.Idle)
        {
            session.SetLightTheme(ActualTheme == ElementTheme.Light);
            UpdateGridSize();
            RefreshOutput(session);
            if (_state == SessionState.Running) _refreshTimer.Start();
        }
    }

    public void FocusTerminal()
    {
        if (_viewActive && _state is SessionState.Running or SessionState.Idle)
            Terminal.Focus(FocusState.Programmatic);
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
            session.SetLightTheme(ActualTheme == ElementTheme.Light);
            // Publish ownership before starting. Closing the window can then await startup and release it.
            _startup = Task.Run(session.Start);
            await _startup;
            if (_session != session || _state != SessionState.Starting)
                return;

            ShellText.Text = session.ShellName;
            SetState(SessionState.Running);
            StatusText.Text = "Running · Local session";
            UpdateGridSize();
            session.Resize(_columns, _rows);
            RefreshOutput(session);
            if (_viewActive) _refreshTimer.Start();
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
                _refreshTimer.Stop();
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

    private async void Refresh_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_state != SessionState.Running || _session is not { } session)
            return;

        try
        {
            RefreshOutput(session);
            _blinkTicks++;
            Terminal.SetCursorPhase(!_uiSettings.AnimationsEnabled || (_blinkTicks / 16) % 2 == 0);
        }
        catch (Exception exception)
        {
            await FailSessionAsync(exception);
        }
    }

    private void RefreshOutput(TerminalSession session)
    {
        var frame = session.ReadFrame();
        Terminal.SetFrame(frame);
        _updatingScrollBar = true;
        try
        {
            HistoryScrollBar.Maximum = frame.Viewport.MaximumOffset;
            HistoryScrollBar.ViewportSize = frame.Viewport.Rows;
            HistoryScrollBar.LargeChange = Math.Max(1, frame.Viewport.Rows - 1);
            HistoryScrollBar.SmallChange = 1;
            HistoryScrollBar.Value = frame.Viewport.Offset;
            HistoryScrollBar.IsEnabled = frame.Viewport.MaximumOffset > 0;
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
        if (columns != _columns || rows != _rows)
        {
            _columns = columns;
            _rows = rows;
            if (_state is SessionState.Running or SessionState.Idle && _session is not null)
            {
                _session.Resize(columns, rows);
                // Publish the resized grid before the surface repaints at its new size.
                RefreshOutput(_session);
            }
        }
        GridSizeText.Text = $"{_columns} x {_rows}";
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out uint value, uint flags);

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
    }

    private void Page_ActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_state == SessionState.Disposed || _session is not { } session) return;
        try
        {
            session.SetLightTheme(ActualTheme == ElementTheme.Light);
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
        FindBar.Visibility = Visibility.Collapsed;
        FocusTerminal();
    }

    private void FindMatch(bool forward)
    {
        if (_session is not { } session || FindBox is null || FindStatus is null || FindMatchCase is null) return;
        try
        {
            if (FindBox.Text.Length == 0)
            {
                session.Find(string.Empty, forward, FindMatchCase.IsChecked == true);
                FindStatus.Text = string.Empty;
                return;
            }
            var found = session.Find(FindBox.Text, forward, FindMatchCase.IsChecked == true);
            FindStatus.Text = found ? string.Empty : "No matches";
            RefreshOutput(session);
        }
        catch (Exception exception) { ShowInteractionFailure(exception); }
    }

    private void HistoryScrollBar_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        try
        {
            if (_updatingScrollBar || _session is not { } session) return;
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
                _zoomWheelDelta += delta;
                var zoomNotches = _zoomWheelDelta / 120;
                _zoomWheelDelta %= 120;
                if (zoomNotches != 0) SetFontSize(_fontSize + zoomNotches);
                e.Handled = true;
                return;
            }
            if (_state == SessionState.Running && !Down(VirtualKey.Shift) && session.IsMouseTrackingEnabled)
            {
                bool horizontal = point.Properties.IsHorizontalMouseWheel;
                ref int remainder = ref (horizontal ? ref _applicationHorizontalWheelDelta : ref _applicationWheelDelta);
                remainder += delta;
                var applicationNotches = remainder / 120;
                remainder %= 120;
                var button = horizontal
                    ? applicationNotches > 0 ? TerminalMouseButton.WheelRight : TerminalMouseButton.WheelLeft
                    : applicationNotches > 0 ? TerminalMouseButton.WheelUp : TerminalMouseButton.WheelDown;
                for (var index = 0; index < Math.Abs(applicationNotches); index++)
                    SendApplicationMouse(point, TerminalMouseAction.Press, button);
                e.Handled = true;
                return;
            }
            if (point.Properties.IsHorizontalMouseWheel) return;
            _wheelDelta += delta;
            var notches = _wheelDelta / 120;
            _wheelDelta %= 120;
            if (notches != 0)
            {
                session.ScrollLines(-notches * WheelLines());
                RefreshOutput(session);
            }
            e.Handled = true;

        }
        catch (Exception exception) { ShowInteractionFailure(exception); }
    }

    private (int Column, int Row) CellAt(Point point) =>
        (Math.Clamp((int)Math.Floor(point.X / Terminal.CellWidth), 0, _columns - 1),
         Math.Clamp((int)Math.Floor(point.Y / Terminal.CellHeight), 0, _rows - 1));

    private TerminalMouseGeometry MouseGeometry() => new(
        (uint)(_columns * Terminal.CellWidth), (uint)(_rows * Terminal.CellHeight),
        (uint)Terminal.CellWidth, (uint)Terminal.CellHeight,
        (float)(Terminal.XamlRoot?.RasterizationScale ?? 1));

    private bool SendApplicationMouse(PointerPoint point, TerminalMouseAction action, TerminalMouseButton? button)
    {
        if (_state != SessionState.Running || _session is not { } session) return false;
        var position = point.Position;
        var modifiers = Modifiers();
        // A gesture sent to the application must end there even if Shift was pressed during the drag.
        if (action == TerminalMouseAction.Release && button is { } released && _applicationMouseButtons.Contains(released))
            modifiers &= ~TerminalModifiers.Shift;
        if (!session.SendMouse(new(action, button, modifiers, (float)position.X, (float)position.Y), MouseGeometry()))
            return false;
        _applicationMousePosition = position;
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
        try
        {
            if (_session is not { } session) return;
            var point = e.GetCurrentPoint(Terminal);
            var button = ChangedMouseButton(point);
            if (Down(VirtualKey.Shift) && _applicationMouseButtons.Count != 0) ReleaseApplicationMouse();
            if (!_selecting && button is not null && SendApplicationMouse(point, TerminalMouseAction.Press, button))
            {
                Terminal.Focus(FocusState.Pointer);
                _applicationMouseButtons.Add(button.Value);
                Terminal.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }
            if (point.Properties.IsRightButtonPressed)
            {
                e.Handled = true;
                if (session.ReadFrame().HasSelection) CopySelection(session);
                else await PasteAsync(session);
                return;
            }
            if (!point.Properties.IsLeftButtonPressed || (button is not null && button != TerminalMouseButton.Left)) return;
            Terminal.Focus(FocusState.Pointer);
            _selectionPointer = point.Position;
            var (column, row) = CellAt(point.Position);
            var now = Environment.TickCount64;
            _clickCount = now - _lastClickTime <= 500 && column == _lastClickColumn && row == _lastClickRow
                ? Math.Min(_clickCount + 1, 3) : 1;
            _lastClickTime = now;
            _lastClickColumn = column;
            _lastClickRow = row;
            _selectionClicks = _clickCount;
            _selectionMoved = false;
            _selecting = true;
            if (Down(VirtualKey.Shift) && session.ReadFrame().HasSelection)
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
            var point = e.GetCurrentPoint(Terminal);
            if (!_selecting)
            {
                TerminalMouseButton? button = point.Properties.IsLeftButtonPressed ? TerminalMouseButton.Left
                    : point.Properties.IsRightButtonPressed ? TerminalMouseButton.Right
                    : point.Properties.IsMiddleButtonPressed ? TerminalMouseButton.Middle
                    : point.Properties.IsXButton1Pressed ? TerminalMouseButton.Back
                    : point.Properties.IsXButton2Pressed ? TerminalMouseButton.Forward : null;
                if (SendApplicationMouse(point, TerminalMouseAction.Motion, button)) e.Handled = true;
                return;
            }
            _selectionPointer = point.Position;
            var (column, row) = CellAt(_selectionPointer);
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
                    if (SendApplicationMouse(point, TerminalMouseAction.Release, button)) e.Handled = true;
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
            var direction = _selectionPointer.Y < 0 ? -1 : _selectionPointer.Y >= Terminal.ActualHeight ? 1 : 0;
            if (direction == 0) return;
            session.ScrollLines(direction);
            var (column, row) = CellAt(_selectionPointer);
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
        var status = e.KeyStatus;
        var physical = new TerminalKeyboardInput.PhysicalKey(status.ScanCode, status.IsExtendedKey);
        var key = IsSidedModifier(e.OriginalKey) ? e.OriginalKey : e.Key;
        var mods = Modifiers();
        const TerminalModifiers ctrlAlt = TerminalModifiers.Control | TerminalModifiers.Alt;
        var altGr = (mods & ctrlAlt) == ctrlAlt && Down(VirtualKey.RightMenu);
        var ctrlShift = mods == (TerminalModifiers.Control | TerminalModifiers.Shift);
        var hasSelection = session.ReadFrame().HasSelection;
        var selectionCopy = (key == VirtualKey.C && mods == TerminalModifiers.Control && hasSelection) ||
            (key == VirtualKey.Insert && mods == TerminalModifiers.Control && hasSelection) ||
            (key == VirtualKey.Enter && mods == TerminalModifiers.None && hasSelection);
        var findShortcut = ctrlShift && key == VirtualKey.F;
        var keyCode = (uint)key;
        var zoomShortcut = (mods & TerminalModifiers.Control) != 0 && (mods & (TerminalModifiers.Alt | TerminalModifiers.Super)) == 0 &&
            (keyCode is 0xBB or 0xBD or 0x6B or 0x6D or 0x30 or 0x60);
        var historyShortcut = ctrlShift && key is VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End;
        var shortcut = findShortcut || zoomShortcut || (ctrlShift && (key is VirtualKey.Tab or VirtualKey.V || (key == VirtualKey.C && hasSelection))) ||
            ((mods == TerminalModifiers.Shift && key == VirtualKey.Insert) || (mods == TerminalModifiers.Control && key == VirtualKey.V)) || selectionCopy || historyShortcut ||
            (key == VirtualKey.Escape && mods == TerminalModifiers.None && session.ReadFrame().HasSelection);
        try
        {
            if (shortcut || (status.WasKeyDown && _keyboard.IsShortcut(physical)))
            {
                _keyboard.KeyDown(physical, (uint)key, mods, status.WasKeyDown, TerminalKeyboardInput.Route.Shortcut);
                e.Handled = true;
                if (status.WasKeyDown && !historyShortcut && !zoomShortcut) return;
                if (findShortcut)
                {
                    ShowFind();
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
            // The keypad separator uses the layout's text. Ghostty does not assign it a legacy sequence.
            var special = virtualKey is < 0x20 or (>= 0x21 and <= 0x2F) or (>= 0x60 and <= 0x87 and not 0x6C)
                or 0x5D or 0x90 or 0x91 || IsSidedModifier(key);
            var direct = special || (mods & TerminalModifiers.Super) != 0 || (!altGr && (mods & ctrlAlt) != 0);
            var action = _keyboard.KeyDown(physical, virtualKey, mods, status.WasKeyDown,
                direct ? TerminalKeyboardInput.Route.Direct : TerminalKeyboardInput.Route.Text,
                !direct && altGr ? ctrlAlt : TerminalModifiers.None);
            if (direct)
            {
                e.Handled = true;
                session.SendKey(virtualKey, mods, action);
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
            if (_session == session && _state == SessionState.Running) session.Paste(text);
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
            if (_keyboard.KeyUp(physical, Modifiers()) is { } key && _state == SessionState.Running)
                session.SendKey(key.Key, key.Modifiers, key.Action);
        }
        catch (Exception exception) { await FailSessionAsync(exception); }
    }

    private async void Terminal_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (_state != SessionState.Running || _session is not { } session) return;
        try
        {
            e.Handled = true;
            var status = e.KeyStatus;
            var physical = new TerminalKeyboardInput.PhysicalKey(status.ScanCode, status.IsExtendedKey);
            if (_keyboard.Character(physical, e.Character) is { } key)
                session.SendKey(key.Key, key.Modifiers, key.Action, key.Text);
        }
        catch (Exception exception) { await FailSessionAsync(exception); }
    }

    private void Terminal_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_state == SessionState.Running) _session?.SetFocus(true);
        Terminal.RefreshCursor();
    }

    private async void Terminal_LostFocus(object sender, RoutedEventArgs e)
    {
        var releases = _keyboard.Reset();
        Terminal.RefreshCursor();
        if (_state != SessionState.Running || _session is not { } session) return;
        try
        {
            ReleaseApplicationMouse();
            // End held keys before the focus report so Kitty applications can clear their key state.
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
        _refreshTimer.Stop();
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
        Terminal.IsEnabled = state == SessionState.Running || (state == SessionState.Idle && _session is not null);
        SessionHeader.Visibility = state == SessionState.Idle ? Visibility.Visible : Visibility.Collapsed;
        SessionFooter.Visibility = state == SessionState.Running ? Visibility.Collapsed : Visibility.Visible;
        StopButton.IsEnabled = state is SessionState.Starting or SessionState.Running;
        NewSessionButton.IsEnabled = state == SessionState.Idle;
    }

    private void CancelSelectionGesture()
    {
        ReleaseApplicationMouse();
        _selectionTimer.Stop();
        if (_selecting)
        {
            _selecting = false;
            _session?.EndSelection();
            Terminal.ReleasePointerCaptures();
        }
        _wheelDelta = 0;
        _zoomWheelDelta = 0;
        _applicationWheelDelta = 0;
        _applicationHorizontalWheelDelta = 0;
        _lastClickTime = 0;
        _lastClickColumn = _lastClickRow = -1;
        _clickCount = 0;
    }

    private Task ReleaseSessionAsync()
    {
        _refreshTimer.Stop();
        CancelSelectionGesture();
        if (_session is not { } session)
            return _release;

        _session = null;
        return _release = ReleaseCoreAsync(session, _startup);
    }

    private async Task ReleaseCoreAsync(TerminalSession session, Task startup)
    {
        try { await startup; }
        catch { /* Startup failures are shown by StartSessionAsync. Ownership still needs release. */ }

        try { await session.DisposeAsync(); }
        catch (Exception exception)
        {
            if (_state != SessionState.Disposed)
                ShowFailure(exception);
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        // Splits reparent visible pages. Tab visibility and session teardown own refresh lifetime.
        CancelSelectionGesture();
    }

    public ValueTask DisposeAsync() => new(_dispose ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        SetState(SessionState.Disposed);
        _refreshTimer.Stop();
        _selectionTimer.Stop();
        await ReleaseSessionAsync();
        TerminalPreferences.Current.Changed -= PreferencesChanged;
        ActualThemeChanged -= Page_ActualThemeChanged;
        Terminal.ReleaseResources();
    }
}
