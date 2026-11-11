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
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;

namespace AgentTerm_App;

public sealed partial class MainWindow : Window
{
    private readonly IReadOnlyList<ShellProfile> _profiles = ShellProfile.Discover();
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
        };
        WindowRoot.ActualThemeChanged += (_, _) => ApplyCaptionTheme();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new SizeInt32(1040, 760));
        SystemBackdrop = new MicaBackdrop();
        BuildProfileMenu();
        AddTab(DefaultProfile);
        AppWindow.Closing += Window_Closing;
        Closed += (_, _) => _settingsWindow?.Close();
    }

    private void ApplyCaptionTheme()
    {
        int dark = WindowRoot.ActualTheme == ElementTheme.Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 20, in dark, sizeof(int));

        // Extended captions keep the OS theme unless their button colors are set explicitly.
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

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, in int value, uint size);

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

    private void NewTabButton_Click(object sender, RoutedEventArgs args) => AddTab(DefaultProfile);

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
        if (_visibleTab?.ActivePage.IsComposing == true) return;
        if (_closing || Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) return;
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

        // Keep the window alive until every pane has released its shell and ConPTY.
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
            Close();
        }
    }
}
