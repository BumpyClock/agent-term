using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AgentTerm.Terminal;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;

namespace AgentTerm_App;

public sealed partial class MainWindow : Window
{
    private readonly ShellProfileDiscovery _profileDiscovery = new(ShellProfile.DiscoverBuiltIn(), () => Task.Run(ShellProfile.DiscoverWsl));
    private IReadOnlyList<ShellProfile> _profiles => _profileDiscovery.Profiles;
    private int _profileItems;
    private TerminalTab? _visibleTab;
    private SettingsWindow? _settingsWindow;
    private bool _closing;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        WindowRoot.Loaded += (_, _) =>
        {
            SetTitleBar(TabDragRegion);
            ApplyCaptionTheme();
            int hostBackdropEnabled = 1;
            _ = DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 17,
                in hostBackdropEnabled, sizeof(int));
        };
        WindowRoot.ActualThemeChanged += (_, _) => ApplyCaptionTheme();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new SizeInt32(1040, 760));
        SystemBackdrop = new MicaBackdrop();
        var statusTimer = DispatcherQueue.CreateTimer();
        statusTimer.Interval = TimeSpan.FromMilliseconds(250);
        void UpdateProgramStatuses()
        {
            if (_closing) return;
            foreach (var item in Tabs.TabItems.OfType<TabViewItem>())
            {
                if (item.Content is not TerminalTab { IsClosing: false } tab) continue;
                tab.RefreshTitle();
                if (item.Tag is TabProgramStatus status && tab.TryReadProgramStatusChange(out var statuses))
                    status.Update(statuses);
            }
        }
        statusTimer.Tick += (_, _) => UpdateProgramStatuses();
        statusTimer.Start();
        var minimized = false;
        AppWindow.Changed += (window, _) =>
        {
            var current = window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
            if (current == minimized) return;
            minimized = current;
            PaneStatus.GitStatusHub.Shared.Paused = minimized;
            if (minimized) statusTimer.Stop();
            else if (!_closing)
            {
                UpdateProgramStatuses();
                statusTimer.Start();
            }
        };
        Closed += (_, _) => statusTimer.Stop();
        BuildProfileMenu();
        _ = AddWslProfilesAsync();
        _ = AddDefaultTabAsync();
        AppWindow.Closing += Window_Closing;
        Closed += (_, _) => _settingsWindow?.Close();
    }

    private void ApplyCaptionTheme()
    {
        int dark = WindowRoot.ActualTheme == ElementTheme.Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 20, in dark, sizeof(int));

        var highContrast = new AccessibilitySettings().HighContrast;
        var titleBar = AppWindow.TitleBar;
        Windows.UI.Color? foreground = highContrast ? null : dark != 0 ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = highContrast ? null : dark != 0
            ? Windows.UI.Color.FromArgb(255, 153, 153, 153) : Windows.UI.Color.FromArgb(255, 96, 96, 96);
        titleBar.ButtonBackgroundColor = highContrast ? null : Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = highContrast ? null : Microsoft.UI.Colors.Transparent;
        var overlay = dark != 0 ? (byte)255 : (byte)0;
        titleBar.ButtonHoverBackgroundColor = highContrast ? null : Windows.UI.Color.FromArgb(24, overlay, overlay, overlay);
        titleBar.ButtonPressedBackgroundColor = highContrast ? null : Windows.UI.Color.FromArgb(40, overlay, overlay, overlay);
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr window, uint attribute, in int value, uint size);

    private async Task AddDefaultTabAsync()
    {
        var profile = await _profileDiscovery.ResolveDefaultAsync(TerminalPreferences.Current.DefaultProfileId);
        AddTab(profile);
    }

    private async Task AddWslProfilesAsync()
    {
        var wsl = await _profileDiscovery.DiscoverWslAsync();
        if (_closing) return;
        foreach (var profile in wsl)
        {
            ProfileMenu.Items.Insert(_profileItems++, CreateProfileItem(profile));
        }
    }

    private MenuFlyoutItem CreateProfileItem(ShellProfile profile)
    {
        var item = new MenuFlyoutItem { Text = profile.Name };
        item.Click += (_, _) =>
        {
            if (Down(VirtualKey.Menu) && _visibleTab is { } tab) tab.SplitDuplicate(profile);
            else AddTab(profile);
        };
        return item;
    }

    private void BuildProfileMenu()
    {
        foreach (var profile in _profiles) ProfileMenu.Items.Add(CreateProfileItem(profile));
        _profileItems = _profiles.Count;

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
    }

    private void NewTabButton_Click(object sender, RoutedEventArgs args) => _ = AddDefaultTabAsync();

    private void SettingsButton_Click(object sender, RoutedEventArgs args) => ShowSettings();

    private void Tabs_Loaded(object sender, RoutedEventArgs args)
    {
        if (FindVisualChild<Grid>(Tabs, "TabContainerGrid") is { } container)
            container.ColumnDefinitions[0].MinWidth = 0;
        if (FindVisualChild<ItemsPresenter>(Tabs, "TabsItemsPresenter")?.Header is FrameworkElement header)
            header.Width = 0;
        if (FindVisualChild<ScrollContentPresenter>(Tabs, "ScrollContentPresenter") is { } presenter)
            presenter.Padding = new Thickness(0);
        if (_tabStrip is null) InitializeTabSidebar();
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T element && element.Name == name) return element;
            if (FindVisualChild<T>(child, name) is { } match) return match;
        }
        return null;
    }

    private void AddTab(ShellProfile profile)
    {
        if (_closing) return;
        var tab = new TerminalTab(profile, (Style)WindowRoot.Resources["PaneSplitterStyle"]);
        var item = new TabViewItem
        {
            Header = tab.Title,
            Content = tab,
            Style = (Style)WindowRoot.Resources["PillTabStyle"]
        };
        item.Tag = new TabProgramStatus(item);
        _ = SetShellIconAsync(item, tab);
        tab.TitleChanged += () =>
        {
            item.Header = tab.Title;
            if (ReferenceEquals(Tabs.SelectedItem, item)) UpdateWindowTitle();
            _ = SetShellIconAsync(item, tab);
        };
        tab.CloseRequested += async () => await CloseTabAsync(item);
        Tabs.TabItems.Add(item);
        Tabs.SelectedItem = item;
    }

    private static readonly Dictionary<string, Task<BitmapImage?>> ShellIcons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TabViewItem, string> AppliedIcons = new();

    private static async Task SetShellIconAsync(TabViewItem item, TerminalTab tab)
    {
        var profile = tab.PrimaryProfile;
        var status = (TabProgramStatus)item.Tag;
        if (AppliedIcons.TryGetValue(item, out var applied) && applied == profile.Executable) return;
        AppliedIcons.Remove(item);
        if (!ShellIcons.TryGetValue(profile.Executable, out var load))
        {
            status.SetShellIcon(new FontIconSource { Glyph = "\uE756" });
            ShellIcons[profile.Executable] = load = LoadShellIconAsync(profile.Executable);
        }
        else if (!load.IsCompleted) status.SetShellIcon(new FontIconSource { Glyph = "\uE756" });
        var image = await load;
        if (tab.PrimaryProfile.Id != profile.Id || tab.PrimaryProfile.Executable != profile.Executable) return;
        if (image is null)
        {
            status.SetShellIcon(new FontIconSource { Glyph = "\uE756" });
            return;
        }
        AppliedIcons.AddOrUpdate(item, profile.Executable);
        status.SetShellIcon(new ImageIconSource { ImageSource = image });
    }

    private static async Task<BitmapImage?> LoadShellIconAsync(string executable)
    {
        try
        {
            var icon = await Task.Run(() => ExecutableIcon.Read(executable));
            if (icon is null) return null;
            using var stream = new MemoryStream(icon).AsRandomAccessStream();
            var image = new BitmapImage { DecodePixelWidth = 32 };
            await image.SetSourceAsync(stream);
            return image;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException)
        {
            return null;
        }
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncSidebarSelection();
        var next = (Tabs.SelectedItem as TabViewItem)?.Content as TerminalTab;
        UpdateWindowTitle();
        if (ReferenceEquals(next, _visibleTab)) return;
        _visibleTab?.SetVisible(false);
        _visibleTab = next;
        if (_closing || next is null || next.IsClosing) return;
        next.SetVisible(true);
        next.FocusActivePane();
    }

    // Taskbar and Alt+Tab read the window title, so it follows the selected tab.
    private void UpdateWindowTitle() =>
        Title = (Tabs.SelectedItem as TabViewItem)?.Content is TerminalTab tab ? $"{tab.Title} - AgentTerm" : "AgentTerm";

    private async void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) =>
        await CloseTabAsync(args.Tab);

    private async Task CloseTabAsync(TabViewItem item, bool keyboard = false)
    {
        if (_closing || !item.IsClosable || item.Content is not TerminalTab tab) return;

        item.IsClosable = false;
        item.IsEnabled = false;
        var focusAfterDisable = keyboard ? FocusManager.GetFocusedElement(WindowRoot.XamlRoot) : null;
        await tab.DisposeAsync();
        if (_closing) return;

        var restoreHeaderFocus = keyboard &&
            ReferenceEquals(FocusManager.GetFocusedElement(WindowRoot.XamlRoot), focusAfterDisable);
        var index = Tabs.TabItems.IndexOf(item);
        Tabs.TabItems.Remove(item);
        if (Tabs.TabItems.Count == 0) Close();
        else if (Tabs.SelectedItem is null) Tabs.SelectedIndex = Math.Min(index, Tabs.TabItems.Count - 1);
        if (restoreHeaderFocus && !_closing) FocusSelectedTab();
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
        if (_visibleTab?.ActivePage.IsComposing == true) return;
        if (_closing || Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) return;
        var control = Down(VirtualKey.Control);
        var shift = Down(VirtualKey.Shift);
        var alt = Down(VirtualKey.Menu);
        var firstPress = !e.KeyStatus.WasKeyDown;

        if (alt && !control && !shift && e.Key == VirtualKey.Down && IsTabHeader(e.OriginalSource))
        {
            var header = (FrameworkElement)e.OriginalSource;
            var tabItem = header is SidebarTabItem row ? row.Tab : (TabViewItem)header;
            if (tabItem.Tag is TabProgramStatus status) status.ShowDetails(header);
            e.Handled = true;
        }
        else if (!control && !shift && !alt && e.Key == VirtualKey.Escape && !IsTabHeader(e.OriginalSource) && _visibleTab?.ActivePage.TryLeaveAgentSurface() == true)
        {
            e.Handled = true;
        }
        else if (control && !alt && e.Key == VirtualKey.Tab)
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
                    _ = AddDefaultTabAsync();
                    break;
                case VirtualKey.W when _visibleTab is { } tab:
                    await tab.CloseActivePaneAsync();
                    break;
                case VirtualKey.D:
                    _visibleTab?.SplitDuplicate();
                    break;
            }
        }
        else if (control && !shift && !alt && e.Key == VirtualKey.F4 && e.OriginalSource is SidebarTabItem focusedRow)
        {
            e.Handled = true;
            if (firstPress) await CloseTabAsync(focusedRow.Tab, keyboard: true);
        }
        else if (control && shift && !alt && e.Key == VirtualKey.E)
        {
            e.Handled = true;
            if (firstPress) FocusSelectedTab();
        }
        else if (!control && !shift && !alt && e.Key == VirtualKey.Escape && IsTabHeader(e.OriginalSource))
        {
            e.Handled = true;
            _visibleTab?.FocusActivePane();
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

    private void ShowSettings()
    {
        if (_closing) return;
        if (_settingsWindow is null)
        {
            var settings = new SettingsWindow(this, _profiles);
            settings.Closed += (_, _) =>
            {
                if (ReferenceEquals(_settingsWindow, settings)) _settingsWindow = null;
            };
            _settingsWindow = settings;
        }
        _settingsWindow.ActivateSettings();
    }

    private static bool Down(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private async void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;

        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        _settingsWindow?.Close();
        try
        {
            var tabs = Tabs.TabItems.OfType<TabViewItem>().Select(item => item.Content).OfType<TerminalTab>();
            await Task.WhenAll(tabs.Select(tab => tab.DisposeAsync().AsTask()));
        }
        finally
        {
            _allowClose = true;
            DispatcherQueue.TryEnqueue(Close);
        }
    }
}
