using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AgentTerm.Terminal;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace AgentTerm_App;

internal sealed partial class SettingsWindow : Window
{
    private readonly record struct SettingsValues(string ProfileId, string FontFamily, int FontSize, bool UseAcrylic,
        double BackgroundOpacity, TabPosition TabPosition, TerminalColorScheme ColorScheme, string LightThemeId, string DarkThemeId,
        TerminalStartPosition TerminalStartPosition);
    private sealed record ProfileDraft(string? FontFamily, bool InheritFontSize, string? FontSizeText, bool? UseAcrylic,
        bool InheritOpacity, double? BackgroundOpacity, TerminalColorScheme? ColorScheme, string? LightThemeId, string? DarkThemeId);

    private readonly TerminalPreferences _preferences = TerminalPreferences.Current;
    private readonly IReadOnlyList<ShellProfile> _profiles;
    private readonly Dictionary<string, NavigationViewItem> _profileItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProfileDraft> _profileDrafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProfileDraft> _savedProfileDrafts = new(StringComparer.Ordinal);
    private Dictionary<string, ProfileAppearanceOverride> _savedOverrides = new(StringComparer.Ordinal);
    private string? _shownProfileId;
    private SettingsValues _saved;
    private bool _updatingControls = true;
    private EffectsSettingsPanel _programs = null!;
    private TerminalEffectOptions _savedEffects = TerminalEffectOptions.Default;

    internal SettingsWindow(Window owner, IReadOnlyList<ShellProfile> profiles)
    {
        _profiles = profiles;
        InitializeComponent();
        _programs = new EffectsSettingsPanel((Style)SettingsNavigation.Resources["SettingCard"], (Style)SettingsNavigation.Resources["SettingLabel"],
            (Style)SettingsNavigation.Resources["SettingDescription"]);
        _programs.Changed += () => { if (!_updatingControls) UpdateActions(); };
        ProgramsHost.Children.Add(_programs);
        SettingsNavigation.Loaded += (_, _) => ApplyCaptionTheme();
        SettingsNavigation.ActualThemeChanged += (_, _) => ApplyCaptionTheme();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        SetInitialBounds(owner);
        foreach (var profile in profiles)
        {
            var item = new NavigationViewItem { Content = profile.Name, Tag = profile.Id, Icon = new FontIcon { Glyph = "\uE756" } };
            _profileItems.Add(profile.Id, item);
            SettingsNavigation.MenuItems.Add(item);
            _ = SetProfileIconAsync(item, profile);
        }
        RestoreDraft();
        SettingsNavigation.SelectedItem = StartupItem;
        if (_preferences.LoadError is { } error)
            ShowNotice(InfoBarSeverity.Warning, "Settings could not be loaded", error);
    }

    private static async Task SetProfileIconAsync(NavigationViewItem item, ShellProfile profile)
    {
        try
        {
            var icon = await Task.Run(() => ExecutableIcon.Read(profile.Executable));
            if (icon is null) return;
            using var stream = new MemoryStream(icon).AsRandomAccessStream();
            var image = new BitmapImage { DecodePixelWidth = 32 };
            await image.SetSourceAsync(stream);
            item.Icon = new ImageIcon { Source = image };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException)
        {
        }
    }

