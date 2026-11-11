using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentTerm.Terminal;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;

namespace AgentTerm_App;

public sealed partial class MainWindow : Window
{
    private readonly IReadOnlyList<ShellProfile> _profiles = ShellProfile.Discover();
    private TerminalTab? _visibleTab;
    private ContentDialog? _settingsDialog;
    private bool _settingsLoadErrorShown;
    private bool _closing;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        WindowRoot.Loaded += (_, _) => SetTitleBar(TabDragRegion);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new SizeInt32(1040, 760));
        SystemBackdrop = new MicaBackdrop();
        BuildProfileMenu();
        AddTab(DefaultProfile);
        AppWindow.Closing += Window_Closing;
    }

    private ShellProfile DefaultProfile => _profiles.FirstOrDefault(profile =>
        profile.Id == TerminalPreferences.Current.DefaultProfileId) ?? _profiles.FirstOrDefault() ?? ShellProfile.Default;

    private void BuildProfileMenu()
    {
        foreach (var profile in _profiles)
        {
            var item = new MenuFlyoutItem { Text = profile.Name };
            item.Click += (_, _) =>
            {
                if (Down(VirtualKey.Menu) && _visibleTab is { } tab) tab.SplitDuplicate(profile);
                else AddTab(profile);
            };
            ProfileMenu.Items.Add(item);
        }

        ProfileMenu.Items.Add(new MenuFlyoutSeparator());
        var splitRight = new MenuFlyoutItem { Text = "Split right", KeyboardAcceleratorTextOverride = "Alt+Shift++" };
        splitRight.Click += (_, _) => _visibleTab?.Split(sideBySide: true);
        ProfileMenu.Items.Add(splitRight);
        var splitDown = new MenuFlyoutItem { Text = "Split down", KeyboardAcceleratorTextOverride = "Alt+Shift+-" };
        splitDown.Click += (_, _) => _visibleTab?.Split(sideBySide: false);
        ProfileMenu.Items.Add(splitDown);
        var find = new MenuFlyoutItem { Text = "Find", KeyboardAcceleratorTextOverride = "Ctrl+Shift+F" };
        find.Click += (_, _) => _visibleTab?.ActivePage.ShowFind();
        ProfileMenu.Items.Add(find);
        var stop = new MenuFlyoutItem { Text = "Stop session" };
        stop.Click += async (_, _) =>
        {
            if (_visibleTab is { } tab) await tab.ActivePage.StopSessionAsync();
        };
        ProfileMenu.Items.Add(stop);
        var restart = new MenuFlyoutItem { Text = "Restart session" };
        restart.Click += async (_, _) =>
        {
            if (_visibleTab is { } tab) await tab.ActivePage.RestartSessionAsync();
        };
        ProfileMenu.Items.Add(restart);
        ProfileMenu.Opening += (_, _) =>
        {
            var page = _visibleTab is { IsClosing: false } tab ? tab.ActivePage : null;
            splitRight.IsEnabled = _visibleTab?.CanSplit(sideBySide: true) == true;
            splitDown.IsEnabled = _visibleTab?.CanSplit(sideBySide: false) == true;
            find.IsEnabled = page is not null;
            stop.IsEnabled = page?.CanStopSession == true;
            restart.IsEnabled = page?.CanRestartSession == true;
        };
        ProfileMenu.Items.Add(new MenuFlyoutSeparator());
        var settings = new MenuFlyoutItem { Text = "Settings", KeyboardAcceleratorTextOverride = "Ctrl+," };
        settings.Click += (_, _) => ShowSettings();
        ProfileMenu.Items.Add(settings);
    }

    private void NewTabButton_Click(SplitButton sender, SplitButtonClickEventArgs args) => AddTab(DefaultProfile);

    private void AddTab(ShellProfile profile)
    {
        if (_closing) return;
        var tab = new TerminalTab(profile, (Style)WindowRoot.Resources["PaneSplitterStyle"]);
        var item = new TabViewItem { Header = tab.Title, Content = tab };
        tab.TitleChanged += () => item.Header = tab.Title;
        tab.CloseRequested += async () => await CloseTabAsync(item);
        Tabs.TabItems.Add(item);
        Tabs.SelectedItem = item;
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var next = (Tabs.SelectedItem as TabViewItem)?.Content as TerminalTab;
        if (ReferenceEquals(next, _visibleTab)) return;
        _visibleTab?.SetVisible(false);
        _visibleTab = next;
        if (_closing || next is null || next.IsClosing) return;
        next.SetVisible(true);
        next.FocusActivePane();
    }

    private async void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) =>
        await CloseTabAsync(args.Tab);

    private async Task CloseTabAsync(TabViewItem item)
    {
        if (_closing || !item.IsClosable || item.Content is not TerminalTab tab) return;

        // Keep a closing tab in the collection until cleanup completes. Window close awaits it too.
        item.IsClosable = false;
        item.IsEnabled = false;
        await tab.DisposeAsync();
        if (_closing) return;

        var index = Tabs.TabItems.IndexOf(item);
        Tabs.TabItems.Remove(item);
        if (Tabs.TabItems.Count == 0) Close();
        else if (Tabs.SelectedItem is null) Tabs.SelectedIndex = Math.Min(index, Tabs.TabItems.Count - 1);
    }

    private void SwitchTab(int direction)
    {
        if (Tabs.TabItems.Count < 2) return;
        var count = Tabs.TabItems.Count;
        var start = Math.Max(0, Tabs.SelectedIndex);
        for (var step = 1; step < count; step++)
        {
            var candidate = (start + direction * step + count) % count;
            if (Tabs.TabItems[candidate] is not TabViewItem { IsEnabled: true }) continue;
            Tabs.SelectedIndex = candidate;
            return;
        }
    }

    private async void WindowRoot_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_closing || _settingsDialog is not null || Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) return;
        var control = Down(VirtualKey.Control);
        var shift = Down(VirtualKey.Shift);
        var alt = Down(VirtualKey.Menu);
        var firstPress = !e.KeyStatus.WasKeyDown;

        if (control && !alt && e.Key == VirtualKey.Tab)
        {
            e.Handled = true;
            SwitchTab(shift ? -1 : 1);
        }
        else if (control && shift && !alt && e.Key is VirtualKey.T or VirtualKey.W or VirtualKey.D)
        {
            e.Handled = true;
            if (!firstPress) return;
            switch (e.Key)
            {
                case VirtualKey.T:
                    AddTab(DefaultProfile);
                    break;
                case VirtualKey.W when _visibleTab is { } tab:
                    await tab.CloseActivePaneAsync();
                    break;
                case VirtualKey.D:
                    _visibleTab?.SplitDuplicate();
                    break;
            }
        }
        else if (alt && shift && !control && (uint)e.Key is 0xBB or 0x6B or 0xBD or 0x6D or 0x44)
        {
            e.Handled = true;
            if (!firstPress) return;
            if (e.Key == VirtualKey.D) _visibleTab?.SplitDuplicate();
            else _visibleTab?.Split(sideBySide: (uint)e.Key is 0xBB or 0x6B);
        }
        else if (alt && !control && e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down &&
            _visibleTab is { PaneCount: > 1 } focusedTab)
        {
            var horizontal = e.Key == VirtualKey.Left ? -1 : e.Key == VirtualKey.Right ? 1 : 0;
            var vertical = e.Key == VirtualKey.Up ? -1 : e.Key == VirtualKey.Down ? 1 : 0;
            if (shift) e.Handled = focusedTab.ResizeActivePane(horizontal, vertical);
            else
            {
                e.Handled = true;
                focusedTab.FocusAdjacentPane(horizontal, vertical);
            }
        }
        else if (control && !shift && !alt && (uint)e.Key == 0xBC)
        {
            e.Handled = true;
            if (firstPress) ShowSettings();
        }
    }

    private async void ShowSettings()
    {
        if (_closing || _settingsDialog is not null) return;
        var preferences = TerminalPreferences.Current;
        var profile = new ComboBox
        {
            Header = "Default profile",
            ItemsSource = _profiles,
            DisplayMemberPath = nameof(ShellProfile.Name),
            SelectedItem = DefaultProfile,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var fontSize = new NumberBox
        {
            Header = "Font size",
            Minimum = 8,
            Maximum = 36,
            Value = preferences.FontSize,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };
        var fontFamily = new ComboBox { Header = "Font family", HorizontalAlignment = HorizontalAlignment.Stretch };
        fontFamily.Items.Add($"Automatic ({TerminalSurface.FontFamilies[0]})");
        fontFamily.SelectedIndex = 0;
        foreach (var family in TerminalSurface.FontFamilies)
        {
            fontFamily.Items.Add(family);
            if (string.Equals(family, preferences.FontFamily, StringComparison.OrdinalIgnoreCase))
                fontFamily.SelectedItem = family;
        }
        var background = new ComboBox { Header = "Pane background", HorizontalAlignment = HorizontalAlignment.Stretch };
        background.Items.Add("Solid");
        background.Items.Add("Acrylic");
        background.SelectedIndex = preferences.UseAcrylic ? 1 : 0;
        var error = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Warning };
        if (!_settingsLoadErrorShown && preferences.LoadError is { } loadError)
        {
            error.Title = "Settings could not be loaded";
            error.Message = loadError;
            error.IsOpen = true;
            _settingsLoadErrorShown = true;
        }

        var fields = new StackPanel { Spacing = 12 };
        fields.Children.Add(profile);
        fields.Children.Add(fontFamily);
        fields.Children.Add(fontSize);
        fields.Children.Add(background);
        fields.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = WindowRoot.XamlRoot,
            Title = "Settings",
            Content = fields,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (profile.SelectedItem is not ShellProfile selected || !double.IsFinite(fontSize.Value) ||
                fontSize.Value < 8 || fontSize.Value > 36 || fontSize.Value != Math.Truncate(fontSize.Value))
            {
                error.Title = "Check your settings";
                error.Message = "Choose a default profile and a whole font size from 8 to 36.";
                error.IsOpen = true;
                args.Cancel = true;
                return;
            }

            var family = fontFamily.SelectedIndex > 0 ? (string)fontFamily.SelectedItem : "";
            try { preferences.Update(selected.Id, (int)fontSize.Value, background.SelectedIndex == 1, family); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error.Title = "Settings could not be saved";
                error.Message = exception.Message;
                error.IsOpen = true;
                args.Cancel = true;
            }
        };

        _settingsDialog = dialog;
        try { await dialog.ShowAsync(); }
        finally { _settingsDialog = null; }
    }

    private static bool Down(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private async void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;

        // Keep the window alive until every pane has released its shell and ConPTY.
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        _settingsDialog?.Hide();
        try
        {
            var tabs = Tabs.TabItems.OfType<TabViewItem>().Select(item => item.Content).OfType<TerminalTab>();
            await Task.WhenAll(tabs.Select(tab => tab.DisposeAsync().AsTask()));
        }
        finally
        {
            _allowClose = true;
            Close();
        }
    }
}
