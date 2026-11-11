using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using AgentTerm.Terminal;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace AgentTerm_App;

internal sealed partial class SettingsWindow : Window
{
    private readonly record struct SettingsValues(string ProfileId, string FontFamily, int FontSize, bool UseAcrylic);

    private readonly TerminalPreferences _preferences = TerminalPreferences.Current;
    private readonly IReadOnlyList<ShellProfile> _profiles;
    private SettingsValues _saved;
    private bool _updatingControls = true;

    internal SettingsWindow(Window owner, IReadOnlyList<ShellProfile> profiles)
    {
        _profiles = profiles;
        InitializeComponent();
        SettingsNavigation.Loaded += (_, _) => ApplyCaptionTheme();
        SettingsNavigation.ActualThemeChanged += (_, _) => ApplyCaptionTheme();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        SetInitialBounds(owner);
        RestoreDraft();
        SettingsNavigation.SelectedItem = StartupItem;
        if (_preferences.LoadError is { } error)
            ShowNotice(InfoBarSeverity.Warning, "Settings could not be loaded", error);
    }

    private void ApplyCaptionTheme()
    {
        // The native caption needs its own theme setting with Windows App SDK 1.8.
        int dark = SettingsNavigation.ActualTheme == ElementTheme.Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 20, in dark, sizeof(int));
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, in int value, uint size);

    internal void ActivateSettings()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    private void SetInitialBounds(Window owner)
    {
        var scale = owner.Content.XamlRoot?.RasterizationScale ?? 1;
        var area = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(Pixels(980), area.Width);
        var height = Math.Min(Pixels(700), area.Height);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min(Pixels(760), area.Width);
            presenter.PreferredMinimumHeight = Math.Min(Pixels(480), area.Height);
        }
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2,
            width, height));

        int Pixels(int dips) => (int)Math.Round(dips * scale);
    }

    private void SettingsNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs e)
    {
        StartupPage.Visibility = ReferenceEquals(e.SelectedItem, StartupItem) ? Visibility.Visible : Visibility.Collapsed;
        AppearancePage.Visibility = ReferenceEquals(e.SelectedItem, AppearanceItem) ? Visibility.Visible : Visibility.Collapsed;
        DefaultsPage.Visibility = ReferenceEquals(e.SelectedItem, DefaultsItem) ? Visibility.Visible : Visibility.Collapsed;
        DetailScroll.ChangeView(null, 0, null, true);
    }

    private void RestoreDraft()
    {
        _saved = new SettingsValues(_preferences.DefaultProfileId ?? "", _preferences.FontFamily,
            _preferences.FontSize, _preferences.UseAcrylic);
        _updatingControls = true;
        PopulateChoices(DefaultProfileBox, _saved.ProfileId, "Automatic", _profiles.Select(profile => (profile.Id, profile.Name)));
        var fonts = TerminalSurface.FontFamilies;
        PopulateChoices(FontFamilyBox, _saved.FontFamily, $"Automatic ({fonts[0]})", fonts.Select(font => (font, font)));
        FontSizeBox.Text = _saved.FontSize.ToString(CultureInfo.InvariantCulture);
        BackgroundBox.SelectedIndex = _saved.UseAcrylic ? 1 : 0;
        _updatingControls = false;
        UpdateActions();
    }

    private static void PopulateChoices(ComboBox box, string saved, string automatic, IEnumerable<(string Id, string Label)> choices)
    {
        box.Items.Clear();
        var auto = new ComboBoxItem { Content = automatic, Tag = "" };
        box.Items.Add(auto);
        ComboBoxItem? selected = saved.Length == 0 ? auto : null;
        foreach (var (id, label) in choices)
        {
            bool matches = string.Equals(id, saved, StringComparison.OrdinalIgnoreCase);
            var item = new ComboBoxItem { Content = label, Tag = matches ? saved : id };
            box.Items.Add(item);
            if (matches) selected = item;
        }
        if (selected is null)
        {
            selected = new ComboBoxItem { Content = $"Unavailable: {saved}", Tag = saved };
            box.Items.Add(selected);
        }
        box.SelectedItem = selected;
    }

    private static string? SelectedValue(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private void DraftChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingControls) return;
        if (SettingsNotice.Severity == InfoBarSeverity.Error) SettingsNotice.IsOpen = false;
        UpdateActions();
    }

    private void UpdateActions()
    {
        bool dirty = SelectedValue(DefaultProfileBox) != _saved.ProfileId || SelectedValue(FontFamilyBox) != _saved.FontFamily ||
            FontSizeBox.Text != _saved.FontSize.ToString(CultureInfo.InvariantCulture) ||
            BackgroundBox.SelectedIndex != (_saved.UseAcrylic ? 1 : 0);
        SaveButton.IsEnabled = DiscardButton.IsEnabled = dirty;
        FooterStatus.Text = dirty ? "Unsaved changes. Closing this window discards them." : "";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(FontSizeBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fontSize) ||
            fontSize is < 8 or > 36)
        {
            SettingsNavigation.SelectedItem = DefaultsItem;
            ShowNotice(InfoBarSeverity.Error, "Check the font size", "Enter a whole number from 8 to 36.");
            FontSizeBox.Focus(FocusState.Programmatic);
            FontSizeBox.SelectAll();
            return;
        }
        if (SelectedValue(DefaultProfileBox) is not { } profile || SelectedValue(FontFamilyBox) is not { } family ||
            BackgroundBox.SelectedIndex is < 0 or > 1)
        {
            ShowNotice(InfoBarSeverity.Error, "Check your settings", "Choose a profile, font family, and pane background.");
            return;
        }

        try { _preferences.Update(profile, fontSize, BackgroundBox.SelectedIndex == 1, family); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowNotice(InfoBarSeverity.Error, "Settings could not be saved", exception.Message);
            return;
        }
        SettingsNotice.IsOpen = false;
        RestoreDraft();
        FooterStatus.Text = "Settings saved.";
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        SettingsNotice.IsOpen = false;
        RestoreDraft();
        FooterStatus.Text = "Changes discarded.";
    }

    private void ShowNotice(InfoBarSeverity severity, string title, string message)
    {
        SettingsNotice.Severity = severity;
        SettingsNotice.Title = title;
        SettingsNotice.Message = message;
        SettingsNotice.IsOpen = true;
        DetailScroll.ChangeView(null, 0, null, true);
    }
}
