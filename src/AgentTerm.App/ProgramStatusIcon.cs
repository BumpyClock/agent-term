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
    private readonly ProgressRing _activity = new() { Width = 20, Height = 20, MinWidth = 0, MinHeight = 0 };
    private readonly FrameworkElement _still = (FrameworkElement)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
        <Ellipse xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="18" Height="18"
                 Stroke="{ThemeResource TextFillColorSecondaryBrush}" StrokeThickness="1.5" StrokeDashArray="2,2"/>
        """);
    private readonly UISettings _settings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private TabViewItem? _observed;
    private long _statusToken;
    private long _iconToken;
    private IconSource? _source;
    private bool _loaded;

    public ProgramStatusIcon()
    {
        IsTabStop = false;
        IsHitTestVisible = false;
        _layout.Children.Add(_icon);
        _layout.Children.Add(_activity);
        _layout.Children.Add(_still);
        Content = _layout;
        AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Loaded += (_, _) => { _loaded = true; Connect(); _settings.AnimationsEnabledChanged += SettingsChanged; _settings.ColorValuesChanged += SettingsChanged; };
        Unloaded += (_, _) => { _loaded = false; Disconnect(); _activity.IsActive = false; _settings.AnimationsEnabledChanged -= SettingsChanged; _settings.ColorValuesChanged -= SettingsChanged; };
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
        if (!_loaded || Tab is not { } tab) return;
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
            _icon.Content = _source switch
            {
                ImageIconSource image => new ImageIcon { Source = image.ImageSource },
                FontIconSource font => new FontIcon { Glyph = font.Glyph, FontFamily = font.FontFamily, FontSize = 16 },
                _ => null
            };
        }
        var primary = (tab.Tag as TabProgramStatus)?.Primary;
        var working = primary?.State == AgentTerm.Terminal.ProgramState.Working;
        var showProgress = working && (primary?.Progress is not null || (_settings.AnimationsEnabled && !_accessibility.HighContrast));
        _icon.Width = _icon.Height = working ? 12 : 16;
        if (_icon.Content is FontIcon fontIcon) fontIcon.FontSize = working ? 12 : 16;
        _activity.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        _activity.IsIndeterminate = primary?.Progress is null;
        _activity.Value = primary?.Progress ?? 0;
        _activity.IsActive = showProgress;
        _still.Visibility = working && !showProgress ? Visibility.Visible : Visibility.Collapsed;
    }
}
