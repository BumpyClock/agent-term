using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentTerm_App;

public sealed partial class MainWindow
{
    private readonly Dictionary<TabViewItem, SidebarTabItem> _sidebarItems = [];
    private TabPosition _tabPosition = TabPosition.Top;
    private FrameworkElement? _tabListView;
    private FrameworkElement? _tabStrip;
    private FrameworkElement? _tabContent;
    private bool _syncingSidebar;

    private void InitializeTabSidebar()
    {
        _tabListView = FindVisualChild<FrameworkElement>(Tabs, "TabListView");
        _tabStrip = FindVisualChild<Grid>(Tabs, "TabContainerGrid");
        _tabContent = FindVisualChild<ContentPresenter>(Tabs, "TabContentPresenter");
        if (_tabStrip is not null) _tabStrip.SizeChanged += (_, _) => UpdateSidebarLayout();
        WindowRoot.SizeChanged += (_, _) => UpdateSidebarLayout();
        Tabs.TabItemsChanged += (_, _) => SyncSidebar();
        TerminalPreferences.Current.Changed += Preferences_Changed;
        Closed += (_, _) => TerminalPreferences.Current.Changed -= Preferences_Changed;
        ApplyTabPosition(TerminalPreferences.Current.TabPosition);
    }

    private void Preferences_Changed() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closing || TerminalPreferences.Current.TabPosition == _tabPosition) return;
        ApplyTabPosition(TerminalPreferences.Current.TabPosition);
        // Collapsing a tab list can drop this window's logical focus onto the title-bar buttons.
        // Restore it to the terminal without activating this window over Settings.
        _visibleTab?.FocusActivePane();
    });

    // Both positions keep the same TabView, footer, and title-bar drag region; only the tab list moves.
    private void ApplyTabPosition(TabPosition position)
    {
        _tabPosition = position;
        var left = position == TabPosition.Left;
        if (_tabListView is not null) _tabListView.Visibility = left ? Visibility.Collapsed : Visibility.Visible;
        if (_tabStrip is not null) _tabStrip.MinHeight = left ? TabStripHeight : 0;
        // Centers the new-tab button over the sidebar icon column.
        TabStripActions.Margin = new Thickness(left ? 12 : 4, 0, 0, 0);
        TabList.Visibility = WindowTitleText.Visibility = left ? Visibility.Visible : Visibility.Collapsed;
        SyncSidebar();
        UpdateSidebarLayout();
        if (left)
        {
            // The list was collapsed until now; ScrollIntoView needs a measured list to reach rows past the first screen.
            TabList.UpdateLayout();
            SyncSidebarSelection(reveal: true);
        }
        _gitHubFailedWidth = 0;
        UpdateGitHubFit();
    }

    private static double TabStripHeight =>
        (double)Application.Current.Resources["TabViewItemMinHeight"] + 8;

    private void UpdateSidebarLayout()
    {
        var left = _tabPosition == TabPosition.Left;
        var width = left ? TabSidebarLayout.Width(WindowRoot.ActualWidth) : 0;
        if (_tabContent is not null) _tabContent.Margin = new Thickness(width, 0, 0, 0);
        if (!left) return;
        TabList.Width = width;
        TabList.Margin = new Thickness(0, (_tabStrip?.ActualHeight ?? TabStripHeight) + 4, 0, 4);
        var compact = TabSidebarLayout.IsCompact(WindowRoot.ActualWidth);
        foreach (var item in _sidebarItems.Values) item.IsCompact = compact;
    }

    private void SyncSidebar()
    {
        if (_syncingSidebar) return;
        _syncingSidebar = true;
        try
        {
            if (_tabPosition != TabPosition.Left)
            {
                TabList.Items.Clear();
                foreach (var row in _sidebarItems.Values) row.Detach();
                _sidebarItems.Clear();
                return;
            }

            var tabs = Tabs.TabItems.OfType<TabViewItem>().ToList();
            foreach (var stale in _sidebarItems.Keys.Except(tabs).ToList())
            {
                TabList.Items.Remove(_sidebarItems[stale]);
                _sidebarItems[stale].Detach();
                _sidebarItems.Remove(stale);
            }

            var compact = TabSidebarLayout.IsCompact(WindowRoot.ActualWidth);
            var desired = new List<object>(tabs.Count);
            foreach (var tab in tabs)
            {
                if (!_sidebarItems.TryGetValue(tab, out var row))
                {
                    row = new SidebarTabItem(tab, WindowRoot.Resources) { IsCompact = compact };
                    row.CloseRequested += (item, keyboard) => _ = CloseTabAsync(item.Tab, keyboard);
                    _sidebarItems.Add(tab, row);
                    TabList.Items.Add(row);
                }
                desired.Add(row);
            }
            TabSidebarLayout.ReorderAround(TabList.Items, desired, TabList.SelectedItem);
            SyncSidebarSelection();
        }
        finally
        {
            _syncingSidebar = false;
        }
    }

    // Title updates only rename the title bar; they must not scroll a sidebar the user is browsing.
    private void UpdateWindowTitle() => WindowTitleText.Text = (Tabs.SelectedItem as TabViewItem)?.Header as string ?? "";

    // The selected row is revealed only when selection moves to a different row or the caller asks
    // (entering Left), so closes, reorders, and title updates leave the scroll position alone.
    private void SyncSidebarSelection(bool reveal = false)
    {
        UpdateWindowTitle();
        if (_tabPosition != TabPosition.Left) return;
        var row = Tabs.SelectedItem is TabViewItem tab && _sidebarItems.TryGetValue(tab, out var match) ? match : null;
        if (!ReferenceEquals(TabList.SelectedItem, row))
        {
            reveal = true;
            var syncing = _syncingSidebar;
            _syncingSidebar = true;
            TabList.SelectedItem = row;
            _syncingSidebar = syncing;
        }
        if (reveal && row is not null) TabList.ScrollIntoView(row);
    }

    private void FocusSelectedTab()
    {
        Control? header = _tabPosition == TabPosition.Left
            ? TabList.SelectedItem as SidebarTabItem
            : Tabs.SelectedItem as TabViewItem;
        if (header is null) return;
        if (header is SidebarTabItem row) TabList.ScrollIntoView(row);
        header.Focus(FocusState.Keyboard);
    }

    private static bool IsTabHeader(object source) => source is SidebarTabItem or TabViewItem;

    private void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSidebar) return;
        if (TabList.SelectedItem is SidebarTabItem row) Tabs.SelectedItem = row.Tab;
        else SyncSidebarSelection();
    }

    private void TabList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SidebarTabItem row) return;
        if (ReferenceEquals(Tabs.SelectedItem, row.Tab)) _visibleTab?.FocusActivePane();
        else Tabs.SelectedItem = row.Tab;
    }

    private void TabList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        var desired = TabList.Items.OfType<SidebarTabItem>().Select(row => (object)row.Tab).ToList();
        if (desired.Count != Tabs.TabItems.Count) return;
        _syncingSidebar = true;
        try { TabSidebarLayout.ReorderAround(Tabs.TabItems, desired, Tabs.SelectedItem); }
        finally { _syncingSidebar = false; }
        SyncSidebar();
    }
}
