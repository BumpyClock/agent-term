using System;
using System.Linq;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace AgentTerm_App;

// A sidebar row mirrors one TabViewItem; the TabView stays the only owner of tab order, selection, and content.
internal sealed class SidebarTabItem : ListViewItem
{
    private Button? _closeButton;
    private ContentPresenter? _icon;
    private TextBlock? _title;
    private readonly MenuFlyout _menu;
    private readonly (DependencyProperty Property, long Token)[] _callbacks;
    private readonly Style _hintStyle;
    private bool _compact;
    private bool _menuFromKeyboard;

    internal SidebarTabItem(TabViewItem tab, ResourceDictionary resources)
    {
        Tab = tab;
        Style = (Style)resources["SidebarTabStyle"];
        _hintStyle = (Style)resources["SidebarTabHintStyle"];
        _callbacks =
        [
            (TabProgramStatus.DetailsProperty, tab.RegisterPropertyChangedCallback(TabProgramStatus.DetailsProperty, (_, _) => Refresh())),
            (TabViewItem.HeaderProperty, tab.RegisterPropertyChangedCallback(TabViewItem.HeaderProperty, (_, _) => Refresh())),
            (TabViewItem.IconSourceProperty, tab.RegisterPropertyChangedCallback(TabViewItem.IconSourceProperty, (_, _) => Refresh())),
            (IsEnabledProperty, tab.RegisterPropertyChangedCallback(IsEnabledProperty, (_, _) => Refresh()))
        ];
        var close = new MenuFlyoutItem
        {
            Text = "Close tab",
            Icon = new FontIcon { Glyph = "" },
            KeyboardAcceleratorTextOverride = "Ctrl+F4"
        };
        close.Click += (_, _) => CloseRequested?.Invoke(this, _menuFromKeyboard);
        _menu = new MenuFlyout { Items = { close } };
        if (tab.Tag is TabProgramStatus status)
        {
            status.Attach(this);
            _menu.Items.Add(status.CreateDetailsMenuItem(this));
        }
        SizeChanged += (_, _) => UpdateToolTip();
        ContextRequested += ShowMenu;
        Refresh();
    }

    internal TabViewItem Tab { get; }
    internal string Title => Tab.Header as string ?? "";
    // The flag is true when the request came from the keyboard and focus should return to the list.
    internal event Action<SidebarTabItem, bool>? CloseRequested;

    // Rows are rebuilt on every switch to Left; detached rows must stop following their tab.
    internal void Detach()
    {
        if (Tab.Tag is TabProgramStatus status) status.Detach(this);
        foreach (var (property, token) in _callbacks) Tab.UnregisterPropertyChangedCallback(property, token);
    }

    internal bool IsCompact
    {
        get => _compact;
        set
        {
            if (_compact == value) return;
            _compact = value;
            VisualStateManager.GoToState(this, value ? "Compact" : "Expanded", false);
            UpdateToolTip();
        }
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_closeButton is not null) _closeButton.Click -= CloseButton_Click;
        _closeButton = GetTemplateChild("CloseButton") as Button;
        _icon = GetTemplateChild("IconElement") as ContentPresenter;
        if (_title is not null) _title.IsTextTrimmedChanged -= Title_IsTextTrimmedChanged;
        _title = GetTemplateChild("TitleText") as TextBlock;
        if (_title is not null) _title.IsTextTrimmedChanged += Title_IsTextTrimmedChanged;
        if (_closeButton is not null) _closeButton.Click += CloseButton_Click;
        VisualStateManager.GoToState(this, _compact ? "Compact" : "Expanded", false);
        Refresh();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SidebarTabItemAutomationPeer(this);

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
        {
            e.Handled = true;
            CloseRequested?.Invoke(this, false);
            return;
        }
        base.OnPointerPressed(e);
    }

    // Keep the target visible for both pointer and keyboard requests.
    private void ShowMenu(UIElement sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        if (Tab.Tag is TabProgramStatus status) status.Hide();
        if (ToolTipService.GetToolTip(this) is ToolTip tooltip) tooltip.IsOpen = false;
        var list = ItemsControl.ItemsControlFromItemContainer(this);
        var duplicateTitle = list?.Items.OfType<SidebarTabItem>().Count(row => row.Title == Title) > 1;
        var target = duplicateTitle ? $"{Title} (tab {list!.IndexFromContainer(this) + 1})" : Title;
        ((MenuFlyoutItem)_menu.Items[0]).Text = $"Close {target}";
        var options = new FlyoutShowOptions
        {
            ShowMode = FlyoutShowMode.Standard,
            Placement = FlyoutPlacementMode.RightEdgeAlignedTop
        };
        _menuFromKeyboard = !e.TryGetPosition(this, out _);
        _menu.ShowAt(this, options);
    }

    private void Title_IsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args) => UpdateToolTip();

    private const string TabActions = "Right-click for the close menu. With this tab focused, Shift+F10 opens the menu and Ctrl+F4 closes the tab.";

    private object? ToolTipContent(string details)
    {
        if (details.Length > 0) return null;
        if (string.IsNullOrEmpty(Title)) return null;
        if (!_compact) return _title?.IsTextTrimmed == true ? Title : null;
        return new StackPanel
        {
            Children =
            {
                new TextBlock { Text = Title },
                new TextBlock { Text = TabActions, Style = _hintStyle, MaxWidth = 260, TextWrapping = TextWrapping.Wrap }
            }
        };
    }

    private void UpdateToolTip()
    {
        var details = (string)Tab.GetValue(TabProgramStatus.DetailsProperty);
        AutomationProperties.SetHelpText(this, details.Length == 0 ? TabActions : details + "\nAlt+Down opens program status. Escape dismisses it.\n" + TabActions);
        var tip = ToolTipContent(details);
        ToolTipService.SetToolTip(this, tip is null ? null : new ToolTip
        {
            Content = tip,
            Placement = PlacementMode.Right,
            PlacementTarget = this,
            PlacementRect = new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight)
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, false);

    private void Refresh()
    {
        var title = Title;
        if (_title is not null) _title.Text = title;
        IsEnabled = Tab.IsEnabled;
        AutomationProperties.SetName(this, title);
        UpdateToolTip();
        if (_icon is not null && _icon.Content is not ProgramStatusIcon) _icon.Content = new ProgramStatusIcon { Tab = Tab };
        if (_closeButton is not null)
        {
            AutomationProperties.SetName(_closeButton, $"Close {title}");
            ToolTipService.SetToolTip(_closeButton, "Close tab (Ctrl+F4)");
        }
    }
}

// The sidebar is a vertical tab strip; report tab roles like TabView so Top and Left read the same to assistive technology.
internal sealed partial class SidebarTabItemAutomationPeer(SidebarTabItem owner) : ListViewItemAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TabItem;
}

internal sealed partial class SidebarTabList : ListView
{
    protected override AutomationPeer OnCreateAutomationPeer() => new SidebarTabListAutomationPeer(this);
}

internal sealed partial class SidebarTabListAutomationPeer(SidebarTabList owner) : ListViewAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tab;
}
