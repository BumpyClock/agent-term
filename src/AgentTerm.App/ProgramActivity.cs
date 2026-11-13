using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AgentTerm_App;

internal enum ProgramActivityKind { TabIcon, PaneEdge }

internal sealed class ProgramActivity : Grid
{
    private readonly ProgramActivityKind _kind;
    private readonly UISettings _settings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private readonly Func<bool> _motionAllowed;
    private readonly Grid _dots = new() { Width = 19, Height = 19, Visibility = Visibility.Collapsed };
    private readonly Rectangle _beam = new() { Height = 1, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    private readonly TranslateTransform _travel = new();
    private readonly SolidColorBrush _ink = new();
    private readonly List<(UIElement Element, long Token)> _visibility = [];
    private XamlRoot? _root;
    private Storyboard? _animation;
    private bool _working;
    private bool _active = true;
    private bool _inViewport = true;
    private bool _loaded;
    internal event Action? Changed;
    internal bool IsAnimating => _animation is not null;

    internal ProgramActivity(ProgramActivityKind kind, Func<bool>? motionAllowed = null)
    {
        _kind = kind;
        _motionAllowed = motionAllowed ?? (() => _settings.AnimationsEnabled && !_accessibility.HighContrast);
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        if (kind == ProgramActivityKind.TabIcon)
        {
            Width = Height = 20;
            // A light band sweeps corner to corner; each dot's Tag is its diagonal, 0 to 8.
            for (var index = 0; index < 25; index++)
            {
                var dot = new Ellipse { Width = 3, Height = 3, Fill = _ink, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(index % 5 * 4, index / 5 * 4, 0, 0), Tag = index % 5 + index / 5 };
                _dots.Children.Add(dot);
            }
            Children.Add(_dots);
        }
        else
        {
            Height = 1;
            VerticalAlignment = VerticalAlignment.Top;
            _beam.RenderTransform = _travel;
            Children.Add(_beam);
        }
        Loaded += (_, _) =>
        {
            _loaded = true;
            _inViewport = true;
            _settings.AnimationsEnabledChanged += SettingsChanged;
            _settings.ColorValuesChanged += SettingsChanged;
            for (DependencyObject? node = this; node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is UIElement element)
                    _visibility.Add((element, element.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Refresh())));
            _root = XamlRoot;
            if (_root is not null) _root.Changed += RootChanged;
            RefreshPreferences();
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            _settings.AnimationsEnabledChanged -= SettingsChanged;
            _settings.ColorValuesChanged -= SettingsChanged;
            foreach (var entry in _visibility) entry.Element.UnregisterPropertyChangedCallback(VisibilityProperty, entry.Token);
            _visibility.Clear();
            if (_root is not null) _root.Changed -= RootChanged;
            _root = null;
            Refresh();
        };
        ActualThemeChanged += (_, _) => RefreshPreferences();
        SizeChanged += (_, _) =>
        {
            if (_kind == ProgramActivityKind.PaneEdge)
            {
                Clip = new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, 1) };
                _beam.Width = ActualWidth * .34;
                Refresh(restart: true);
            }
        };
        EffectiveViewportChanged += (_, e) =>
        {
            // A scrolled-out element keeps a nonempty viewport that lies outside its own bounds.
            var viewport = e.EffectiveViewport;
            var visible = viewport.Width > 0 && viewport.Height > 0 && viewport.X + viewport.Width > 0 && viewport.Y + viewport.Height > 0 &&
                (ActualWidth == 0 || viewport.X < ActualWidth) && (ActualHeight == 0 || viewport.Y < ActualHeight);
            if (_inViewport == visible) return;
            _inViewport = visible;
            Refresh();
        };
    }

    internal bool Working
    {
        get => _working;
        set { if (_working == value) return; _working = value; Refresh(); }
    }

    internal bool Active
    {
        get => _active;
        set { if (_active == value) return; _active = value; Refresh(); }
    }

    private void SettingsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(RefreshPreferences);
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Refresh();

    internal void RefreshPreferences()
    {
        _ink.Color = ActualTheme == ElementTheme.Light ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 229, 231, 235);
        var accent = _accessibility.HighContrast ? _settings.GetColorValue(UIColorType.Foreground)
            : _settings.GetColorValue(ActualTheme == ElementTheme.Light ? UIColorType.AccentDark1 : UIColorType.AccentLight2);
        _beam.Fill = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
            GradientStops = { new GradientStop { Color = Color.FromArgb(0, accent.R, accent.G, accent.B), Offset = 0 },
                new GradientStop { Color = accent, Offset = .5 },
                new GradientStop { Color = Color.FromArgb(0, accent.R, accent.G, accent.B), Offset = 1 } }
        };
        Refresh();
    }

    private void Refresh(bool restart = false)
    {
        var visible = _working && _active && _loaded && _inViewport && _root?.IsHostVisible != false &&
            _visibility.All(entry => entry.Element.Visibility == Visibility.Visible);
        var animate = visible && _motionAllowed();
        _dots.Visibility = animate && _kind == ProgramActivityKind.TabIcon ? Visibility.Visible : Visibility.Collapsed;
        _beam.Visibility = visible && _kind == ProgramActivityKind.PaneEdge ? Visibility.Visible : Visibility.Collapsed;
        if (!restart && animate == IsAnimating) return;
        _animation?.Stop();
        _animation = null;
        if (_kind == ProgramActivityKind.PaneEdge) _travel.X = (ActualWidth - _beam.Width) / 2;
        if (animate)
        {
            _animation = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            if (_kind == ProgramActivityKind.TabIcon)
            {
                foreach (var dot in _dots.Children.Cast<Ellipse>())
                {
                    var shift = (int)dot.Tag * SweepStep;
                    var animation = new DoubleAnimationUsingKeyFrames { Duration = new Duration(TimeSpan.FromSeconds(SweepCycle)) };
                    var initial = OpacityAt((1 - shift) % 1);
                    animation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = initial });
                    foreach (var phase in SweepPhases)
                    {
                        var time = (phase + shift) % 1;
                        if (time == 0) continue;
                        animation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(time * SweepCycle)), Value = OpacityAt(phase) });
                    }
                    animation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(SweepCycle)), Value = initial });
                    Storyboard.SetTarget(animation, dot);
                    Storyboard.SetTargetProperty(animation, nameof(Opacity));
                    _animation.Children.Add(animation);
                }
            }
            else
            {
                var animation = new DoubleAnimation { From = -_beam.Width, To = ActualWidth, Duration = new Duration(TimeSpan.FromSeconds(2.4)) };
                Storyboard.SetTarget(animation, _travel);
                Storyboard.SetTargetProperty(animation, nameof(TranslateTransform.X));
                _animation.Children.Add(animation);
            }
            _animation.Begin();
        }
        Changed?.Invoke();
    }

    // Seconds per sweep. Diagonals start SweepStep of a cycle apart, so the band crosses in three quarters of a
    // cycle and the grid rests at its floor for the last quarter.
    private const double SweepCycle = 1.2;
    private const double SweepStep = 1 / 12d;
    private static readonly double[] SweepPhases = [0, .12, .3, .5];
    private static readonly double[] SweepOpacity = [.18, 1, .45, .18];

    private static double OpacityAt(double phase)
    {
        for (var index = 1; index < SweepPhases.Length; index++)
            if (phase <= SweepPhases[index])
                return SweepOpacity[index - 1] + (SweepOpacity[index] - SweepOpacity[index - 1]) * (phase - SweepPhases[index - 1]) /
                    (SweepPhases[index] - SweepPhases[index - 1]);
        return SweepOpacity[^1];
    }
}
