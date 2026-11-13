using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

internal static class ProgramActivityChecks
{
    internal static async Task RunAsync(Window window)
    {
        var motion = true;
        var tab = new TabViewItem { Header = "Activity fixture" };
        var status = new TabProgramStatus(tab);
        tab.Tag = status;
        status.SetShellIcon(new FontIconSource { Glyph = "S" });
        var icon = new ProgramStatusIcon(() => motion) { Tab = tab, HorizontalAlignment = HorizontalAlignment.Left };
        var secondIcon = new ProgramStatusIcon(() => motion) { Tab = tab, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(40, 0, 0, 0) };
        var edge = new ProgramActivity(ProgramActivityKind.PaneEdge, () => motion) { Working = true };
        var idleEdge = new ProgramActivity(ProgramActivityKind.PaneEdge, () => motion);
        var firstPane = new Grid { Height = 90, Margin = new Thickness(0, 40, 0, 0), Children = { edge } };
        var secondPane = new Grid { Height = 90, Margin = new Thickness(0, 140, 0, 0), Children = { idleEdge } };
        var canvas = new Grid { Width = 600, Height = 260, Padding = new Thickness(16), RequestedTheme = ElementTheme.Dark,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Black), Children = { icon, secondIcon, firstPane, secondPane } };
        icon.VerticalAlignment = secondIcon.VerticalAlignment = firstPane.VerticalAlignment = secondPane.VerticalAlignment = VerticalAlignment.Top;
        window.Content = canvas;
        window.Activate();
        var working = new ProgramStatus("", ProgramState.Working, null, null, "pi", "pi", "Fixture", null, DateTimeOffset.UtcNow);
        status.Update([new(1, "Shell", working)]);
        await Program.WaitAsync(() => Descendants(icon).OfType<ProgramActivity>().Any() && Descendants(secondIcon).OfType<ProgramActivity>().Any());
        await Program.WaitAsync(() => Activity(icon).IsAnimating && Activity(secondIcon).IsAnimating && edge.IsAnimating);
        Check(!idleEdge.IsAnimating, "An idle pane must not borrow another pane's activity.");
        Check(AppIcon(icon).Visibility == Visibility.Collapsed && AppIcon(secondIcon).Visibility == Visibility.Collapsed, "Both tab representations must replace the app icon while working.");
        Check(edge.ActualHeight == 1 && Math.Abs(edge.TransformToVisual(firstPane).TransformPoint(new(0, 0)).Y) < .1, "The comet must sit on the pane's top edge.");
        var activeFrames = await Program.SampleAsync(icon, 6);
        var cometFrames = await Program.SampleAsync(firstPane, 4);
        Check(activeFrames > 1 && cometFrames > 1, "Native dot sweep and comet must each produce changing pixels.");
        await ProgramStatusUiChecks.CaptureAsync(canvas, "activity-working-dark");
        foreach (var app in new[] { "pi", "claude", "cmd" })
        {
            working = working with { App = app, EffectiveApp = app };
            status.Update([new(1, "Shell", working)]);
            await Task.Delay(150);
            var source = tab.IconSource;
            motion = false;
            Activity(icon).RefreshPreferences();
            Activity(secondIcon).RefreshPreferences();
            edge.RefreshPreferences();
            Check(!Activity(icon).IsAnimating && !edge.IsAnimating, "Reduced motion must stop both loops.");
            Check(AppIcon(icon).Visibility == Visibility.Visible && AppIcon(secondIcon).Visibility == Visibility.Visible, "Reduced motion must keep the normal app icon, not a static matrix.");
            Check(AppIcon(icon).Width == 16 && ReferenceEquals(source, tab.IconSource), "Reduced motion must preserve the full-size app identity.");
            Check(await Program.SampleAsync(canvas, 3) == 1, "Reduced-motion pixels must stay static.");
            await ProgramStatusUiChecks.CaptureAsync(canvas, $"activity-reduced-{app}");
            motion = true;
            Activity(icon).RefreshPreferences();
            Activity(secondIcon).RefreshPreferences();
            edge.RefreshPreferences();
            Check(Activity(icon).IsAnimating && AppIcon(icon).Visibility == Visibility.Collapsed, "Re-enabling motion must restore animation without a new status report.");
        }
        foreach (var state in new[] { ProgramState.Blocked, ProgramState.Error, ProgramState.Done, ProgramState.Idle })
        {
            status.Update([new(1, "Shell", working with { State = state })]);
            Check(!Activity(icon).IsAnimating && !Activity(secondIcon).IsAnimating, $"{state} must stop tab loops.");
            Check(AppIcon(icon).Visibility == Visibility.Visible, $"{state} must restore the normal status or program icon.");
        }
        status.Update([new(1, "Shell", working), new(2, "Shell", working with { State = ProgramState.Blocked })]);
        Check(!Activity(icon).IsAnimating, "A blocked pane must retain priority over another pane working.");
        status.Update([new(1, "Shell", working)]);
        edge.Active = false;
        Check(!edge.IsAnimating, "A hidden pane must immediately stop its comet.");
        edge.Active = true;
        Check(edge.IsAnimating, "A visible pane must resume its own work.");
        canvas.RequestedTheme = ElementTheme.Light;
        await Task.Delay(200);
        Check(Activity(icon).IsAnimating && edge.IsAnimating, "Theme changes must preserve ongoing work.");
        await ProgramStatusUiChecks.CaptureAsync(canvas, "activity-working-light");
        canvas.Visibility = Visibility.Collapsed;
        await Program.WaitAsync(() => !Activity(icon).IsAnimating && !edge.IsAnimating);
        canvas.Visibility = Visibility.Visible;
        await Program.WaitAsync(() => Activity(icon).IsAnimating && edge.IsAnimating);
        var presenter = (Microsoft.UI.Windowing.OverlappedPresenter)window.AppWindow.Presenter;
        presenter.Minimize();
        await Program.WaitAsync(() => !Activity(icon).IsAnimating && !edge.IsAnimating);
        presenter.Restore();
        await Program.WaitAsync(() => Activity(icon).IsAnimating && edge.IsAnimating);
        canvas.Children.Clear();
        await Task.Delay(150);
        Check(!Activity(icon).IsAnimating && !Activity(secondIcon).IsAnimating && !edge.IsAnimating, "Unloading must release all activity loops.");
        canvas.Children.Add(icon);
        await Program.WaitAsync(() => Activity(icon).IsAnimating);
        status.Update([]);
        Check(!Activity(icon).IsAnimating && AppIcon(icon).Visibility == Visibility.Visible, "Clearing reports after reattachment must restore the shell icon.");
        Console.WriteLine($"Program activity passed. {activeFrames} native icon frames, {cometFrames} comet frames; reduced Pi/Claude/executable icons and waiting/completed states static; independent panes, hidden/unloaded controls and reattachment passed. Windows preferences were not changed.");
    }

    private static ProgramActivity Activity(ProgramStatusIcon icon) => Descendants(icon).OfType<ProgramActivity>().Single();
    private static ContentPresenter AppIcon(ProgramStatusIcon icon) => Descendants(icon).OfType<ContentPresenter>().Single(presenter => presenter.Content is IconElement or Viewbox);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
