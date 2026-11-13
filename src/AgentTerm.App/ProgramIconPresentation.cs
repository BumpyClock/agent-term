using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.ViewManagement;

namespace AgentTerm_App;

internal interface IProgramIconPreferences
{
    bool HighContrast { get; }
    bool AnimationsEnabled { get; }
    event Action? Changed;
}

// The tab adapter and report rows share source rendering, preference refresh and sweep visibility.
internal sealed class ProgramIconPresentation : ContentControl
{
    private readonly ContentPresenter _icon = new() { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgramActivity _activity;
    private readonly IProgramIconPreferences _preferences;
    private IconSource? _source;
    private double _glyphSize = 16;
    private bool _loaded;

    internal ProgramIconPresentation(IProgramIconPreferences? preferences = null, Func<bool>? motionAllowed = null)
    {
        _preferences = preferences ?? new SystemPreferences();
        _activity = new(ProgramActivityKind.TabIcon, motionAllowed ?? (() => !_preferences.HighContrast && _preferences.AnimationsEnabled));
        Width = Height = 20;
        IsTabStop = false;
        IsHitTestVisible = false;
        Content = new Grid { Width = 20, Height = 20, Children = { _icon, _activity } };
        AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        _activity.Changed += UpdateVisibility;
        Loaded += (_, _) =>
        {
            _loaded = true;
            _preferences.Changed += PreferencesChanged;
            RefreshPreferences();
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            _preferences.Changed -= PreferencesChanged;
        };
    }

    internal void Update(IconSource? source, bool working, double glyphSize = 16)
    {
        if (!ReferenceEquals(_source, source) || _glyphSize != glyphSize)
        {
            _source = source;
            _glyphSize = glyphSize;
            _icon.Content = Render(source);
        }
        RefreshPreferences();
        _activity.Working = working;
    }

    private void PreferencesChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_loaded) RefreshPreferences();
    });

    private void RefreshPreferences()
    {
        if (_icon.Content is Viewbox { Child: PathIcon mark })
        {
            // Clearing the local brand restores foreground inheritance in high contrast.
            if (_source is PathIconSource { Foreground: { } brand } && !_preferences.HighContrast) mark.Foreground = brand;
            else mark.ClearValue(IconElement.ForegroundProperty);
        }
        _activity.RefreshPreferences();
        UpdateVisibility();
    }

    private void UpdateVisibility() => _icon.Visibility = _activity.IsAnimating ? Visibility.Collapsed : Visibility.Visible;

    // Each rendered mark owns its geometry. The surrounding presenter keeps artwork at 16 DIP.
    private FrameworkElement? Render(IconSource? source) => source switch
    {
        ImageIconSource image => new ImageIcon { Source = image.ImageSource },
        FontIconSource font => RenderFont(font),
        PathIconSource path => new Viewbox
        {
            Child = new PathIcon { Data = AgentMarks.CreateGeometry(path), Width = AgentMarks.ViewBoxSize, Height = AgentMarks.ViewBoxSize }
        },
        _ => null
    };

    private FontIcon RenderFont(FontIconSource source)
    {
        var icon = new FontIcon { Glyph = source.Glyph, FontFamily = source.FontFamily, FontSize = _glyphSize };
        if (source.Foreground is { } foreground) icon.Foreground = foreground;
        return icon;
    }

    private sealed class SystemPreferences : IProgramIconPreferences
    {
        private readonly UISettings _settings = new();
        private readonly AccessibilitySettings _accessibility = new();
        private Action? _changed;
        public bool HighContrast => _accessibility.HighContrast;
        public bool AnimationsEnabled => _settings.AnimationsEnabled;
        public event Action? Changed
        {
            add
            {
                if (_changed is null)
                {
                    _settings.ColorValuesChanged += SettingsChanged;
                    _settings.AnimationsEnabledChanged += SettingsChanged;
                }
                _changed += value;
            }
            remove
            {
                _changed -= value;
                if (_changed is null)
                {
                    _settings.ColorValuesChanged -= SettingsChanged;
                    _settings.AnimationsEnabledChanged -= SettingsChanged;
                }
            }
        }
        private void SettingsChanged(UISettings sender, object args) => _changed?.Invoke();
    }
}
