using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AgentTerm_App;

internal sealed partial class TerminalPane : UserControl, IDisposable
{
    private readonly MainPage _page;
    private readonly PaneAcrylicBackdrop _backdrop = new();
    internal event Action<bool>? SplitRequested;
    internal event Action? CloseRequested;

    internal TerminalPane(MainPage page)
    {
        _page = page;
        InitializeComponent();
        _backdrop.AttachmentChanged += UpdateSolidFallback;
        TerminalContent.Content = page;
        ActualThemeChanged += Pane_ActualThemeChanged;
        // Splits can deliver a late Unloaded after this pane is reparented and visible again.
        TerminalPreferences.Current.Changed += ApplyMaterial;
        _page.TerminalBackgroundChanged += ApplyMaterial;
        // Keep the native backdrop link stable across material and opacity changes.
        PaneBackdrop.SystemBackdrop = _backdrop;
        ApplyMaterial();
    }

    internal void SetFocused(bool focused, bool multiplePanes)
    {
        ActiveBorder.Visibility = focused && multiplePanes ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Pane_Loaded(object sender, RoutedEventArgs e) => ApplyMaterial();

    private void Pane_ActualThemeChanged(FrameworkElement sender, object args) => ApplyMaterial();

    public void Dispose()
    {
        TerminalPreferences.Current.Changed -= ApplyMaterial;
        _page.TerminalBackgroundChanged -= ApplyMaterial;
        ActualThemeChanged -= Pane_ActualThemeChanged;
        Loaded -= Pane_Loaded;
        _backdrop.Dispose();
        _backdrop.AttachmentChanged -= UpdateSolidFallback;
    }

    private void ApplyMaterial()
    {
        var appearance = TerminalPreferences.Current.ResolveAppearance(_page.Profile.Id);
        var background = _page.TerminalBackground;
        var color = Color.FromArgb(255, background.R, background.G, background.B);
        SolidSurface.Background = new SolidColorBrush(color);
        _backdrop.UpdateMaterial(color, appearance.UseAcrylic ? appearance.BackgroundOpacity : 1);
        UpdateSolidFallback();
    }

    private void UpdateSolidFallback() => SolidSurface.Visibility =
        _backdrop.IsAttached ? Visibility.Collapsed : Visibility.Visible;

    private void SplitRight_Click(object sender, RoutedEventArgs e) => SplitRequested?.Invoke(true);
    private void SplitDown_Click(object sender, RoutedEventArgs e) => SplitRequested?.Invoke(false);
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
