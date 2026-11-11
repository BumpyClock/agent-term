using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentTerm_App;

internal sealed partial class TerminalPane : UserControl
{
    private bool _subscribed;
    internal event Action<bool>? SplitRequested;
    internal event Action? CloseRequested;

    internal TerminalPane(MainPage page)
    {
        InitializeComponent();
        TitleText.Text = page.Profile.Name;
        TerminalContent.Content = page;
        ApplyMaterial();
    }

    internal void SetFocused(bool focused) => ActiveBorder.Visibility = focused ? Visibility.Visible : Visibility.Collapsed;

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
        var acrylic = TerminalPreferences.Current.UseAcrylic;
        SolidSurface.Visibility = acrylic ? Visibility.Collapsed : Visibility.Visible;
        AcrylicSurface.Visibility = acrylic ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SplitRight_Click(object sender, RoutedEventArgs e) => SplitRequested?.Invoke(true);
    private void SplitDown_Click(object sender, RoutedEventArgs e) => SplitRequested?.Invoke(false);
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
