using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
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
    private static readonly DependencyProperty OverlaySuppressedProperty = DependencyProperty.RegisterAttached(
        "OverlaySuppressed", typeof(bool), typeof(ProgramActivity), new PropertyMetadata(false));
    private ProgramActivityOverlay? _native;
    private Color _accent;
    private Rect? _effectiveViewport;
    private readonly TrailingThrottle _geometry;
    private bool _overlayUncovered;
    private double _rasterizationScale;
    private long? _animationOrigin;
    private readonly Dictionary<VisualStateGroup, bool> _movingGroups = [];
    private readonly List<(VisualStateGroup Group, VisualStateChangedEventHandler Changing, VisualStateChangedEventHandler Changed)> _motionHandlers = [];
    private readonly List<(UIElement Element, long Token)> _visibility = [];
    private readonly List<(UIElement Element, DependencyProperty Property, long Token)> _overlayProperties = [];
    private XamlRoot? _root;
    private Storyboard? _animation;
    private bool _working;
    private bool _active = true;
    private bool _inViewport = true;
    internal event Action? Changed;
    internal bool IsAnimating => _animation is not null || UsesSystemCompositor;
    internal bool UsesSystemCompositor => _native?.IsReady == true;

    internal static void SetOverlaySuppressed(UIElement element, bool suppressed)
    {
        element.SetValue(OverlaySuppressedProperty, suppressed);
    }

    internal ProgramActivity(ProgramActivityKind kind, Func<bool>? motionAllowed = null)
    {
        _kind = kind;
        _motionAllowed = motionAllowed ?? (() => _settings.AnimationsEnabled && !_accessibility.HighContrast);
        IsHitTestVisible = false;
        var geometryTimer = DispatcherQueue.CreateTimer();
        geometryTimer.IsRepeating = false;
        geometryTimer.Interval = TimeSpan.FromMilliseconds(33);
        _geometry = new(geometryTimer.Start, geometryTimer.Stop, UpdateNativeGeometry);
        geometryTimer.Tick += (_, _) => _geometry.Flush();
        AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        if (kind == ProgramActivityKind.TabIcon)
        {
            Width = Height = 20;
            // A light band sweeps corner to corner; each dot's Tag is its diagonal, 0 to 8.
            for (var index = 0; index < 25; index++)
            {
                var dot = new Ellipse { Width = 3, Height = 3, Fill = _ink, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(index % 5 * 4, index / 5 * 4, 0, 0), Tag = index % 5 + index / 5 };
                dot.SizeChanged += (_, _) => QueueNativeGeometry();
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
            if (!ReferenceEquals(_root, XamlRoot)) StopNative();
            Disconnect();
            _inViewport = true;
            _settings.AnimationsEnabledChanged += SettingsChanged;
            _settings.ColorValuesChanged += SettingsChanged;
            for (DependencyObject? node = this; node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is UIElement element)
                {
                    _visibility.Add((element, element.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Refresh())));
                    foreach (var property in new[] { OpacityProperty, ClipProperty, OverlaySuppressedProperty })
                        _overlayProperties.Add((element, property, element.RegisterPropertyChangedCallback(property, (_, _) =>
                        {
                            Refresh();
                            QueueNativeGeometry();
                        })));
                    if (element is FrameworkElement frameworkElement)
                        foreach (var group in VisualStateManager.GetVisualStateGroups(frameworkElement))
                            if (group.Name is "ReorderHintStates" or "DragStates")
                            {
                                _movingGroups.Add(group, !IsSteady(group, group.CurrentState));
                                // WinUI raises these with the owning template element as sender.
                                VisualStateChangedEventHandler changing = (_, _) => MotionStateChanging(group);
                                VisualStateChangedEventHandler changed = (_, args) => MotionStateChanged(group, args.NewState);
                                _motionHandlers.Add((group, changing, changed));
                                group.CurrentStateChanging += changing;
                                group.CurrentStateChanged += changed;
                            }
                }
            _root = XamlRoot;
            _rasterizationScale = _root?.RasterizationScale ?? 1;
            if (_root is not null) _root.Changed += RootChanged;
            RefreshPreferences();
            QueueNativeGeometry();
        };
        Unloaded += (_, _) =>
        {
            // Reparenting can deliver an old Unloaded after the element has loaded into its new pane.
            if (IsLoaded) return;
            Disconnect();
            Refresh();
        };
        ActualThemeChanged += (_, _) => RefreshPreferences();
        // Native clocks do not drive XAML layout. This only follows actual layout changes,
        // including scrolling/reparenting that changes position without changing our size.
        LayoutUpdated += (_, _) => QueueNativeGeometry();
        SizeChanged += (_, _) =>
        {
            if (_kind == ProgramActivityKind.PaneEdge)
            {
                Clip = new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, 1) };
                _beam.Width = ActualWidth * .34;
                Refresh(restart: true);
            }
            QueueNativeGeometry();
        };
        EffectiveViewportChanged += (_, e) =>
        {
            // A scrolled-out element keeps a nonempty viewport that lies outside its own bounds.
            var viewport = e.EffectiveViewport;
            _effectiveViewport = viewport;
            QueueNativeGeometry();
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
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_rasterizationScale != sender.RasterizationScale)
        {
            _rasterizationScale = sender.RasterizationScale;
            QueueNativeGeometry();
        }
        Refresh();
    }

    internal void RefreshPreferences()
    {
        _ink.Color = ActualTheme == ElementTheme.Light ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 229, 231, 235);
        var accent = _accent = _accessibility.HighContrast ? _settings.GetColorValue(UIColorType.Foreground)
            : _settings.GetColorValue(ActualTheme == ElementTheme.Light ? UIColorType.AccentDark1 : UIColorType.AccentLight2);
        _beam.Fill = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
            GradientStops = { new GradientStop { Color = Color.FromArgb(0, accent.R, accent.G, accent.B), Offset = 0 },
                new GradientStop { Color = accent, Offset = .5 },
                new GradientStop { Color = Color.FromArgb(0, accent.R, accent.G, accent.B), Offset = 1 } }
        };
        _native?.SetColors(_ink.Color, accent);
        Refresh();
    }

    private void Disconnect()
    {
        _geometry.Cancel();
        _settings.AnimationsEnabledChanged -= SettingsChanged;
        _settings.ColorValuesChanged -= SettingsChanged;
        foreach (var entry in _visibility) entry.Element.UnregisterPropertyChangedCallback(VisibilityProperty, entry.Token);
        _visibility.Clear();
        foreach (var entry in _overlayProperties) entry.Element.UnregisterPropertyChangedCallback(entry.Property, entry.Token);
        _overlayProperties.Clear();
        foreach (var entry in _motionHandlers)
        {
            entry.Group.CurrentStateChanging -= entry.Changing;
            entry.Group.CurrentStateChanged -= entry.Changed;
        }
        _motionHandlers.Clear();
        _movingGroups.Clear();
        if (_root is not null) _root.Changed -= RootChanged;
        _root = null;
        _overlayUncovered = false;
    }

    private void Refresh(bool restart = false)
    {
        var wasAnimating = IsAnimating;
        var visible = _working && _active && IsLoaded && _inViewport && _root?.IsHostVisible != false &&
            _visibility.All(entry => entry.Element.Visibility == Visibility.Visible);
        var animate = visible && _motionAllowed();
        if (!animate) _animationOrigin = null;
        else _animationOrigin ??= Stopwatch.GetTimestamp();
        _dots.Visibility = animate && _kind == ProgramActivityKind.TabIcon ? Visibility.Visible : Visibility.Collapsed;
        _beam.Visibility = visible && _kind == ProgramActivityKind.PaneEdge ? Visibility.Visible : Visibility.Collapsed;
        var useOverlay = animate && CanUseOverlay();
        if (useOverlay && _native is null)
        {
            _native = new ProgramActivityOverlay(_kind, _root!);
            _native.SetColors(_ink.Color, _accent);
            QueueNativeGeometry();
        }
        else if (!useOverlay) StopNative();
        _dots.Opacity = _beam.Opacity = UsesSystemCompositor ? 0 : 1;
        var fallback = animate && !UsesSystemCompositor;
        if (!restart && fallback == (_animation is not null))
        {
            if (wasAnimating != IsAnimating) Changed?.Invoke();
            return;
        }
        _animation?.Stop();
        _animation = null;
        if (_kind == ProgramActivityKind.PaneEdge) _travel.X = (ActualWidth - _beam.Width) / 2;
        if (fallback)
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
                var animation = new DoubleAnimation { From = -_beam.Width, To = ActualWidth, Duration = new Duration(TimeSpan.FromSeconds(BeamCycle)) };
                Storyboard.SetTarget(animation, _travel);
                Storyboard.SetTargetProperty(animation, nameof(TranslateTransform.X));
                _animation.Children.Add(animation);
            }
            _animation.Begin();
            _animation.Seek(TimeSpan.FromSeconds(AnimationPhaseSeconds));
        }
        Changed?.Invoke();
    }

    private bool CanUseOverlay()
    {
        if (!_overlayUncovered || _movingGroups.Values.Any(moving => moving) ||
            _root is null || VisualTreeHelper.GetOpenPopupsForXamlRoot(_root).Count != 0) return false;
        // Flyout cards may share a XamlRoot but have another native host. Keep their XAML
        // renderer rather than placing their indicators over the main window.
        for (DependencyObject? node = this; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement element && (element.Opacity < 1 || (bool)element.GetValue(OverlaySuppressedProperty))) return false;
            if (ReferenceEquals(node, _root.Content)) return true;
        }
        return false;
    }

    private double AnimationPhaseSeconds => _animationOrigin is { } origin
        ? Stopwatch.GetElapsedTime(origin).TotalSeconds % (_kind == ProgramActivityKind.TabIcon ? SweepCycle : BeamCycle)
        : 0;

    private static bool IsSteady(VisualStateGroup group, VisualState? state) => state?.Name ==
        (group.Name == "DragStates" ? "NotDragging" : "NoReorderHint");

    private void MotionStateChanging(VisualStateGroup group)
    {
        _movingGroups[group] = true;
        Refresh();
    }

    private void MotionStateChanged(VisualStateGroup group, VisualState? state)
    {
        _movingGroups[group] = !IsSteady(group, state);
        QueueNativeGeometry();
    }

    private void StopNative()
    {
        _native?.Dispose();
        _native = null;
    }

    private void QueueNativeGeometry()
    {
        if (_working && IsLoaded) _geometry.Request();
    }

    private void UpdateNativeGeometry()
    {
        _overlayUncovered = IsOverlayUncovered();
        Refresh();
        if (_native is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var scale = XamlRoot.RasterizationScale;
        var transform = TransformToVisual(null);
        var bounds = transform.TransformBounds(new Rect(0, 0, ActualWidth, ActualHeight));
        var clipped = Intersect(bounds, new Rect(0, 0, XamlRoot.Size.Width, XamlRoot.Size.Height));
        if (_effectiveViewport is { } viewport)
            clipped = Intersect(clipped, transform.TransformBounds(Intersect(new Rect(0, 0, ActualWidth, ActualHeight), viewport)));
        for (DependencyObject? node = this; node is UIElement element; node = VisualTreeHelper.GetParent(node))
            if (element.Clip is { } clip)
                clipped = Intersect(clipped, element.TransformToVisual(null).TransformBounds(clip.Rect));
        var position = new Vector2((float)(bounds.X * scale), (float)(bounds.Y * scale));
        var size = new Vector2((float)(bounds.Width * scale), (float)(bounds.Height * scale));
        var localClip = clipped.Width <= 0 || clipped.Height <= 0 ? new Rect() : LocalPixels(clipped);
        var scaleX = bounds.Width / ActualWidth * scale;
        var scaleY = bounds.Height / ActualHeight * scale;
        Rect[] elements;
        if (_kind == ProgramActivityKind.TabIcon)
        {
            if (_dots.Children.Cast<Ellipse>().Any(dot => dot.ActualWidth <= 0 || dot.ActualHeight <= 0)) return;
            var origin = _dots.ActualOffset;
            elements = _dots.Children.Cast<Ellipse>().Select(dot =>
                Local(origin.X + dot.ActualOffset.X, origin.Y + dot.ActualOffset.Y, dot.ActualWidth, dot.ActualHeight)).ToArray();
        }
        else
        {
            if (_beam.ActualWidth <= 0 || _beam.ActualHeight <= 0) return;
            elements = [Local(_beam.ActualOffset.X, _beam.ActualOffset.Y, _beam.ActualWidth, _beam.ActualHeight)];
        }
        _native.SetGeometry(new ActivityOverlayGeometry(position, size, localClip, elements,
            _kind == ProgramActivityKind.PaneEdge ? (float)(-_beam.Width * scaleX) : 0, size.X), AnimationPhaseSeconds);
        Refresh();

        Rect LocalPixels(Rect rect) => new((rect.X - bounds.X) * scale, (rect.Y - bounds.Y) * scale, rect.Width * scale, rect.Height * scale);
        Rect Local(double x, double y, double width, double height) => new(x * scaleX, y * scaleY, width * scaleX, height * scaleY);
    }

    private bool IsOverlayUncovered()
    {
        if (_root is null || !IsLoaded || ActualWidth <= 0 || ActualHeight <= 0) return false;
        var bounds = TransformToVisual(null).TransformBounds(new Rect(0, 0, ActualWidth, ActualHeight));
        // The documented result is ordered front to back. Include non-hit-testable
        // elements because activity indicators intentionally do not receive input.
        var hits = VisualTreeHelper.FindElementsInHostCoordinates(bounds, _root.Content, true);
        var ancestors = new List<DependencyObject>();
        for (DependencyObject? node = this; node is not null; node = VisualTreeHelper.GetParent(node)) ancestors.Add(node);
        var foundAncestor = false;
        foreach (var hit in hits)
        {
            if (ancestors.Contains(hit)) { foundAncestor = true; continue; }
            var descendant = false;
            for (DependencyObject? node = hit; node is not null; node = VisualTreeHelper.GetParent(node))
                if (ReferenceEquals(node, this)) { descendant = true; break; }
            if (descendant) continue;
            // Transparent layout panels paint no pixels of their own. Their
            // overlapping children have separate entries in the same hit stack.
            if (hit is Panel { Shadow: null } panel &&
                (panel.Background is null || panel.Background is SolidColorBrush { Color.A: 0 })) continue;
            // A backgroundless indicator can be omitted from the hit result. Compare
            // the diverging branches under their common panel, rather than treating
            // all hits before its first reported ancestor as foreground content.
            var below = false;
            for (DependencyObject? branch = hit; branch is not null; branch = VisualTreeHelper.GetParent(branch))
            {
                var parent = VisualTreeHelper.GetParent(branch);
                var ancestorIndex = parent is null ? -1 : ancestors.IndexOf(parent);
                if (ancestorIndex < 1) continue;
                if (parent is Panel common && branch is UIElement other && ancestors[ancestorIndex - 1] is UIElement own)
                {
                    var otherIndex = common.Children.IndexOf(other);
                    var ownIndex = common.Children.IndexOf(own);
                    var otherZ = common is Canvas ? Canvas.GetZIndex(other) : 0;
                    var ownZ = common is Canvas ? Canvas.GetZIndex(own) : 0;
                    below = otherIndex >= 0 && ownIndex >= 0 && (otherZ < ownZ || otherZ == ownZ && otherIndex < ownIndex);
                }
                break;
            }
            if (below) continue;
            return false;
        }
        return foundAncestor;

    }

    private static Rect Intersect(Rect left, Rect right)
    {
        var x = Math.Max(left.X, right.X);
        var y = Math.Max(left.Y, right.Y);
        return new Rect(x, y, Math.Max(0, Math.Min(left.Right, right.Right) - x), Math.Max(0, Math.Min(left.Bottom, right.Bottom) - y));
    }

    // Seconds per sweep. Diagonals start SweepStep of a cycle apart, so the band crosses in three quarters of a
    // cycle and the grid rests at its floor for the last quarter.
    internal const double SweepCycle = 1.2;
    internal const double BeamCycle = 2.4;
    internal const double SweepStep = 1 / 12d;
    internal static readonly double[] SweepPhases = [0, .12, .3, .5];
    private static readonly double[] SweepOpacity = [.18, 1, .45, .18];

    internal static double OpacityAt(double phase)
    {
        for (var index = 1; index < SweepPhases.Length; index++)
            if (phase <= SweepPhases[index])
                return SweepOpacity[index - 1] + (SweepOpacity[index] - SweepOpacity[index - 1]) * (phase - SweepPhases[index - 1]) /
                    (SweepPhases[index] - SweepPhases[index - 1]);
        return SweepOpacity[^1];
    }
}
