using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Windows.UI.ViewManagement;

namespace AgentTerm_App;

internal sealed partial class ProgramStatusIcon : ContentControl
{
    public static readonly DependencyProperty TabProperty = DependencyProperty.Register(nameof(Tab), typeof(TabViewItem), typeof(ProgramStatusIcon), new PropertyMetadata(null, (sender, _) => ((ProgramStatusIcon)sender).Connect()));
    public TabViewItem? Tab { get => (TabViewItem?)GetValue(TabProperty); set => SetValue(TabProperty, value); }
    private readonly Grid _layout = new() { Width = 20, Height = 20 };
    private readonly ContentPresenter _icon = new() { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgramActivity _activity;
    private readonly UISettings _settings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private TabViewItem? _observed;
    private long _statusToken;
    private long _iconToken;
    private IconSource? _source;
    private bool _loaded;

    public ProgramStatusIcon() : this(null) { }

    internal ProgramStatusIcon(Func<bool>? motionAllowed)
    {
        _activity = new(ProgramActivityKind.TabIcon, motionAllowed);
        IsTabStop = false;
        IsHitTestVisible = false;
        _layout.Children.Add(_icon);
        _layout.Children.Add(_activity);
        _activity.Changed += UpdatePresentation;
        Content = _layout;
        AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Loaded += (_, _) => { _loaded = true; Connect(); _settings.ColorValuesChanged += SettingsChanged; };
        Unloaded += (_, _) => { _loaded = false; Disconnect(); _activity.Working = false; _settings.ColorValuesChanged -= SettingsChanged; };
    }

    private void SettingsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(Refresh);
    private void Disconnect()
    {
        if (_observed is null) return;
        _observed.UnregisterPropertyChangedCallback(TabProgramStatus.DetailsProperty, _statusToken);
        _observed.UnregisterPropertyChangedCallback(TabViewItem.IconSourceProperty, _iconToken);
        _observed = null;
    }
    private void Connect()
    {
        Disconnect();
        if (!_loaded || Tab is not { } tab)
        {
            _activity.Working = false;
            _source = null;
            _icon.Content = null;
            return;
        }
        _observed = tab;
        _statusToken = tab.RegisterPropertyChangedCallback(TabProgramStatus.DetailsProperty, (_, _) => Refresh());
        _iconToken = tab.RegisterPropertyChangedCallback(TabViewItem.IconSourceProperty, (_, _) => Refresh());
        Refresh();
    }
    private void Refresh()
    {
        var tab = _observed;
        if (tab is null) return;
        if (!ReferenceEquals(_source, tab.IconSource))
        {
            _source = tab.IconSource;
            _icon.Content = Render(_source);
        }
        ApplyBrand(_icon.Content, _source, _accessibility.HighContrast);
        _activity.Working = (tab.Tag as TabProgramStatus)?.Primary?.State == AgentTerm.Terminal.ProgramState.Working;
        UpdatePresentation();
    }

    /// <summary>Renders a tab icon source as a 16-DIP element. Each call parses its own mark geometry.</summary>
    internal static FrameworkElement? Render(IconSource? source) => source switch
    {
        ImageIconSource image => new ImageIcon { Source = image.ImageSource },
        FontIconSource font => new FontIcon { Glyph = font.Glyph, FontFamily = font.FontFamily, FontSize = 16 },
        PathIconSource path => new Viewbox
        {
            Child = new PathIcon { Data = AgentMarks.CreateGeometry(path), Width = AgentMarks.ViewBoxSize, Height = AgentMarks.ViewBoxSize }
        },
        _ => null
    };

    internal static void ApplyBrand(object? rendered, IconSource? source, bool highContrast)
    {
        if (rendered is not Viewbox { Child: PathIcon mark }) return;
        // High contrast replaces brand color with the system foreground.
        if (source is PathIconSource { Foreground: { } brand } && !highContrast) mark.Foreground = brand;
        else mark.ClearValue(IconElement.ForegroundProperty);
    }

    private void UpdatePresentation() => _icon.Visibility = _activity.IsAnimating ? Visibility.Collapsed : Visibility.Visible;
}
