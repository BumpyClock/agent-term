using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

internal static class ProgramStatusUiChecks
{
    internal static void Run()
    {
        var tab = new TabViewItem { Header = "PowerShell" };
        var status = new TabProgramStatus(tab);
        tab.Tag = status;
        var shell = new FontIconSource { Glyph = "\uE756" };
        status.SetShellIcon(shell);
        var resources = new ResourceDictionary
        {
            ["SidebarTabStyle"] = new Style { TargetType = typeof(ListViewItem) },
            ["SidebarTabHintStyle"] = new Style { TargetType = typeof(TextBlock) }
        };
        var sidebar = new SidebarTabItem(tab, resources);
        var working = new PaneProgramStatus(1, "PowerShell", new("", ProgramState.Working, null, 30,
            "claude-code", "claude-code", "Build", "Compiling", DateTimeOffset.UtcNow));
        status.Update([working]);
        Check(Tip(tab) == Tip(sidebar) && Tip(tab).Contains("Working · 30%"), "Top and expanded sidebar must expose identical live status details.");
        Check(AutomationProperties.GetHelpText(tab) == "Working · 30%", "Top tab must expose status to assistive technology.");
        Check(AutomationProperties.GetHelpText(sidebar).Contains("Compiling"), "Sidebar must expose status to assistive technology.");
        Check(!ReferenceEquals(tab.IconSource, shell), "Program status must replace the shell icon.");
        sidebar.IsCompact = true;
        Check(Tip(sidebar).StartsWith(Tip(tab), StringComparison.Ordinal) && Tip(sidebar).Contains("Shift+F10") && Tip(sidebar).Contains("Ctrl+F4"), "Compact sidebar must retain full status and tab actions.");
        Check(AutomationProperties.GetHelpText(sidebar).Contains("Shift+F10") && AutomationProperties.GetHelpText(sidebar).Contains("Ctrl+F4"), "Compact actions must remain accessible with status.");
        var blocked = working with { Status = working.Status with { State = ProgramState.Blocked, Kind = "question", Message = "Which target?" } };
        status.Update([blocked]);
        Check(Tip(sidebar).StartsWith(Tip(tab), StringComparison.Ordinal) && Tip(sidebar).Contains("Which target?"), "Status changes must reach an existing compact row.");
        var newShell = new FontIconSource { Glyph = "\uE756" };
        status.SetShellIcon(newShell);
        Check(!ReferenceEquals(tab.IconSource, newShell), "Late shell icon loading must not erase program status.");
        status.Update([]);
        Check(ReferenceEquals(tab.IconSource, newShell), "Clearing the last report must restore the latest shell icon.");
        Check(ToolTipService.GetToolTip(tab) is null && AutomationProperties.GetHelpText(tab) == "", "Clearing reports must clear top tooltip and accessible status.");
        Check(ToolTipService.GetToolTip(sidebar) is ToolTip { Content: StackPanel } && AutomationProperties.GetHelpText(sidebar).Contains("Ctrl+F4"), "Compact sidebar must restore its ordinary title/action hint.");
        status.Update([working]);
        var beforeDetach = Tip(sidebar);
        sidebar.Detach();
        status.Update([blocked]);
        Check(Tip(sidebar) == beforeDetach && Tip(tab).Contains("Which target?"), "Detached rows must stop following live tabs.");
        Console.WriteLine("Native top, expanded sidebar, and compact sidebar status checks passed.");
    }

    internal static async Task CheckDetailsAsync(Window window)
    {
        var tab = new TabViewItem { Header = "Status fixture" };
        var status = new TabProgramStatus(tab);
        var tabs = new TabView();
        tabs.TabItems.Add(tab);
        window.Content = tabs;
        window.Activate();
        var reports = Enumerable.Range(0, 256).Select(index => new PaneProgramStatus(1, "Fixture",
            new($"task{index:D3}", ProgramState.Working, null, null, "fixture", "fixture", $"Task {index}", "Detail " + index, DateTimeOffset.UtcNow))).ToArray();
        status.Update(reports);
        await Task.Delay(200);
        var menu = (MenuFlyout)tab.ContextFlyout;
        menu.ShowAt(tab);
        await Task.Delay(100);
        var entry = (MenuFlyoutItem)menu.Items[0];
        var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(entry);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await Task.Delay(200);
        var popup = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(tab.XamlRoot)
            .SelectMany(popup => Descendants(popup.Child)).OfType<TextBox>().Single();
        Check(popup.Text.Contains("Detail 255"), "The interactive details surface must retain the final report.");
        popup.Focus(FocusState.Programmatic);
        Key(0x11, 0, 0, 0);
        Key(0x23, 0, 0, 0);
        Key(0x23, 0, 2, 0);
        Key(0x11, 0, 2, 0);
        await Task.Delay(200);
        Check(popup.SelectionStart == popup.Text.Length, "Ctrl+End must reach the final report in the focused details flyout.");
        var scroll = Descendants(popup).OfType<ScrollViewer>().First();
        Check(scroll.ScrollableHeight > 0 && scroll.VerticalOffset > 0, "Keyboard navigation must scroll overflowing details into view.");
        scroll.ChangeView(null, 0, null, true);
        await Task.Delay(100);
        scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
        await Task.Delay(100);
        Check(scroll.VerticalOffset >= scroll.ScrollableHeight - 1, "The details scrollbar must reach the final record.");
        Key(0x1B, 0, 0, 0);
        Key(0x1B, 0, 2, 0);
        await Task.Delay(100);
        Console.WriteLine("Interactive status details passed menu invocation, Ctrl+End and final-record scrolling.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void Key(byte key, byte scan, uint flags, nuint extra);

    private static string Tip(DependencyObject element)
    {
        var tip = ToolTipService.GetToolTip(element);
        if (tip is ToolTip wrapper) tip = wrapper.Content;
        return Text(tip);
    }
    private static string Text(object? content) => content switch
    {
        TextBlock text => text.Text,
        StackPanel panel => string.Join("\n", panel.Children.OfType<TextBlock>().Select(text => text.Text).Where(text => text.Length > 0)),
        _ => ""
    };
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
