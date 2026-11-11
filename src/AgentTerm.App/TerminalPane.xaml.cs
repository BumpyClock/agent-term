using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentTerm_App;

internal sealed partial class TerminalPane : UserControl
{
    private bool _subscribed;
    private readonly string _profileId;
    internal event Action<bool>? SplitRequested;
    internal event Action? CloseRequested;

    internal TerminalPane(MainPage page)
    {
        _profileId = page.Profile.Id;
        InitializeComponent();
        TitleText.Text = page.Profile.Name;
        TerminalContent.Content = page;
        ApplyMaterial();
    }

    internal void SetFocused(bool focused, bool multiplePanes)
    {
        var stroke = new Thickness(multiplePanes ? 0.5 : 0);
        SolidSurface.BorderThickness = stroke;
        AcrylicSurface.BorderThickness = stroke;
        ActiveBorder.Visibility = focused && multiplePanes ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Pane_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            TerminalPreferences.Current.Changed += ApplyMaterial;
            _subscribed = true;
        }
        ApplyMaterial();
    }

    private void Pane_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed) return;
        TerminalPreferences.Current.Changed -= ApplyMaterial;
        _subscribed = false;
    }

    private void ApplyMaterial()
    {
        var appearance = TerminalPreferences.Current.ResolveAppearance(_profileId);
        PaneAcrylicBrush.TintOpacity = appearance.BackgroundOpacity;
        SolidSurface.Visibility = appearance.UseAcrylic ? Visibility.Collapsed : Visibility.Visible;
        AcrylicSurface.Visibility = appearance.UseAcrylic ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SplitRight_Click(object sender, RoutedEventArgs e) => SplitRequested?.Invoke(true);
    private void SplitDown_Click(object sender, RoutedEventArgs e) => SplitRequested?.Invoke(false);
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