    private void ApplyCaptionTheme()
    {
        int dark = SettingsNavigation.ActualTheme == ElementTheme.Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 20, in dark, sizeof(int));
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr window, uint attribute, in int value, uint size);

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
        CaptureProfileDraft();
        _shownProfileId = (e.SelectedItem as NavigationViewItem)?.Tag as string;
        StartupPage.Visibility = ReferenceEquals(e.SelectedItem, StartupItem) ? Visibility.Visible : Visibility.Collapsed;
        TabsPage.Visibility = ReferenceEquals(e.SelectedItem, TabsItem) ? Visibility.Visible : Visibility.Collapsed;
        ProgramsPage.Visibility = ReferenceEquals(e.SelectedItem, ProgramsItem) ? Visibility.Visible : Visibility.Collapsed;
        DefaultsPage.Visibility = ReferenceEquals(e.SelectedItem, DefaultsItem) ? Visibility.Visible : Visibility.Collapsed;
        ProfilePage.Visibility = _shownProfileId is null ? Visibility.Collapsed : Visibility.Visible;
        if (_shownProfileId is { } id) LoadProfileDraft(id);
        DetailScroll.ChangeView(null, 0, null, true);
    }

    private void RestoreDraft()
    {
        _saved = new SettingsValues(_preferences.DefaultProfileId ?? "", _preferences.FontFamily,
            _preferences.FontSize, _preferences.UseAcrylic, _preferences.BackgroundOpacity, _preferences.TabPosition,
            _preferences.ColorScheme, _preferences.LightThemeId, _preferences.DarkThemeId, _preferences.TerminalStartPosition);
        _updatingControls = true;
        _savedEffects = _preferences.Effects;
        _programs.Load(_savedEffects);
        _savedOverrides = _preferences.ProfileOverrides.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        _profileDrafts.Clear();
        _savedProfileDrafts.Clear();
        foreach (var profile in _profiles)
        {
            _savedOverrides.TryGetValue(profile.Id, out var value);
            var draft = new ProfileDraft(value?.FontFamily, value?.FontSize is null,
                value?.FontSize?.ToString(CultureInfo.InvariantCulture), value?.UseAcrylic,
                value?.BackgroundOpacity is null, value?.BackgroundOpacity, value?.ColorScheme, value?.LightThemeId, value?.DarkThemeId);
            _profileDrafts.Add(profile.Id, draft);
            _savedProfileDrafts.Add(profile.Id, draft);
        }
        PopulateChoices(DefaultProfileBox, _saved.ProfileId, "Automatic", _profiles.Select(profile => (profile.Id, profile.Name)));
        var fonts = TerminalSurface.FontFamilies;
        PopulateChoices(FontFamilyBox, _saved.FontFamily, $"Automatic ({fonts[0]})", fonts.Select(font => (font, font)));
        PopulateChoices(ColorSchemeBox, _saved.ColorScheme.ToString(), null, SchemeChoices);
        PopulateChoices(LightThemeBox, _saved.LightThemeId, null, ThemeChoices(true));
        PopulateChoices(DarkThemeBox, _saved.DarkThemeId, null, ThemeChoices(false));
        FontSizeBox.Text = _saved.FontSize.ToString(CultureInfo.InvariantCulture);
        BackgroundBox.SelectedIndex = _saved.UseAcrylic ? 1 : 0;
        TabPositionBox.SelectedIndex = (int)_saved.TabPosition;
        UpdateSelectionToolTip(TabPositionBox);
        TerminalStartPositionBox.SelectedIndex = (int)_saved.TerminalStartPosition;
        UpdateSelectionToolTip(TerminalStartPositionBox);
        OpacitySlider.Value = _saved.BackgroundOpacity * 100;
        DisplayOpacity();
        if (_shownProfileId is { } id) LoadProfileDraft(id);
        _updatingControls = false;
        UpdateActions();
    }

    private static void PopulateChoices(ComboBox box, string? saved, string? automatic,
        IEnumerable<(string Id, string Label)> choices, string? inheritedLabel = null)
    {
        box.Items.Clear();
        ComboBoxItem? selected = null;
        if (inheritedLabel is not null)
        {
            var inherit = new ComboBoxItem { Content = inheritedLabel };
            box.Items.Add(inherit);
            if (saved is null) selected = inherit;
        }
        if (automatic is not null)
        {
            var auto = new ComboBoxItem { Content = automatic, Tag = "" };
            box.Items.Add(auto);
            if (saved == "") selected = auto;
        }
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
        UpdateSelectionToolTip(box);
    }

    private static IEnumerable<(string Id, string Label)> SchemeChoices =>
        Enum.GetValues<TerminalColorScheme>().Select(scheme => (scheme.ToString(), scheme == TerminalColorScheme.Auto ? "Auto (system)" : scheme.ToString()));

    private static IEnumerable<(string Id, string Label)> ThemeChoices(bool light) =>
        TerminalThemes.All.Where(theme => theme.IsLight == light).Select(theme => (theme.Id, theme.Name));

    private static TerminalColorScheme? SelectedScheme(ComboBox box) =>
        Enum.TryParse<TerminalColorScheme>(SelectedValue(box), out var scheme) ? scheme : null;

    private static string? SelectedValue(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;
    private static void UpdateSelectionToolTip(ComboBox box) =>
        ToolTipService.SetToolTip(box, (box.SelectedItem as ComboBoxItem)?.Content);

    private void LoadProfileDraft(string id)
    {
        bool updating = _updatingControls;
        _updatingControls = true;
        var draft = _profileDrafts[id];
        ProfileTitle.Text = _profiles.First(profile => profile.Id == id).Name;
        var fonts = TerminalSurface.FontFamilies;
        var effectiveFamily = TerminalSurface.ResolveFontFamily(SelectedValue(FontFamilyBox));
        PopulateChoices(ProfileFontFamilyBox, draft.FontFamily, $"Automatic ({fonts[0]})",
            fonts.Select(font => (font, font)), inheritedLabel: $"Use default ({effectiveFamily})");
        PopulateChoices(ProfileColorSchemeBox, draft.ColorScheme?.ToString(), null, SchemeChoices,
            inheritedLabel: $"Use default ({(ColorSchemeBox.SelectedItem as ComboBoxItem)?.Content})");
        PopulateChoices(ProfileLightThemeBox, draft.LightThemeId, null, ThemeChoices(true),
            inheritedLabel: $"Use default ({(LightThemeBox.SelectedItem as ComboBoxItem)?.Content})");
        PopulateChoices(ProfileDarkThemeBox, draft.DarkThemeId, null, ThemeChoices(false),
            inheritedLabel: $"Use default ({(DarkThemeBox.SelectedItem as ComboBoxItem)?.Content})");
        ProfileInheritSize.IsChecked = draft.InheritFontSize;
        ProfileBackgroundBox.Items[0] = new ComboBoxItem
        {
            Content = $"Use default ({(BackgroundBox.SelectedIndex == 1 ? "Acrylic" : "Solid")})"
        };
        ProfileBackgroundBox.SelectedIndex = draft.UseAcrylic is null ? 0 : draft.UseAcrylic.Value ? 2 : 1;
        UpdateSelectionToolTip(ProfileBackgroundBox);
        DisplayProfileSize(draft);
        ProfileInheritOpacity.IsChecked = draft.InheritOpacity;
        DisplayProfileOpacity(draft);
        _updatingControls = updating;
    }

    private void DisplayProfileSize(ProfileDraft draft)
    {
        ProfileFontSizeBox.IsEnabled = !draft.InheritFontSize;
        ProfileFontSizeBox.Text = draft.InheritFontSize ? FontSizeBox.Text : draft.FontSizeText ?? FontSizeBox.Text;
    }

    private void DisplayOpacity()
    {
        OpacitySlider.IsEnabled = BackgroundBox.SelectedIndex == 1;
        OpacityDescription.Text = OpacitySlider.IsEnabled
            ? "Acrylic tint opacity. Terminal text stays opaque."
            : "Solid backgrounds are opaque. Choose Acrylic to adjust opacity.";
        OpacityValue.Text = $"{OpacitySlider.Value:0}%";
        AutomationProperties.SetName(OpacitySlider, $"Default background opacity, {OpacitySlider.Value:0} percent");
    }

    private void DisplayProfileOpacity(ProfileDraft draft)
    {
        bool acrylic = draft.UseAcrylic ?? BackgroundBox.SelectedIndex == 1;
        ProfileOpacitySlider.IsEnabled = acrylic && !draft.InheritOpacity;
        ProfileOpacitySlider.Value = draft.InheritOpacity ? OpacitySlider.Value : (draft.BackgroundOpacity ?? OpacitySlider.Value / 100) * 100;
        ProfileOpacityDescription.Text = acrylic
            ? "Acrylic tint opacity. Terminal text stays opaque."
            : "Solid backgrounds are opaque. Choose Acrylic to adjust opacity.";
        ProfileOpacityValue.Text = $"{ProfileOpacitySlider.Value:0}%";
        AutomationProperties.SetName(ProfileOpacitySlider, $"Profile background opacity, {ProfileOpacitySlider.Value:0} percent");
    }

    private void CaptureProfileDraft()
    {
        if (_updatingControls || _shownProfileId is not { } id) return;
        var previous = _profileDrafts[id];
        bool inherit = ProfileInheritSize.IsChecked == true;
        var rawSize = previous.FontSizeText;
        if (!previous.InheritFontSize) rawSize = ProfileFontSizeBox.Text;
        else if (!inherit && rawSize is null) rawSize = FontSizeBox.Text;
        bool inheritOpacity = ProfileInheritOpacity.IsChecked == true;
        var opacity = previous.BackgroundOpacity;
        if (!previous.InheritOpacity && ProfileOpacitySlider.Value != previous.BackgroundOpacity * 100)
            opacity = ProfileOpacitySlider.Value / 100;
        else if (!inheritOpacity && opacity is null) opacity = OpacitySlider.Value / 100;
        _profileDrafts[id] = new ProfileDraft(SelectedValue(ProfileFontFamilyBox), inherit, rawSize,
            ProfileBackgroundBox.SelectedIndex switch { 1 => false, 2 => true, _ => null }, inheritOpacity, opacity,
            SelectedScheme(ProfileColorSchemeBox), SelectedValue(ProfileLightThemeBox), SelectedValue(ProfileDarkThemeBox));
    }

    private void OpacityChanged(object sender, RangeBaseValueChangedEventArgs e) => DraftChanged(sender, e);

    private void DraftChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingControls) return;
        if (sender is ComboBox box) UpdateSelectionToolTip(box);
        CaptureProfileDraft();
        if (ReferenceEquals(sender, ProfileInheritSize) && _shownProfileId is { } id)
        {
            _updatingControls = true;
            DisplayProfileSize(_profileDrafts[id]);
            _updatingControls = false;
        }
        _updatingControls = true;
        DisplayOpacity();
        if (_shownProfileId is { } profileId) DisplayProfileOpacity(_profileDrafts[profileId]);
        _updatingControls = false;
        UpdateActions();
    }

    private void UpdateActions()
    {
        bool profilesDirty = _profileDrafts.Any(pair =>
        {
            var current = pair.Value;
            var saved = _savedProfileDrafts[pair.Key];
            return current.FontFamily != saved.FontFamily || current.ColorScheme != saved.ColorScheme ||
                current.LightThemeId != saved.LightThemeId || current.DarkThemeId != saved.DarkThemeId || current.UseAcrylic != saved.UseAcrylic ||
                current.InheritFontSize != saved.InheritFontSize ||
                (!current.InheritFontSize && current.FontSizeText != saved.FontSizeText) ||
                current.InheritOpacity != saved.InheritOpacity ||
                (!current.InheritOpacity && current.BackgroundOpacity != saved.BackgroundOpacity);
        });
        bool dirty = SelectedValue(DefaultProfileBox) != _saved.ProfileId || SelectedValue(FontFamilyBox) != _saved.FontFamily ||
            SelectedScheme(ColorSchemeBox) != _saved.ColorScheme ||
            SelectedValue(LightThemeBox) != _saved.LightThemeId || SelectedValue(DarkThemeBox) != _saved.DarkThemeId ||
            FontSizeBox.Text != _saved.FontSize.ToString(CultureInfo.InvariantCulture) ||
            BackgroundBox.SelectedIndex != (_saved.UseAcrylic ? 1 : 0) ||
            OpacitySlider.Value != _saved.BackgroundOpacity * 100 || SelectedTabPosition != _saved.TabPosition ||
            SelectedTerminalStartPosition != _saved.TerminalStartPosition || _programs.Options != _savedEffects || profilesDirty;
        SaveButton.IsEnabled = DiscardButton.IsEnabled = dirty;
        FooterStatus.Text = dirty ? "Unsaved changes. Closing this window discards them." : "";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CaptureProfileDraft();
        if (!TryFontSize(FontSizeBox.Text, out var fontSize))
        {
            ShowFontSizeError(null);
            return;
        }
        if (SelectedValue(DefaultProfileBox) is not { } profile || SelectedValue(FontFamilyBox) is not { } family ||
            SelectedScheme(ColorSchemeBox) is not { } colorScheme || SelectedValue(LightThemeBox) is not { } lightThemeId ||
            SelectedValue(DarkThemeBox) is not { } darkThemeId || BackgroundBox.SelectedIndex is < 0 or > 1)
        {
            ShowNotice(InfoBarSeverity.Error, "Check your settings", "Choose a profile, font family, color theme, and pane background.");
            return;
        }

        var overrides = new Dictionary<string, ProfileAppearanceOverride>(_savedOverrides, StringComparer.Ordinal);
        foreach (var shell in _profiles)
        {
            var draft = _profileDrafts[shell.Id];
            int? size = null;
            if (!draft.InheritFontSize)
            {
                if (!TryFontSize(draft.FontSizeText, out var profileSize))
                {
                    ShowFontSizeError(shell.Id);
                    return;
                }
                size = profileSize;
            }
            var value = new ProfileAppearanceOverride(draft.FontFamily, size, draft.UseAcrylic,
                draft.InheritOpacity ? null : draft.BackgroundOpacity, draft.ColorScheme, draft.LightThemeId, draft.DarkThemeId);
            if (value.FontFamily is null && value.FontSize is null && value.UseAcrylic is null && value.BackgroundOpacity is null &&
                value.ColorScheme is null && value.LightThemeId is null && value.DarkThemeId is null)
                overrides.Remove(shell.Id);
            else
                overrides[shell.Id] = value;
        }
        try
        {
            _preferences.Update(profile, fontSize, BackgroundBox.SelectedIndex == 1, family, overrides, OpacitySlider.Value / 100,
                SelectedTabPosition, colorScheme, lightThemeId, darkThemeId, SelectedTerminalStartPosition);
            _preferences.UpdateEffects(_programs.Options);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowNotice(InfoBarSeverity.Error, "Settings could not be saved", exception.Message);
            return;
        }
        SettingsNotice.IsOpen = false;
        RestoreDraft();
        FooterStatus.Text = "Settings saved.";
    }

    private TerminalStartPosition SelectedTerminalStartPosition =>
        TerminalStartPositionBox.SelectedIndex == 1 ? TerminalStartPosition.Top : TerminalStartPosition.Bottom;

    private TabPosition SelectedTabPosition => TabPositionBox.SelectedIndex == 1 ? TabPosition.Left : TabPosition.Top;

    private static bool TryFontSize(string? text, out int size) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out size) && size is >= 8 and <= 36;

    private void ShowFontSizeError(string? profileId)
    {
        SettingsNavigation.SelectedItem = profileId is null ? DefaultsItem : _profileItems[profileId];
        ShowNotice(InfoBarSeverity.Error, "Check the font size", "Enter a whole number from 8 to 36.");
        var input = profileId is null ? FontSizeBox : ProfileFontSizeBox;
        input.Focus(FocusState.Programmatic);
        input.SelectAll();
    }

    private void ResetProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_shownProfileId is not { } id) return;
        _profileDrafts[id] = new ProfileDraft(null, true, null, null, true, null, null, null, null);
        LoadProfileDraft(id);
        SettingsNotice.IsOpen = false;
        UpdateActions();
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
