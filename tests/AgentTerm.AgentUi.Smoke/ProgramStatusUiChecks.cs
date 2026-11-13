using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

internal static class ProgramStatusUiChecks
{
    internal static void Run()
    {
        var orphanCard = new ProgramStatusCard();
        var firstOrphan = new PaneProgramStatus(1, "PowerShell", new("build/test", ProgramState.Done, null, null, null, null, null, null, DateTimeOffset.Now));
        var secondOrphan = firstOrphan with { Status = firstOrphan.Status with { Id = "deploy/test" } };
        orphanCard.Update("PowerShell", [firstOrphan, secondOrphan], false);
        var identities = Descendants(orphanCard).OfType<TextBlock>().Where(text => text.Name == "Identity").Select(text => text.Text).ToArray();
        Check(identities.Any(text => text.StartsWith("build/test · ")) && identities.Any(text => text.StartsWith("deploy/test · ")), "Orphan nested reports with equal leaf names must retain distinct full identities.");
        var tab = new TabViewItem { Header = "PowerShell" };
        var status = new TabProgramStatus(tab);
        tab.Tag = status;
        Check(tab.ContextFlyout is null, "Top tabs must not offer a Program status context-menu action.");
        var shell = new FontIconSource { Glyph = "\uE756" };
        status.SetShellIcon(shell);
        var resources = new ResourceDictionary
        {
            ["SidebarTabStyle"] = new Style { TargetType = typeof(ListViewItem) },
            ["SidebarTabHintStyle"] = new Style { TargetType = typeof(TextBlock) }
        };
        var sidebar = new SidebarTabItem(tab, resources);
        var working = Report("", ProgramState.Working, "Build", "Compiling", progress: 30);
        status.Update([working]);
        Check(ToolTipService.GetToolTip(tab) is ToolTip { IsEnabled: false } && ToolTipService.GetToolTip(sidebar) is null, "Status must have one rich surface, without a competing basic tooltip.");
        Check(AutomationProperties.GetHelpText(tab).Contains("Working · 30%") && AutomationProperties.GetHelpText(sidebar).Contains("Compiling"), "Both layouts must expose complete status to assistive technology.");
        Check(ReferenceEquals(tab.IconSource, shell), "Unresolved programs must retain shell identity instead of a generic status circle.");
        var pi = Report("", ProgramState.Idle, "pi", "Waiting for input");
        status.Update([pi]);
        Check(tab.IconSource is FontIconSource { Glyph: "π", FontFamily.Source: "Segoe UI" }, "Pi must have built-in identity even without a PATH executable icon.");
        var piIcon = tab.IconSource;
        foreach (var state in new[] { ProgramState.Working, ProgramState.Done, ProgramState.Idle })
        {
            status.Update([pi with { Status = pi.Status with { State = state } }]);
            Check(ReferenceEquals(tab.IconSource, piIcon), "Pi identity must survive activity and completion changes while running.");
        }
        foreach (var agent in new[] { "claude", "Claude-Code", "codex", "gemini", "copilot", "cursor-agent", "opencode", "qwen", "cline" })
        {
            var report = Report("", ProgramState.Idle, agent, "Waiting for input");
            status.Update([report]);
            var mark = tab.IconSource;
            Check(mark is PathIconSource { Data: not null }, $"{agent} must have a built-in mark even without a PATH executable icon.");
            status.Update([report with { Status = report.Status with { State = ProgramState.Working } }]);
            Check(ReferenceEquals(tab.IconSource, mark), $"{agent} identity must survive activity changes while running.");
        }
        status.Update([pi]);
        var retained = pi with { Status = pi.Status with { State = ProgramState.Done, IsActive = false } };
        status.Update([retained]);
        Check(ReferenceEquals(tab.IconSource, shell) && AutomationProperties.GetHelpText(tab).Contains("Done"), "Shell readiness must restore shell identity without losing completion details.");
        var newShell = new FontIconSource { Glyph = "S" };
        status.SetShellIcon(newShell);
        Check(ReferenceEquals(tab.IconSource, newShell), "Shell identity must follow shell changes with retained reports.");
        status.SetShellIcon(shell);
        sidebar.IsCompact = true;
        Check(AutomationProperties.GetHelpText(sidebar).Contains("Alt+Down") && AutomationProperties.GetHelpText(sidebar).Contains("Ctrl+F4"), "Compact actions must remain accessible with status.");
        var blocked = working with { Status = working.Status with { State = ProgramState.Blocked, Kind = "question", Message = "Which target?" } };
        status.Update([blocked]);
        Check(AutomationProperties.GetHelpText(sidebar).Contains("Which target?"), "Existing compact rows must follow status changes.");
        status.Update([]);
        Check(ReferenceEquals(tab.IconSource, shell), "Clearing reports must restore the shell icon.");
        Check(AutomationProperties.GetHelpText(tab) == "", "Clearing reports must clear accessible status.");
        Check(ToolTipService.GetToolTip(sidebar) is ToolTip { Content: StackPanel }, "Compact sidebar must restore its ordinary title/action hint.");
        status.Update([working]);
        var beforeDetach = AutomationProperties.GetHelpText(sidebar);
        sidebar.Detach();
        status.Update([blocked]);
        Check(AutomationProperties.GetHelpText(sidebar) == beforeDetach, "Detached rows must stop following live tabs.");
        Console.WriteLine("Top, expanded and compact sidebar status contracts passed.");
    }

