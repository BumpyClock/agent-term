using System;
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

    // IconSource instances already back the hidden TabViewItem; each row builds its own element from the same source.
    private static IconElement? CreateIcon(IconSource? source) => source switch
    {
        ImageIconSource image => new ImageIcon { Source = image.ImageSource },
        FontIconSource font => new FontIcon { Glyph = font.Glyph, FontSize = 16 },
        _ => null
    };

    // Keyboard requests carry no pointer position; anchor them under the row instead of the list origin.
    private void ShowMenu(UIElement sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        var options = new FlyoutShowOptions { ShowMode = FlyoutShowMode.Standard };
        _menuFromKeyboard = !e.TryGetPosition(this, out var point);
        if (_menuFromKeyboard) options.Placement = FlyoutPlacementMode.RightEdgeAlignedTop;
        else options.Position = point;
        _menu.ShowAt(this, options);
    }

    private void Title_IsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args) => UpdateToolTip();

    // A full-title tooltip only helps when the row hides part or all of the title.
    // Compact rows also hide the close button, so their tooltip points at the context menu.
    private void UpdateToolTip()
    {
        var title = Title;
        const string actions = "Right-click or Shift+F10 for tab actions. Ctrl+F4 closes this tab.";
        AutomationProperties.SetHelpText(this, _compact ? actions : "");
        object? tip = string.IsNullOrEmpty(title) ? null
            : _compact ? new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = title },
                    new TextBlock { Text = actions, Style = _hintStyle, MaxWidth = 260, TextWrapping = TextWrapping.Wrap }
                }
            }
            : _title?.IsTextTrimmed == true ? title : null;
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
        if (_icon is not null) _icon.Content = CreateIcon(Tab.IconSource);
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
