using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace AgentTerm_App;

/// <summary>A moving highlight on text only, animated by the compositor. No idle timer or whole-row opacity pulse.</summary>
internal sealed class ActivityLabel : Grid
{
    private readonly TextBlock _text = new() { Style = AgentUi.Style("AgentLabelText") };
    private readonly UISettings _settings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private CompositionBrush? _mask;
    private SpriteVisual? _shimmer;
    private string _shown = "";
    private bool _active;
    private bool _loaded;
    public ActivityLabel()
    {
        Children.Add(_text);
        _text.SizeChanged += (_, e) => { if (_shimmer is not null) _shimmer.Size = e.NewSize.ToVector2(); };
        // HighContrastChanged subscription throws 0x80070490 in the unpackaged WinUI host.
        // ColorValuesChanged also signals contrast changes without that unsupported subscription.
        Loaded += (_, _) => { _loaded = true; _settings.AnimationsEnabledChanged += AnimationsChanged; _settings.ColorValuesChanged += ColorsChanged; Refresh(); };
        ActualThemeChanged += (_, _) => { Stop(); Refresh(); };
        Unloaded += (_, _) => { _loaded = false; _settings.AnimationsEnabledChanged -= AnimationsChanged; _settings.ColorValuesChanged -= ColorsChanged; Stop(); };
    }
    public void Update(string text, bool active)
    {
        if (_shown != text) _text.Text = _shown = text;
        if (_active == active) return;
        _active = active; Refresh();
    }
    private void AnimationsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(Refresh);
    private void ColorsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(Refresh);
    private void Stop()
    {
        if (_shimmer is null) return;
        ElementCompositionPreview.SetElementChildVisual(_text, null);
        var brush = (CompositionMaskBrush)_shimmer.Brush;
        brush.Source.Dispose();
        brush.Dispose();
        _shimmer.Dispose();
        _shimmer = null;
    }
    private void Refresh()
    {
        if (!_active || !_loaded || !_settings.AnimationsEnabled || _accessibility.HighContrast) { Stop(); return; }
        if (_shimmer is not null) return;
        var compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
        var clear = Windows.UI.Color.FromArgb(0, 255, 255, 255);
        var gradient = compositor.CreateLinearGradientBrush();
        gradient.InterpolationSpace = CompositionColorSpace.Rgb;
        gradient.ColorStops.Add(compositor.CreateColorGradientStop(0, clear));
        gradient.ColorStops.Add(compositor.CreateColorGradientStop(0.5f, ActualTheme == ElementTheme.Light ? Windows.UI.Color.FromArgb(110, 0, 0, 0) : Windows.UI.Color.FromArgb(160, 255, 255, 255)));
        gradient.ColorStops.Add(compositor.CreateColorGradientStop(1, clear));
        var linear = compositor.CreateLinearEasingFunction();
        gradient.StartAnimation(nameof(CompositionLinearGradientBrush.StartPoint), Sweep(compositor, linear, -0.6f));
        gradient.StartAnimation(nameof(CompositionLinearGradientBrush.EndPoint), Sweep(compositor, linear, 0));
        var brush = compositor.CreateMaskBrush();
        brush.Source = gradient;
        brush.Mask = _mask ??= _text.GetAlphaMask();
        _shimmer = compositor.CreateSpriteVisual();
        _shimmer.Brush = brush;
        _shimmer.Size = _text.ActualSize;
        ElementCompositionPreview.SetElementChildVisual(_text, _shimmer);
    }
    private static Vector2KeyFrameAnimation Sweep(Compositor compositor, CompositionEasingFunction linear, float from)
    {
        var sweep = compositor.CreateVector2KeyFrameAnimation();
        sweep.InsertKeyFrame(0, new(from, 0), linear);
        sweep.InsertKeyFrame(1, new(from + 1.8f, 0), linear);
        sweep.Duration = TimeSpan.FromSeconds(1.8);
        sweep.IterationBehavior = AnimationIterationBehavior.Forever;
        return sweep;
    }
}
