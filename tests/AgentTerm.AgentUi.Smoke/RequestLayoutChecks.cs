using AgentTerm.Agents;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

internal static class RequestLayoutChecks
{
    private const double MinimumPaneWidth = 180;
    private const double PaneInset = 1;
    private const double PagePadding = 8;
    private const double NarrowWidth = MinimumPaneWidth - 2 * PaneInset - 2 * PagePadding;
    private const double WideWidth = 480;
    private const double Tolerance = 0.5;
    private const string LongCommand = "Get-ChildItem -Recurse -Filter *.cs | Select-String -Pattern 'RequestHeaderLayout' | Format-Table -AutoSize";

    internal static async Task RunAsync(Window window)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        await using var pane = new RequestFocusChecks.Pane(dispatcher, "Layout");
        pane.View.HorizontalAlignment = HorizontalAlignment.Left;
        pane.View.VerticalAlignment = VerticalAlignment.Top;
        pane.View.Width = NarrowWidth;
        var root = new Grid { RequestedTheme = ElementTheme.Dark, Background = new SolidColorBrush(Microsoft.UI.Colors.Black), Children = { pane.View } };
        window.Content = root;
        var size = window.AppWindow.Size;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Max(size.Width, 1600), Math.Max(size.Height, 1000)));
        window.Activate();
        try
        {
            await Program.WaitAsync(() => root.XamlRoot is not null && root.IsLoaded);
            await pane.StartAsync();

            foreach (var approve in new[] { true, false })
            {
                await pane.AskApprovalAsync(LongCommand);
                var request = (ApprovalRequest)pane.Panel.Shown!;
                var where = $"approval at {NarrowWidth} DIP";
                await SettleAsync(root);
                Check(Math.Abs(pane.Panel.ActualWidth - NarrowWidth) < Tolerance, $"{where}: the request panel measured {pane.Panel.ActualWidth:F1} DIP.");
                var actions = CheckApproval(root, pane, where);
                var title = Bounds(pane.Title, root);
                foreach (var (button, rect) in actions)
                    Check(rect.Y + Tolerance >= title.Bottom, $"{where}: \"{AutomationProperties.GetName(button)}\" starts at {rect.Y:F1}, above the title bottom {title.Bottom:F1}.");
                var target = approve ? pane.Approve : pane.Reject;
                new ButtonAutomationPeer(target).Invoke();
                await pane.WaitDismissedAsync($"{AutomationProperties.GetName(target)} at {NarrowWidth} DIP");
                Check(pane.Harness.Session.LastResponse is { } response && response.Request == request.Request && response.Approved == approve,
                    $"{where}: \"{AutomationProperties.GetName(target)}\" sent {pane.Harness.Session.LastResponse}.");
            }

            pane.View.Width = WideWidth;
            await pane.AskApprovalAsync(LongCommand);
            await SettleAsync(root);
            var wide = $"approval at {WideWidth} DIP";
            var wideTitle = Bounds(pane.Title, root);
            foreach (var (button, rect) in CheckApproval(root, pane, wide))
                Check(rect.X + Tolerance >= wideTitle.Right && rect.Y < wideTitle.Bottom && rect.Bottom > wideTitle.Y,
                    $"{wide}: \"{AutomationProperties.GetName(button)}\" left the title row ({Describe(rect)} vs title {Describe(wideTitle)}).");
            new ButtonAutomationPeer(pane.Approve).Invoke();
            await pane.WaitDismissedAsync($"Run at {WideWidth} DIP");

            pane.View.Width = NarrowWidth;
            var question = new QuestionRequest(pane.Harness.Session.Turn, Guid.NewGuid(), "Which target should the agent build before it runs the narrow pane fixture?",
                ["Release x64", "Debug x64"], false);
            pane.Harness.Session.Emit(question);
            await Program.WaitAsync(() => pane.Panel.Shown == question && pane.Panel.Visibility == Visibility.Visible);
            await SettleAsync(root);
            CheckQuestion(root, pane, $"question at {NarrowWidth} DIP");
            var choice = pane.Choices.Children.OfType<Button>().Last();
            new ButtonAutomationPeer(choice).Invoke();
            await pane.WaitDismissedAsync($"choice at {NarrowWidth} DIP");
            Check(pane.Harness.Session.LastResponse is { } answered && answered.Request == question.Request && answered.Answer == question.Choices[^1],
                $"question at {NarrowWidth} DIP: the choice sent {pane.Harness.Session.LastResponse}.");
        }
        finally { window.AppWindow.Resize(size); }
        Console.WriteLine($"Request panel kept Reject and Run visible, hittable and invokable at {NarrowWidth} DIP (a {MinimumPaneWidth} DIP pane) and inline at {WideWidth} DIP; question choices fit at {NarrowWidth} DIP.");
    }

    private static (Button Button, Rect Rect)[] CheckApproval(FrameworkElement root, RequestFocusChecks.Pane pane, string where)
    {
        var card = Content(pane, root);
        var title = Bounds(pane.Title, root);
        var detail = Bounds(pane.Detail, root);
        CheckInside(where, "title", title, card);
        CheckInside(where, "command", detail, card);
        var actions = new[] { pane.Reject, pane.Approve }.Select(button => (Button: button, Rect: Bounds(button, root))).ToArray();
        foreach (var (button, rect) in actions)
        {
            var id = AutomationProperties.GetName(button);
            Check(IsShown(button) && rect.Width > 0 && rect.Height > 0, $"{where}: \"{id}\" has no size.");
            CheckInside(where, id, rect, card);
            Check(rect.Width + Tolerance >= button.DesiredSize.Width, $"{where}: \"{id}\" is clipped to {rect.Width:F1} of {button.DesiredSize.Width:F1} DIP.");
            foreach (var text in Descendants(button).OfType<TextBlock>().Where(IsShown))
                CheckInside(where, $"\"{text.Text}\" in \"{id}\"", Bounds(text, root), rect);
            Check(button is { IsEnabled: true, IsTabStop: true }, $"{where}: \"{id}\" is not keyboard reachable.");
            Check(!Intersects(rect, title), $"{where}: \"{id}\" {Describe(rect)} overlaps the title {Describe(title)}.");
            Check(!Intersects(rect, detail), $"{where}: \"{id}\" {Describe(rect)} overlaps the command {Describe(detail)}.");
            CheckHit(where, id, button, rect, root);
        }
        Check(!Intersects(actions[0].Rect, actions[1].Rect), $"{where}: Reject {Describe(actions[0].Rect)} overlaps Run {Describe(actions[1].Rect)}.");
        Check(AutomationProperties.GetName(pane.Reject) == "Reject" && AutomationProperties.GetName(pane.Approve) == "Run once", $"{where}: approval actions lost their names.");
        return actions;
    }

    private static void CheckQuestion(FrameworkElement root, RequestFocusChecks.Pane pane, string where)
    {
        var card = Content(pane, root);
        var title = Bounds(pane.Title, root);
        var slot = ((FrameworkElement)VisualTreeHelper.GetParent(pane.Title)).TransformToVisual(root).TransformBounds(LayoutInformation.GetLayoutSlot(pane.Title));
        CheckInside(where, "question", title, card);
        Check(!IsShown(pane.Reject) && !IsShown(pane.Approve), $"{where}: approval actions stayed visible.");
        Check(Math.Abs(slot.Right - card.Right) <= Tolerance, $"{where}: the question slot ends at {slot.Right:F1}, short of the card edge {card.Right:F1}.");
        var choices = pane.Choices.Children.OfType<Button>().Select(button => (Button: button, Rect: Bounds(button, root))).ToArray();
        Check(choices.Length == 2, $"{where}: expected 2 choices, found {choices.Length}.");
        for (var i = 0; i < choices.Length; i++)
        {
            var (button, rect) = choices[i];
            var id = $"choice {i + 1}";
            Check(rect.Width > 0 && rect.Height > 0, $"{where}: {id} has no size.");
            CheckInside(where, id, rect, card);
            Check(rect.Y + Tolerance >= title.Bottom, $"{where}: {id} overlaps the question.");
            if (i > 0) Check(!Intersects(choices[i - 1].Rect, rect), $"{where}: {id} overlaps the previous choice.");
            Check(button is { IsEnabled: true, IsTabStop: true }, $"{where}: {id} is not keyboard reachable.");
            CheckHit(where, id, button, rect, root);
        }
    }

    private static Rect Content(RequestFocusChecks.Pane pane, FrameworkElement root)
    {
        var border = (Border)pane.Panel.Content;
        var outer = Bounds(border, root);
        var inset = border.BorderThickness;
        var padding = border.Padding;
        return new(outer.X + inset.Left + padding.Left, outer.Y + inset.Top + padding.Top,
            Math.Max(0, outer.Width - inset.Left - inset.Right - padding.Left - padding.Right),
            Math.Max(0, outer.Height - inset.Top - inset.Bottom - padding.Top - padding.Bottom));
    }

    private static void CheckHit(string where, string id, Button button, Rect rect, FrameworkElement root)
    {
        var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        var host = root.TransformToVisual(null).TransformPoint(center);
        var hit = VisualTreeHelper.FindElementsInHostCoordinates(host, root).OfType<Button>().FirstOrDefault();
        Check(ReferenceEquals(hit, button), $"{where}: a click at the centre of \"{id}\" reaches {(hit is null ? "nothing" : AutomationProperties.GetName(hit))}.");
    }

    private static void CheckInside(string where, string id, Rect rect, Rect bounds) =>
        Check(rect.X + Tolerance >= bounds.X && rect.Y + Tolerance >= bounds.Y && rect.Right <= bounds.Right + Tolerance && rect.Bottom <= bounds.Bottom + Tolerance,
            $"{where}: {id} {Describe(rect)} leaves {Describe(bounds)}.");

    private static bool Intersects(Rect a, Rect b) =>
        a.X < b.Right - Tolerance && b.X < a.Right - Tolerance && a.Y < b.Bottom - Tolerance && b.Y < a.Bottom - Tolerance;

    private static async Task SettleAsync(FrameworkElement root)
    {
        await Task.Delay(150);
        root.UpdateLayout();
    }

    private static Rect Bounds(FrameworkElement element, UIElement root) =>
        element.TransformToVisual(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static string Describe(Rect rect) => $"{rect.X:F1},{rect.Y:F1} {rect.Width:F1}x{rect.Height:F1}";

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static bool IsShown(DependencyObject element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
