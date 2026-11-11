using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Automation;
using Windows.UI.ViewManagement;

namespace AgentTerm_App;

/// <summary>A moving highlight on text only. No idle timer or whole-row opacity pulse.</summary>
internal sealed class ActivityLabel : Grid
{
    private readonly TextBlock _text = new() { Style = AgentUi.Style("AgentLabelText") };
    private readonly TextBlock _highlight = new() { Style = AgentUi.Style("AgentLabelText"), IsHitTestVisible = false };
    private readonly UISettings _settings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private Storyboard? _shimmer;
    private bool _active;
    private bool _loaded;
    public ActivityLabel()
    {
        Children.Add(_text); Children.Add(_highlight);
        AutomationProperties.SetAccessibilityView(_highlight, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        // HighContrastChanged subscription throws 0x80070490 in the unpackaged WinUI host.
        // ColorValuesChanged also signals contrast changes without that unsupported subscription.
        Loaded += (_, _) => { _loaded = true; _settings.AnimationsEnabledChanged += AnimationsChanged; _settings.ColorValuesChanged += ColorsChanged; Refresh(); };
        ActualThemeChanged += (_, _) => { Stop(); Refresh(); };
        Unloaded += (_, _) => { _loaded = false; _settings.AnimationsEnabledChanged -= AnimationsChanged; _settings.ColorValuesChanged -= ColorsChanged; Stop(); };
    }
    public void Update(string text, bool active)
    {
        if (_text.Text != text) _text.Text = _highlight.Text = text;
        if (_active == active) return;
        _active = active; Refresh();
    }
    private void AnimationsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(Refresh);
    private void ColorsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(Refresh);
    private void Stop() { _shimmer?.Stop(); _shimmer = null; _highlight.Visibility = Visibility.Collapsed; }
    private void Refresh()
    {
        if (!_active || !_loaded || !_settings.AnimationsEnabled || _accessibility.HighContrast) { Stop(); return; }
        if (_shimmer is not null) return;
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
        _shimmer = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        for (var i = 0; i < 3; i++)
        {
            var stop = new GradientStop { Color = i == 1 ? (ActualTheme == ElementTheme.Light ? Windows.UI.Color.FromArgb(110, 0, 0, 0) : Windows.UI.Color.FromArgb(160, 255, 255, 255)) : Windows.UI.Color.FromArgb(0, 255, 255, 255), Offset = -0.6 + i * 0.3 };
            brush.GradientStops.Add(stop);
            var motion = new DoubleAnimation { From = -0.6 + i * 0.3, To = 1.2 + i * 0.3, Duration = new Duration(TimeSpan.FromSeconds(1.8)), EnableDependentAnimation = true };
            Storyboard.SetTarget(motion, stop); Storyboard.SetTargetProperty(motion, "Offset"); _shimmer.Children.Add(motion);
        }
        _highlight.Foreground = brush; _highlight.Visibility = Visibility.Visible; _shimmer.Begin();
    }
}
