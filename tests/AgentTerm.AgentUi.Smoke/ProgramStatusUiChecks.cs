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
        var working = Report("", ProgramState.Working, "Build", "Compiling", progress: 30);
        status.Update([working]);
        Check(ToolTipService.GetToolTip(tab) is ToolTip { IsEnabled: false } && ToolTipService.GetToolTip(sidebar) is null, "Status must have one rich surface, without a competing basic tooltip.");
        Check(AutomationProperties.GetHelpText(tab).Contains("Working · 30%") && AutomationProperties.GetHelpText(sidebar).Contains("Compiling"), "Both layouts must expose complete status to assistive technology.");
        Check(!ReferenceEquals(tab.IconSource, shell), "Program status must replace the shell icon.");
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

        status.Update([Report("", ProgramState.Done, "pi", "Quiver Git Pull")]);
        await Task.Delay(300);
        input.Focus(FocusState.Programmatic);
        Move(window, input, 100, 15);
        await Task.Delay(100);
        Move(window, tab, 70, 16);
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
        Press(0x1B);
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
        Mouse(0x0002, 0, 0, 0, 0);
        Mouse(0x0004, 0, 0, 0, 0);
        await Task.Delay(300);
        Move(window, input, 500, 180);
        await Task.Delay(600);
        Check(OpenCard(tab) == card && pin.Visibility == Visibility.Collapsed, "Clicking Keep open must pin the same card after the pointer leaves.");
        Check(ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(tab.XamlRoot), card.Scroll), "Keep open must focus reports for keyboard use.");
        await CaptureAsync(card, "pinned-pointer");
        Press(0x1B);
        await Task.Delay(200);
        Check(OpenCard(tab) is null, "Escape must dismiss a pointer-pinned card.");
        Console.WriteLine("Keep open pointer click, persistence after pointer exit, keyboard focus and Escape passed.");

        tab.Focus(FocusState.Keyboard);
        Press(0x12, 0x28);
        await Task.Delay(300);
        card = OpenCard(tab);
        Check(card is not null, "Alt+Down must open status directly from a focused tab.");
        Check(ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(tab.XamlRoot), card!.Scroll), "Keyboard opening must focus the report scroller.");
        await CaptureAsync(card, "pinned-keyboard");
        var reports = Enumerable.Range(0, 256).Select(index => Report($"task{index:D3}", ProgramState.Working, $"Task {index}", "Detail " + index)).ToArray();
        status.Update(reports);
        await Task.Delay(300);
        Check(Text(card).Contains("Detail 255"), "Every report must remain available.");
        Press(0x11, 0x23);
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
        Press(0x10, 0x24);
        await Task.Delay(100);
        Check(Math.Abs(card.Scroll.VerticalOffset - readingOffset) < 1, "Shift+Home in selectable text must not jump the report scroller.");
        Console.WriteLine("Structural insertion/removal preserve selected text, row identity and the visible report.");
        Press(0x1B);
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
    }

    internal static async Task CheckIconsAsync(Window window)
    {
        var tab = new TabViewItem { Header = "Command Prompt" };
        var status = new TabProgramStatus(tab);
        tab.Tag = status;
        var icon = new ProgramStatusIcon { Tab = tab, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var canvas = new Grid { Width = 160, Height = 48, Padding = new Thickness(12), RequestedTheme = ElementTheme.Dark, Background = new SolidColorBrush(Microsoft.UI.Colors.Black), Children = { icon } };
        window.Content = canvas;
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
        var ring = Descendants(icon).OfType<ProgressRing>().Single();
        Check(!ring.IsIndeterminate && ring.Value == 62 && ring.Visibility == Visibility.Visible, "Reported progress must stay visible even when animation is disabled.");
        await CaptureAsync(canvas, "icon-progress");
        status.Update([working with { Status = working.Status with { State = ProgramState.Done } }]);
        Check(await Program.SampleAsync(canvas, 3) == 1, "Completed tabs must stop animating.");
        await CaptureAsync(canvas, "icon-done");
        Console.WriteLine($"Native executable icon resolved; tab activity rendered {frames} frames; 62% progress and static completion passed. Motion enabled={moving}.");
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
    private static async Task CaptureAsync(FrameworkElement element, string name)
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

        Mouse(0xC001, (uint)((origin.X + point.X * scale - GetSystemMetrics(76)) * 65535 / GetSystemMetrics(78)), (uint)((origin.Y + point.Y * scale - GetSystemMetrics(77)) * 65535 / GetSystemMetrics(79)), 0, 0);
    }
    private static void Press(params byte[] keys)
    {
        foreach (var key in keys) Key(key, 0, 0, 0);
        foreach (var key in keys.Reverse()) Key(key, 0, 2, 0);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int show);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", EntryPoint="mouse_event")] private static extern void Mouse(uint flags, uint x, uint y, uint data, nuint extra);
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")] private static extern bool Foreground(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "keybd_event")] private static extern void Key(byte key, byte scan, uint flags, nuint extra);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
