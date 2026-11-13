using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AgentTerm.Terminal;
using AgentTerm_App.Effects;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace AgentTerm_App;

// What programs ask a pane to do: clipboard, bell, desktop notifications, progress, and window resize.
// The terminal queues requests; this file presents them. Nothing here runs while a pane is idle: the only timers are
// one-shot and exist while a card or a bell flash is on screen.
public sealed partial class MainPage
{
    private static readonly WindowsEffectHost EffectHost = new();
    private static readonly ProgressAggregator EffectProgress = new(EffectHost);

    private PaneEffects? _effects;
    private readonly EffectInbox _effectInbox = new();
    private int _effectDispatchQueued;
    private EffectNoticeView? _noticeView;
    private ProgressBar? _progressBar;
    private Border? _bellFlash;
    private DispatcherQueueTimer? _noticeTimer;
    private DispatcherQueueTimer? _bellTimer;
    private nint _window;

    /// <summary>Raised when the user asks to see this pane, for example by clicking its Windows notification.</summary>
    internal event Action? RevealRequested;

    /// <summary>A short tab marker: this pane's progress, or a dot when something asked for attention while unseen. Empty otherwise.</summary>
    internal string EffectBadge => _effects?.Badge ?? "";

    private void InitializeEffects()
    {
        _effects = new PaneEffects(EffectHost, new EffectView(this), EffectProgress, () => TerminalPreferences.Current.Effects, ResolveClipboardWrite);
        _noticeView = new EffectNoticeView(_effects.Notices, (id, action) => _effects?.Invoke(id, action));
        _progressBar = new ProgressBar
        {
            VerticalAlignment = VerticalAlignment.Top, Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        AutomationProperties.SetName(_progressBar, "Program progress");
        _bellFlash = new Border
        {
            Opacity = 0, IsHitTestVisible = false, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4),
            BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(24, 255, 255, 255)),
        };
        AutomationProperties.SetAccessibilityView(_bellFlash, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        if (Terminal.Parent is Grid host)
        {
            var layer = new Grid { IsHitTestVisible = true };
            Grid.SetRow(layer, Grid.GetRow(Terminal));
            layer.Children.Add(_bellFlash);
            layer.Children.Add(_progressBar);
            layer.Children.Add(_noticeView);
            host.Children.Add(layer);
        }
        _noticeTimer = _dispatcher.CreateTimer();
        _noticeTimer.IsRepeating = false;
        _noticeTimer.Tick += (_, _) => ExpireNotices();
        _bellTimer = _dispatcher.CreateTimer();
        _bellTimer.IsRepeating = false;
        _bellTimer.Tick += (_, _) => { if (_bellFlash is not null) _bellFlash.Opacity = 0; };
        _effects.Notices.Changed += ScheduleNoticeExpiry;
        Terminal.GotFocus += (_, _) => EffectsViewed();
        Terminal.PointerEntered += (_, _) => EffectsViewed();
        Loaded += (_, _) => RememberWindow();
    }

    private void AttachEffects(TerminalSession session)
    {
        session.ConfigureEffects(TerminalPreferences.Current.Effects, WindowsTerminalClipboard.Shared);
        session.EffectsAvailable += Session_EffectsAvailable;
    }

    private void DetachEffects(TerminalSession session)
    {
        session.EffectsAvailable -= Session_EffectsAvailable;
        _effectInbox.Clear();
        _effects?.Reset();
    }

    /// <summary>The shell exited. A program that is gone cannot clear its own progress or answer a held clipboard question.</summary>
    private void EffectsSessionEnded()
    {
        _effectInbox.Clear();
        _session?.Effects.Reset();
        _effects?.Reset();
    }

    private void ApplyEffectOptions()
    {
        var options = TerminalPreferences.Current.Effects;
        _session?.ConfigureEffects(options);
        // Turning progress off removes what is showing. PaneEffects reads the other options on each request.
        if (!options.Progress) _effects?.Apply([new ProgressEffect(TerminalProgressState.Remove, 0)]);
    }

    private void DisposeEffects()
    {
        _noticeTimer?.Stop();
        _bellTimer?.Stop();
        _noticeView?.Detach();
        _effects?.Dispose();
    }

    private void EffectsViewed()
    {
        if (_effects is null || !_viewActive || !IsLoaded) return;
        if (EffectHost.IsActive(ResolveWindow())) _effects.Seen();
    }

    // Called on the terminal reader thread, outside every session lock.
    private void Session_EffectsAvailable(IReadOnlyList<TerminalEffect> batch)
    {
        _effectInbox.Add(batch);
        if (Interlocked.Exchange(ref _effectDispatchQueued, 1) != 0) return;
        if (!_dispatcher.TryEnqueue(DrainEffects)) Volatile.Write(ref _effectDispatchQueued, 0);
    }