    internal static async Task CheckDetailsAsync(Window window)
    {
        var tab = new TabViewItem { Header = "PowerShell" };
        var status = new TabProgramStatus(tab);
        tab.Tag = status;
        var input = new TextBox { Text = "Terminal focus fixture", Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Top };
        var terminalEscapeCount = 0;
        input.PreviewKeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) terminalEscapeCount++; };
        tab.Content = input;
        var tabs = new TabView();
        tabs.TabItems.Add(tab);
        var root = new Grid { RequestedTheme = ElementTheme.Dark, Children = { tabs } };
        window.Content = root;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 850));
        window.Activate();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        ShowWindow(hwnd, 9);
        Foreground(hwnd);
        RequireForeground(window);

        await Task.Delay(300);
        input.Focus(FocusState.Programmatic);
        Move(window, input, 100, 15);
        await Task.Delay(100);
        Move(window, tab, 70, 16);
        await Task.Delay(600);
        Check(OpenCard(tab) is null, "Tabs without reports must not show an empty status card.");
        status.Update([Report("", ProgramState.Done, "pi", "Quiver Git Pull")]);
        await Task.Delay(800);
        var card = OpenCard(tab);
        if (card is null) await CaptureAsync(root, "hover-failure");
        Check(card is not null, "Hover must open the rich card without a context menu.");
        Check(ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(tab.XamlRoot), input), "Hover must preserve terminal focus.");
        await CaptureAsync(card!, "single-dark");
        Move(window, card!, 70, 60);
        await Task.Delay(600);
        Check(OpenCard(tab) == card, "Moving from tab to card must keep it open.");
        status.Update([Report("", ProgramState.Working, "pi", "Pulling changes", progress: 35)]);
        Check(OpenCard(tab) == card && Text(card!).Contains("Pulling changes"), "Live reports must update the open card in place.");
        Press(window, 0x1B);
        await Task.Delay(150);
        Check(OpenCard(tab) is null && ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(tab.XamlRoot), input), "Escape must dismiss hover without stealing terminal focus.");
        Check(terminalEscapeCount == 1, "Hover dismissal must let Escape reach the terminal input.");
        Move(window, input, 500, 180);
        await Task.Delay(100);
        Move(window, tab, 70, 16);
        await Task.Delay(700);
        card = OpenCard(tab);
        Check(card is not null, "Hover must reopen after a fresh pointer entry.");
        Move(window, input, 500, 180);
        await Task.Delay(600);
        Check(OpenCard(tab) is null, "Leaving both tab and card must dismiss hover.");
        Console.WriteLine("Pointer hover, terminal focus preservation, pointer bridge, live update and leave dismissal passed.");

        status.Update([Report("", ProgramState.Working, "PowerShell", "Validating terminal rendering")]);
        Move(window, tab, 70, 16);
        await Task.Delay(700);
        card = OpenCard(tab)!;
        var programName = Descendants(card).OfType<TextBlock>().Single(block => block.Name == "Name" && block.Text == "PowerShell");
        Check(programName.TransformToVisual(card).TransformPoint(new(0, 0)).Y < 12, "Matching single-report titles must collapse the heading without an empty gap.");
        var presenter = VisualTreeHelper.GetOpenPopupsForXamlRoot(tab.XamlRoot).SelectMany(popup => Descendants(popup.Child)).OfType<FlyoutPresenter>().Single();
        Check(new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast || presenter.Background is AcrylicBrush, "The status card must use the native acrylic brush.");
        var pin = Descendants(card).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Keep program status open");
        Move(window, pin, 30, 12);
        Inject(window, PointerInput(0x0002), PointerInput(0x0004));
        await Task.Delay(300);
        Move(window, input, 500, 180);
        await Task.Delay(600);
        Check(OpenCard(tab) == card && pin.Visibility == Visibility.Collapsed, "Clicking Keep open must pin the same card after the pointer leaves.");
        Check(ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(tab.XamlRoot), card.Scroll), "Keep open must focus reports for keyboard use.");
        await CaptureAsync(card, "pinned-pointer");
        Press(window, 0x1B);
        await Task.Delay(200);
        Check(OpenCard(tab) is null, "Escape must dismiss a pointer-pinned card.");
        Console.WriteLine("Keep open pointer click, persistence after pointer exit, keyboard focus and Escape passed.");

        tab.Focus(FocusState.Keyboard);
        Press(window, 0x12, 0x28);
        await Task.Delay(300);
        card = OpenCard(tab);
        Check(card is not null, "Alt+Down must open status directly from a focused tab.");
        Check(ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(tab.XamlRoot), card!.Scroll), "Keyboard opening must focus the report scroller.");
        await CaptureAsync(card, "pinned-keyboard");
        var reports = Enumerable.Range(0, 256).Select(index => Report($"task{index:D3}", ProgramState.Working, $"Task {index}", "Detail " + index)).ToArray();
        status.Update(reports);
        await Task.Delay(300);
        Check(Text(card).Contains("Detail 255"), "Every report must remain available.");
        Press(window, 0x11, 0x23);
        await Task.Delay(200);
        Check(card.Scroll.VerticalOffset >= card.Scroll.ScrollableHeight - 1 && card.Scroll.VerticalOffset > 0, "Ctrl+End must reach the last report.");
        var offset = card.Scroll.VerticalOffset;
        status.Update(reports.Select(r => r with { Status = r.Status with { Progress = 40 } }).ToArray());
        await Task.Delay(100);
        Check(Math.Abs(card.Scroll.VerticalOffset - offset) < 1, "Live updates must preserve reading position.");
        var selectedMessage = Descendants(card).OfType<TextBlock>().Single(t => t.Text == "Detail 255");
        selectedMessage.Select(selectedMessage.ContentStart, selectedMessage.ContentEnd);
        var selectedBefore = selectedMessage.SelectedText;
        var beforeTop = selectedMessage.TransformToVisual(card.Scroll).TransformPoint(new(0, 0)).Y;
        var inserted = Report("aaa", ProgramState.Blocked, "New approval", "May I continue?", pane: 2, kind: "permission");
        status.Update([inserted, .. reports]);
        await Task.Delay(150);
        Check(ReferenceEquals(selectedMessage, Descendants(card).OfType<TextBlock>().Single(t => t.Text == "Detail 255")) && selectedMessage.SelectedText == selectedBefore, "Inserting an urgent pane must preserve selected text and its element.");
        Check(Math.Abs(selectedMessage.TransformToVisual(card.Scroll).TransformPoint(new(0, 0)).Y - beforeTop) < 2, "Structural insertion must preserve the visible report.");
        status.Update([.. reports, inserted with { Status = inserted.Status with { State = ProgramState.Done } }]);
        await Task.Delay(150);
        Check(selectedMessage.SelectedText == selectedBefore && Math.Abs(selectedMessage.TransformToVisual(card.Scroll).TransformPoint(new(0, 0)).Y - beforeTop) < 2, "An urgency reorder must preserve text selection and reading position.");
        status.Update(reports);
        await Task.Delay(150);
        Check(selectedMessage.SelectedText == selectedBefore, "Removing another pane must retain text selection.");
        selectedMessage.Focus(FocusState.Keyboard);
        await Task.Delay(300);
        Check(ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(tab.XamlRoot), selectedMessage), "Selectable report text must accept keyboard focus.");
        var readingOffset = card.Scroll.VerticalOffset;
        Press(window, 0x10, 0x24);
        await Task.Delay(100);
        Check(Math.Abs(card.Scroll.VerticalOffset - readingOffset) < 1, "Shift+Home in selectable text must not jump the report scroller.");
        Console.WriteLine("Structural insertion/removal preserve selected text, row identity and the visible report.");
        await CheckReadingUpdatesAsync(status, card);
        Press(window, 0x1B);
        await Task.Delay(200);
        Check(OpenCard(tab) is null, "Escape must dismiss keyboard status.");
        Check(ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(tab.XamlRoot), tab), "Keyboard dismissal must restore tab focus.");
        Console.WriteLine("Alt+Down, focused scrolling, 256 reports, Ctrl+End, live scroll preservation, Escape and focus return passed.");

        var mixed = new[] {
            Report("", ProgramState.Working, "claude-code", "Updating terminal rendering", pane: 1, progress: 62),
            Report("build", ProgramState.Done, "Build", "Release build passed", pane: 1),
            Report("build/tests", ProgramState.Working, "Smoke tests", "Checking live status and keyboard navigation", pane: 1, progress: 80),
            Report("", ProgramState.Blocked, "codex", "Approve running the integration tests?", pane: 2, kind: "permission")
        };
        status.Update(mixed);
        status.ShowDetails(tab);
        await Task.Delay(300);
        card = OpenCard(tab)!;
        Check(Text(card).IndexOf("Pane 2", StringComparison.Ordinal) < Text(card).IndexOf("Pane 1", StringComparison.Ordinal), "The urgent pane must lead.");
        var accessibleNames = AccessibleNames(Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(card.Scroll)).ToArray();
        var approvalIndex = Array.IndexOf(accessibleNames, "Approve running the integration tests?");
        var buildIndex = Array.IndexOf(accessibleNames, "Release build passed");
        var testIndex = Array.IndexOf(accessibleNames, "Checking live status and keyboard navigation");
        Check(approvalIndex >= 0 && buildIndex > approvalIndex && testIndex > buildIndex, "The accessibility tree must expose urgent and nested reports in visual reading order.");
        Check(accessibleNames.Any(name => name.Contains("Approval needed")) && accessibleNames.Any(name => name.Contains("Working · 62%")), "Accessible report text must include state and percentage.");
        Console.WriteLine("Native accessibility tree exposes urgent-first report order, nested text, state and percentage.");
        await CaptureAsync(card, "multiple-dark");
        Console.WriteLine($"Native multi-report capture. High contrast={new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast}.");
        status.Hide();
        await Task.Delay(150);
        root.RequestedTheme = ElementTheme.Light;
        status.ShowDetails(tab);
        await Task.Delay(300);
        await CaptureAsync(OpenCard(tab)!, "multiple-light");
        status.Hide();
        await Task.Delay(150);
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(520, 650));
        status.Update([Report("", ProgramState.Error, "Build with a long descriptive program title", new string('x', 300) + " final message")]);
        status.ShowDetails(tab);
        await Task.Delay(300);
        card = OpenCard(tab)!;
        Check(card.ActualWidth <= tab.XamlRoot.Size.Width && card.Scroll.ExtentWidth <= card.Scroll.ViewportWidth + 1, "Narrow cards must wrap without horizontal overflow.");
        await CaptureAsync(card, "long-light");
        status.Update([]);
        await Task.Delay(150);
        Check(OpenCard(tab) is null, "Clearing the final report must close the card.");
        Console.WriteLine("Urgent pane ordering, nested content, dark/light native captures, narrow long text and clearing passed.");
        await CheckSidebarHoverAsync(window);
    }

    private static async Task CheckSidebarHoverAsync(Window window)
    {
        var tab = new TabViewItem { Header = "PowerShell" };
        var status = new TabProgramStatus(tab);
        tab.Tag = status;
        var resources = new ResourceDictionary
        {
            ["SidebarTabStyle"] = new Style { TargetType = typeof(ListViewItem) },
            ["SidebarTabHintStyle"] = new Style { TargetType = typeof(TextBlock) }
        };
        var sidebar = new SidebarTabItem(tab, resources) { Content = tab.Header, Width = 220, Height = 40 };
        var list = new ListView { Width = 220, VerticalAlignment = VerticalAlignment.Top, Items = { sidebar } };
        var input = new TextBox { Text = "Sidebar terminal focus fixture", Margin = new Thickness(24) };
        var root = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(220) }, new ColumnDefinition() }, Children = { list, input } };
        Grid.SetColumn(input, 1);
        window.Content = root;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 850));
        await Task.Delay(300);
        input.Focus(FocusState.Programmatic);
        foreach (var compact in new[] { false, true })
        {
            sidebar.IsCompact = compact;
            sidebar.Width = compact ? 48 : 220;
            Move(window, input, 100, 50);
            await Task.Delay(100);
            Move(window, sidebar, 20, 20);
            await Task.Delay(600);
            Check(OpenCard(sidebar) is null, "An empty sidebar tab must not show status.");
            status.Update([Report("", ProgramState.Done, "Build", "Sidebar hover report")]);
            await Task.Delay(800);
            var card = OpenCard(sidebar);
            Check(card is not null && Text(card).Contains("Sidebar hover report"), "New reports must automatically open for an already-hovered sidebar tab in both widths.");
            Check(ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(sidebar.XamlRoot), input), "Sidebar hover must preserve terminal focus.");
            Move(window, input, 100, 50);
            await Task.Delay(600);
            Check(OpenCard(sidebar) is null, "Leaving a sidebar tab must dismiss its status card.");
            status.Update([]);
        }
        sidebar.IsCompact = false;
        sidebar.Width = 220;
        status.Update([Report("", ProgramState.Done, "Build", "Context menu fixture")]);
        Move(window, sidebar, 20, 20);
        Inject(window, PointerInput(0x0008), PointerInput(0x0010));
        await Task.Delay(300);
        var actions = VisualTreeHelper.GetOpenPopupsForXamlRoot(sidebar.XamlRoot)
            .SelectMany(popup => Descendants(popup.Child)).OfType<MenuFlyoutItem>().Select(action => action.Text).ToArray();
        Check(actions.Length == 1 && actions[0] == "Close PowerShell", "Sidebar context menus must retain Close without a Program status action.");
        Press(window, 0x1B);
        sidebar.Detach();
        Console.WriteLine("Expanded and compact sidebar hover, reports arriving under the pointer, focus preservation, dismissal and close-only context menu passed.");
    }

    internal static async Task CheckIconsAsync(Window window)
    {
        var tab = new TabViewItem { Header = "Command Prompt" };
        var status = new TabProgramStatus(tab);
        tab.Tag = status;
        var icon = new ProgramStatusIcon { Tab = tab, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var canvas = new Grid { Width = 160, Height = 48, Padding = new Thickness(12), RequestedTheme = ElementTheme.Dark, Background = new SolidColorBrush(Microsoft.UI.Colors.Black), Children = { icon } };
        window.Content = canvas;
        var shell = new FontIconSource { Glyph = "\uE756" };
        status.SetShellIcon(shell);
        var working = Report("", ProgramState.Working, "cmd", "Icon fixture");
        status.Update([working]);
        for (var attempt = 0; attempt < 30 && tab.IconSource is not ImageIconSource; attempt++) await Task.Delay(100);
        Check(tab.IconSource is ImageIconSource, "A reported local executable must resolve its actual icon.");
        var moving = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled && !new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        var frames = await Program.SampleAsync(canvas, 6);
        Check(moving ? frames > 1 : frames == 1, "The tab activity indicator must animate only when Windows permits motion.");
        await CaptureAsync(canvas, "icon-working");
        status.Update([working with { Status = working.Status with { Progress = 62 } }]);
        await Task.Delay(300);
        Check(!Descendants(icon).OfType<ProgressRing>().Any(), "Working tabs must use the dot sweep instead of a progress ring.");
        Check(AutomationProperties.GetHelpText(tab).Contains("Working · 62%"), "Reported progress must remain in accessible status details.");
        await CaptureAsync(canvas, "icon-progress");
        var executableIcon = tab.IconSource;
        status.Update([working with { Status = working.Status with { State = ProgramState.Done } }]);
        Check(ReferenceEquals(tab.IconSource, executableIcon), "Completed work must retain executable identity while the program is running.");
        Check(await Program.SampleAsync(canvas, 3) == 1, "Completed tabs must stop animating.");
        await CaptureAsync(canvas, "icon-done");
        status.Update([working with { Status = working.Status with { State = ProgramState.Done, IsActive = false } }]);
        Check(ReferenceEquals(tab.IconSource, shell), "Returning to the shell must restore its icon even with retained outcomes.");
        await CaptureAsync(canvas, "icon-shell");
        var pi = Report("", ProgramState.Idle, "pi", "Ready");
        status.Update([pi]);
        await Task.Delay(100);
        Check(Descendants(icon).OfType<FontIcon>().Single().Glyph == "π", "Pi identity must render as pi, not a status circle or missing font glyph.");
        await CaptureAsync(canvas, "icon-pi-idle");
        status.Update([pi with { Status = pi.Status with { State = ProgramState.Working } }]);
        Check(Descendants(icon).OfType<ContentPresenter>().Single(presenter => presenter.Content is FontIcon { Glyph: "π" }).Visibility == (moving ? Visibility.Collapsed : Visibility.Visible), "Pi must use the loader while animating, or keep its normal icon with motion disabled.");
        await CaptureAsync(canvas, "icon-pi-working");
        status.Update([pi with { Status = pi.Status with { IsActive = false } }]);
        Check(ReferenceEquals(tab.IconSource, shell), "Exiting idle Pi must restore shell identity.");
        var claude = Report("", ProgramState.Idle, "claude", "Ready");
        status.Update([claude]);
        await Task.Delay(100);
        var claudeIcon = Descendants(icon).OfType<PathIcon>().Single();
        Check(claudeIcon.Data is not null && claudeIcon.ActualWidth > 0, "Claude identity must render its built-in mark.");
        var highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        var claudeBrand = Windows.UI.Color.FromArgb(255, 0xD9, 0x77, 0x57);
        Check(highContrast ? claudeIcon.Foreground is not SolidColorBrush { Color: var c } || c != claudeBrand : claudeIcon.Foreground is SolidColorBrush { Color: var b } && b == claudeBrand,
            "Claude must render in its brand color, or the system foreground in high contrast.");
        await CaptureAsync(canvas, "icon-claude-idle");
        status.Update([claude with { Status = claude.Status with { State = ProgramState.Working } }]);
        await Task.Delay(100);
        Check(Descendants(icon).OfType<ProgramActivity>().Single().IsAnimating == moving, "Claude must use the loader only while motion is enabled.");
        await CaptureAsync(canvas, "icon-claude-working");
        var sidebarIcon = new ProgramStatusIcon { Tab = tab };
        canvas.Children.Add(sidebarIcon);
        await Task.Delay(100);
        Check(Descendants(sidebarIcon).OfType<ProgramActivity>().Single().IsAnimating == moving, "Both views of a working tab must follow the same motion preference.");
        status.Update([claude]);
        await Task.Delay(100);
        var firstMark = Descendants(icon).OfType<PathIcon>().Single();
        var secondMark = Descendants(sidebarIcon).OfType<PathIcon>().Single();
        Check(firstMark.Data is not null && secondMark.Data is not null && !ReferenceEquals(firstMark.Data, secondMark.Data), "Idle representations of the same tab must restore their own app geometry.");
        canvas.Children.Remove(sidebarIcon);
        status.Update([Report("", ProgramState.Idle, "codex", "Ready")]);
        await Task.Delay(100);
        var codexIcon = Descendants(icon).OfType<PathIcon>().Single();
        Check(codexIcon.Data is not null && codexIcon.ReadLocalValue(IconElement.ForegroundProperty) == DependencyProperty.UnsetValue, "Codex has a black-and-white brand, so its mark must follow the theme foreground.");
        await CaptureAsync(canvas, "icon-codex-idle");
        foreach (var agent in new[] { "gemini", "qwen", "copilot", "cursor", "opencode", "cline" })
        {
            status.Update([Report("", ProgramState.Idle, agent, "Ready")]);
            await Task.Delay(100);
            await CaptureAsync(canvas, $"icon-{agent}-idle");
        }
        status.Update([claude with { Status = claude.Status with { IsActive = false } }]);
        Check(ReferenceEquals(tab.IconSource, shell), "Exiting an agent must restore shell identity.");
        Console.WriteLine($"Native executable icon resolved; tab activity rendered {frames} frames; 62% accessible progress and static completion passed. Motion enabled={moving}.");
    }

    internal static async Task CheckIconPreferencesAsync(Window window)
    {
        var preferences = new IconPreferences();
        var tab = new TabViewItem { Header = "PowerShell" };
        var status = new TabProgramStatus(tab);
        tab.Tag = status;
        var report = Report("", ProgramState.Working, "claude", "Selected report text");
        status.Update([report]);
        var icon = new ProgramStatusIcon(null, preferences) { Tab = tab };
        var card = new ProgramStatusCard(preferences);
        card.Update("PowerShell", [report], true);
        var host = new StackPanel { Children = { icon, card } };
        window.Content = host;
        window.Activate();
        await Task.Delay(150);
        var text = Descendants(card).OfType<TextBlock>().Single(t => t.Text == "Selected report text");
        text.Select(text.ContentStart, text.ContentEnd);
        var row = VisualTreeHelper.GetParent(text);
        var tabMark = Descendants(icon).OfType<PathIcon>().Single();
        var rowMark = Descendants(card).OfType<PathIcon>().Single();
        var brand = Windows.UI.Color.FromArgb(255, 0xD9, 0x77, 0x57);
        Check(preferences.Subscribers == 2, "Each loaded presentation must observe preferences once.");
        foreach (var contrast in new[] { false, true, false })
        {
            preferences.SetContrast(contrast);
            await Task.Delay(150);
            foreach (var mark in new[] { tabMark, rowMark })
                Check(contrast ? mark.ReadLocalValue(IconElement.ForegroundProperty) == DependencyProperty.UnsetValue
                    : mark.Foreground is SolidColorBrush brush && brush.Color == brand,
                    "Unchanged tab and row marks must enter and leave high contrast.");
            Check(ReferenceEquals(rowMark, Descendants(card).OfType<PathIcon>().Single()) &&
                ReferenceEquals(row, VisualTreeHelper.GetParent(text)) && text.SelectedText == "Selected report text",
                "Preference refresh must preserve the rendered row, text identity and selection.");
            foreach (var root in new DependencyObject[] { icon, card })
            {
                var activity = Descendants(root).OfType<ProgramActivity>().Single();
                Check(activity.IsAnimating == !contrast, "Contrast must stop and restore permitted working motion.");
                var presenter = Descendants(root).OfType<ContentPresenter>().Single(p => p.Content is Viewbox);
                Check(presenter.Visibility == (contrast ? Visibility.Visible : Visibility.Collapsed), "A static mark must replace the sweep in high contrast.");
            }
        }
        report = report with { Status = report.Status with { IsActive = false } };
        card.Update("PowerShell", [report], true);
        rowMark = Descendants(card).OfType<PathIcon>().Single();
        foreach (var contrast in new[] { true, false })
        {
            preferences.SetContrast(contrast);
            await Task.Delay(100);
            Check(contrast ? rowMark.ReadLocalValue(IconElement.ForegroundProperty) == DependencyProperty.UnsetValue
                : rowMark.Foreground is SolidColorBrush brush && brush.Color == brand,
                "An exited report must refresh contrast in both directions without another report update.");
            Check(ReferenceEquals(text, Descendants(card).OfType<TextBlock>().Single(t => t.Text == "Selected report text")) &&
                ReferenceEquals(row, VisualTreeHelper.GetParent(text)) && text.SelectedText == "Selected report text" &&
                !Descendants(card).OfType<ProgramActivity>().Single().IsAnimating,
                "Exited reports must retain row identity, selected text and a static icon.");
        }
        host.Children.Clear();
        await Task.Delay(100);
        Check(preferences.Subscribers == 0, "Unloaded presentations must release preference subscriptions.");
        Check(Descendants(card).OfType<ProgramActivity>().All(a => !a.IsAnimating), "Unloading must stop row motion.");
        preferences.SetContrast(true);
        host.Children.Add(icon);
        host.Children.Add(card);
        await Task.Delay(150);
        Check(preferences.Subscribers == 2 && rowMark.ReadLocalValue(IconElement.ForegroundProperty) == DependencyProperty.UnsetValue,
            "Reattachment must read preferences changed while unloaded without duplicating subscriptions.");
        host.Children.Clear();
        await Task.Delay(100);
        Check(preferences.Subscribers == 0, "Repeated unload must release all subscriptions.");
    }

    private sealed class IconPreferences : IProgramIconPreferences
    {
        public bool HighContrast { get; private set; }
        public bool AnimationsEnabled => true;
        private Action? _changed;
        internal int Subscribers => _changed?.GetInvocationList().Length ?? 0;
        public event Action? Changed { add => _changed += value; remove => _changed -= value; }
        internal void SetContrast(bool value) { HighContrast = value; _changed?.Invoke(); }
    }

    internal static async Task CheckLayoutAsync(Window window)
    {
        var tab = new TabViewItem { Header = "PowerShell" };
        var status = new TabProgramStatus(tab);
        tab.Tag = status;
        var tabs = new TabView();
        tabs.TabItems.Add(tab);
        window.Content = tabs;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 850));
        window.Activate();
        await Task.Delay(300);
        status.Update([Report("", ProgramState.Working, "Build", "Layout fixture")]);
        status.ShowDetails(tab);
        await Task.Delay(300);
        var card = OpenCard(tab);
        Check(card is not null && card.ActualHeight > 0, "The native layout fixture must render a status card.");
        try { await CheckReadingUpdatesAsync(status, card!); }
        finally { status.Hide(); await Task.Delay(150); }
        Console.WriteLine("Native status layout checks passed without global input injection.");
    }

    internal static async Task CheckCardAsync(Window window)
    {
        var tab = new TabViewItem { Header = "PowerShell" };
        var status = new TabProgramStatus(tab);
        tab.Tag = status;
        var tabs = new TabView();
        tabs.TabItems.Add(tab);
        var root = new Grid { RequestedTheme = ElementTheme.Dark, Children = { tabs } };
        window.Content = root;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 850));
        window.Activate();
        await Task.Delay(300);
        var now = DateTimeOffset.Now;
        PaneProgramStatus Session(string id, ProgramState state, string? app, string? title, string? message, int pane = 1, string? kind = null) =>
            new(pane, "PowerShell", new(id, state, kind, null, app, app ?? "claude-code", title, message, now));
        PaneProgramStatus[] session =
        [
            Session("", ProgramState.Working, "claude-code", null, "Refining the tab status card"),
            Session("explore", ProgramState.Working, null, "Explore tooltip code", "Reading ProgramStatusCard.cs"),
            Session("review", ProgramState.Done, null, "Review diff", "No blocking issues"),
            Session("tests", ProgramState.Blocked, null, "Run smoke tests", "Allow dotnet run?", kind: "permission"),
        ];
        status.Update(session);
        status.ShowDetails(tab);
        await Task.Delay(400);
        var card = OpenCard(tab)!;
        await CaptureAsync(card, "card-session-dark");
        var names = Descendants(card).OfType<TextBlock>().Where(block => block.Name == "Name").Select(block => block.Text).ToArray();
        Check(names.Contains("Claude Code") && !names.Contains("claude-code"), "Known agents must show their product name instead of the reported app id.");
        var moving = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled && !new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        var animating = Descendants(card).OfType<ProgramActivity>().Count(activity => activity.IsAnimating);
        Check(animating == (moving ? 2 : 0), $"Each working session and subagent row must show the tab activity indicator only while motion is allowed (animating={animating}).");
        status.Hide();
        await Task.Delay(150);
        root.RequestedTheme = ElementTheme.Light;
        status.Update([.. session, Session("", ProgramState.Idle, "codex", null, "Waiting for input", pane: 2) with { Title = "Codex review" }]);
        status.ShowDetails(tab);
        await Task.Delay(400);
        await CaptureAsync(OpenCard(tab)!, "card-session-light");
        var paneText = Text(OpenCard(tab)!);
        Check(paneText.Contains("Pane 2 · Codex review · PowerShell") && paneText.Contains("Pane 1 · PowerShell"),
            "Pane headings must use the pane's program title and keep the shell only when the title replaced it.");
        status.Update([.. session, Session("", ProgramState.Idle, "codex", null, "Waiting for input", pane: 2) with { Title = "Codex tests" }]);
        await Task.Delay(100);
        Check(Text(OpenCard(tab)!).Contains("Pane 2 · Codex tests · PowerShell"), "A program title change must rename its pane heading in the open card.");
        status.Update([.. session.Select(record => record with { Status = record.Status with { State = ProgramState.Done } })]);
        await Task.Delay(200);
        Check(Descendants(OpenCard(tab)!).OfType<ProgramActivity>().All(activity => !activity.IsAnimating), "Finished rows must stop the activity indicator.");
        status.Hide();
        await Task.Delay(150);
        Console.WriteLine($"Status card session rows, product names and per-row activity passed. Motion enabled={moving}.");
    }

    private static async Task CheckReadingUpdatesAsync(TabProgramStatus status, ProgramStatusCard card)
    {
        var reports = Enumerable.Range(0, 20).Select(index => Report($"task{index:D2}", ProgramState.Working, $"Task {index}", "Reading detail " + index)).ToArray();
        status.Update(reports);
        await Task.Delay(150);
        var reading = Descendants(card).OfType<TextBlock>().Single(t => t.Text == "Reading detail 10");
        card.Scroll.ChangeView(null, card.Scroll.VerticalOffset + reading.TransformToVisual(card.Scroll).TransformPoint(new(0, 0)).Y - 30, null, true);
        await Task.Delay(150);
        var before = reading.TransformToVisual(card.Scroll).TransformPoint(new(0, 0)).Y;
        reading.Select(reading.ContentStart, reading.ContentEnd);
        status.Update(reports.Select((report, index) => index == 0 ? report with { Status = report.Status with { Message = string.Join("\n", Enumerable.Repeat("Growing report", 12)) } } : report).ToArray());
        await Task.Delay(150);
        var growthShift = reading.TransformToVisual(card.Scroll).TransformPoint(new(0, 0)).Y - before;
        var selectionKept = reading.SelectedText == "Reading detail 10";

        var older = Report("old", ProgramState.Working, "Older row", "Created first, below the viewport");
        status.Update([older]);
        await Task.Delay(100);
        var urgent = Enumerable.Range(0, 8).Select(index => Report($"a{index}", ProgramState.Blocked, $"Urgent {index}", "Urgent detail " + index, pane: 2)).ToArray();
        status.Update([older, .. urgent]);
        await Task.Delay(150);
        card.Scroll.ChangeView(null, 0, null, true);
        await Task.Delay(150);
        reading = Descendants(card).OfType<TextBlock>().Single(t => t.Text == "Urgent detail 0");
        before = reading.TransformToVisual(card.Scroll).TransformPoint(new(0, 0)).Y;
        status.Update([older, .. urgent, Report("z", ProgramState.Blocked, "Inserted later", "After the visible row, before the older row", pane: 2)]);
        await Task.Delay(150);
        var insertionShift = reading.TransformToVisual(card.Scroll).TransformPoint(new(0, 0)).Y - before;
        Console.WriteLine($"Reading-position regression: in-place growth shift={growthShift:F2}px, insertion below visible row shift={insertionShift:F2}px, selection retained={selectionKept}.");
        Check(Math.Abs(growthShift) < 2 && Math.Abs(insertionShift) < 2 && selectionKept, "In-place growth and insertion below the visible report must preserve reading position and selection.");
    }

    private static PaneProgramStatus Report(string id, ProgramState state, string title, string message, int pane = 1, int? progress = null, string? kind = null) =>
        new(pane, "PowerShell", new(id, state, kind, progress, title, title, title, message, DateTimeOffset.Now));

    private static ProgramStatusCard? OpenCard(FrameworkElement target) => VisualTreeHelper.GetOpenPopupsForXamlRoot(target.XamlRoot)
        .SelectMany(p => Descendants(p.Child)).OfType<ProgramStatusCard>().SingleOrDefault();
    private static string Text(DependencyObject root) => string.Join("\n", Descendants(root).OfType<TextBlock>().Select(t => t.Text));
    private static IEnumerable<string> AccessibleNames(Microsoft.UI.Xaml.Automation.Peers.AutomationPeer peer)
    {
        yield return peer.GetName();
        foreach (var child in peer.GetChildren() ?? [])
            foreach (var name in AccessibleNames(child)) yield return name;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    internal static async Task CaptureAsync(FrameworkElement element, string name)
    {
        var presenter = element;
        while (VisualTreeHelper.GetParent(presenter) is FrameworkElement parent) presenter = parent;
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(presenter);
        var bytes = (await bitmap.GetPixelsAsync()).ToArray();
        var path = Path.GetFullPath(Path.Combine("artifacts", "program-status", name + ".png"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var file = await StorageFile.GetFileFromPathAsync(Create(path));
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        if (element is ProgramStatusCard)
        {
            var bounds = element.TransformToVisual(presenter).TransformPoint(new Windows.Foundation.Point(-16, -16));
            encoder.BitmapTransform.Bounds = new BitmapBounds { X = (uint)Math.Max(0, bounds.X), Y = (uint)Math.Max(0, bounds.Y), Width = (uint)(element.ActualWidth + 32), Height = (uint)(element.ActualHeight + 32) };
        }
        await encoder.FlushAsync();
        Console.WriteLine("Native status capture " + path);
    }
    private static string Create(string path) { File.WriteAllBytes(path, []); return path; }
    private static void Move(Window window, FrameworkElement target, double x, double y)
    {
        var point = target.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(x, y));
        var origin = new Point();
        ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(window), ref origin);
        var scale = target.XamlRoot.RasterizationScale;

        Inject(window, PointerInput(0xC001, (int)((origin.X + point.X * scale - GetSystemMetrics(76)) * 65535 / GetSystemMetrics(78)), (int)((origin.Y + point.Y * scale - GetSystemMetrics(77)) * 65535 / GetSystemMetrics(79))));
    }
    private static void Press(Window window, params byte[] keys)
    {
        var down = keys.Select(key => new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { Key = key } } });
        var up = keys.Reverse().Select(key => new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { Key = key, Flags = 2 } } });
        Inject(window, down.Concat(up).ToArray());
    }
    private static Input PointerInput(uint flags, int x = 0, int y = 0) => new() { Data = new InputData { Mouse = new MouseInput { X = x, Y = y, Flags = flags } } };
    private static void RequireForeground(Window window)
    {
        var expected = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var actual = GetForegroundWindow();
        Check(actual == expected, $"Smoke window lost foreground ownership (expected {expected:X}, actual {actual:X}); input injection stopped.");
    }
    private static void Inject(Window window, params Input[] inputs)
    {
        RequireForeground(window);
        Check(SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length, "Windows rejected smoke input injection.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int show);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")] private static extern bool Foreground(nint hwnd);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