    private void DrainEffects()
    {
        Volatile.Write(ref _effectDispatchQueued, 0);
        if (_state == SessionState.Disposed || _effects is null) return;
        var batch = _effectInbox.Take();
        if (batch.Count != 0) _effects.Apply(batch);
    }

    private ClipboardOutcome ResolveClipboardWrite(long requestId, bool allow) =>
        _session?.ResolveClipboardWrite(requestId, allow) ?? ClipboardOutcome.Unsupported;

    private void ScheduleNoticeExpiry()
    {
        if (_noticeTimer is null || _effects is null) return;
        _noticeTimer.Stop();
        if (_effects.Notices.NextExpiry is not { } next) return;
        _noticeTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(next - Environment.TickCount64, 50, 60_000));
        _noticeTimer.Start();
    }

    private void ExpireNotices() => _effects?.Notices.Expire(Environment.TickCount64);

    private nint ResolveWindow()
    {
        if (XamlRoot is { } root) _window = Win32Interop.GetWindowFromWindowId(root.ContentIslandEnvironment.AppWindowId);
        else if (_window == 0) _window = Process.GetCurrentProcess().MainWindowHandle;
        return _window;
    }

    private void RememberWindow() => ResolveWindow();

    private void FlashBell()
    {
        if (_bellFlash is null || _bellTimer is null) return;
        _bellTimer.Stop();
        if (_animationsEnabled && IsLoaded)
        {
            var fade = new DoubleAnimation { From = 1, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(350)) };
            Storyboard.SetTarget(fade, _bellFlash);
            Storyboard.SetTargetProperty(fade, "Opacity");
            var storyboard = new Storyboard();
            storyboard.Children.Add(fade);
            storyboard.Begin();
            return;
        }
        // Reduced motion: a brief static highlight instead of a fade.
        _bellFlash.Opacity = 1;
        _bellTimer.Interval = TimeSpan.FromMilliseconds(180);
        _bellTimer.Start();
    }

    private void ShowEffectProgress(TerminalProgressState state, int percent)
    {
        if (_progressBar is null) return;
        if (state == TerminalProgressState.Remove)
        {
            _progressBar.Visibility = Visibility.Collapsed;
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = 0;
            return;
        }
        _progressBar.Visibility = Visibility.Visible;
        _progressBar.IsIndeterminate = state == TerminalProgressState.Indeterminate;
        _progressBar.ShowError = state == TerminalProgressState.Error;
        _progressBar.ShowPaused = state == TerminalProgressState.Pause;
        if (state != TerminalProgressState.Indeterminate) _progressBar.Value = percent;
        AutomationProperties.SetName(_progressBar, state switch
        {
            TerminalProgressState.Error => $"Program progress failed at {percent} percent",
            TerminalProgressState.Pause => $"Program progress paused at {percent} percent",
            TerminalProgressState.Indeterminate => "Program is working",
            _ => $"Program progress {percent} percent",
        });
    }

    /// <summary>
    /// Honors a program's request to resize the window's text area, only while the user allows it. The window changes by
    /// the difference in cells, so a split pane approximates its share. A maximized, full screen, or hidden window is left alone.
    /// </summary>
    private void ResizeWindowFor(int rows, int columns)
    {
        if (XamlRoot is not { } root || !IsLoaded || !_viewActive || Terminal.CellWidth <= 0 || Terminal.CellHeight <= 0) return;
        var appWindow = AppWindow.GetFromWindowId(root.ContentIslandEnvironment.AppWindowId);
        if (appWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored }) return;
        var scale = root.RasterizationScale;
        var columnDelta = columns == 0 ? 0 : Math.Clamp(columns, 20, 500) - _columns;
        var rowDelta = rows == 0 ? 0 : Math.Clamp(rows, 5, 200) - _rows;
        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Clamp(appWindow.Size.Width + (int)Math.Round(columnDelta * Terminal.CellWidth * scale), 320, area.Width);
        var height = Math.Clamp(appWindow.Size.Height + (int)Math.Round(rowDelta * Terminal.CellHeight * scale), 240, area.Height);
        if (width != appWindow.Size.Width || height != appWindow.Size.Height) appWindow.Resize(new SizeInt32(width, height));
    }

    private sealed class EffectView(MainPage page) : IPaneEffectView
    {
        public nint Window => page.ResolveWindow();
        public string Name => global::AgentTerm_App.ProgramTitle.Clean(page.ProgramTitle) ?? page.Profile.Name;
        public bool IsVisible => page._viewActive && page.IsLoaded;
        public void ShowBell() => page.FlashBell();
        public void ShowProgress(TerminalProgressState state, int percent) => page.ShowEffectProgress(state, percent);
        public void RequestWindowResize(int rows, int columns) => page.ResizeWindowFor(rows, columns);

        public void Reveal()
        {
            page.RevealRequested?.Invoke();
            WindowsEffectHost.Reveal(Window);
        }
    }
}
