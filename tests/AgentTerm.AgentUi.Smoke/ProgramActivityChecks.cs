using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Reflection;
using Microsoft.UI.Xaml.Media.Animation;

internal static class ProgramActivityChecks
{
    private static Windows.System.DispatcherQueueController? _ownedTestQueue;
    [StructLayout(LayoutKind.Sequential)] private struct DispatcherQueueOptions { public int Size, ThreadType, ApartmentType; }
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
    internal static async Task RunAsync(Window window)
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is null)
        {
            var options = new DispatcherQueueOptions { Size = Marshal.SizeOf<DispatcherQueueOptions>(), ThreadType = 2, ApartmentType = 2 };
            Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var pointer));
            try { _ownedTestQueue = WinRT.MarshalInterface<Windows.System.DispatcherQueueController>.FromAbi(pointer); }
            finally { Marshal.Release(pointer); }
        }
        if (Environment.GetCommandLineArgs().Contains("--activity-occlusion"))
        {
            await CheckOcclusionAsync(window);
            return;
        }
        if (Environment.GetCommandLineArgs().Contains("--activity-transitions"))
        {
            await CheckTransitionsAsync(window);
            return;
        }
        if (Environment.GetCommandLineArgs().Contains("--activity-parity"))
        {
            await CaptureParityAsync(window);
            return;
        }
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

        var activeFrames = await ActivityPixelCapture.SampleAsync(window, icon, 6, "activity-composited-dots");
        var cometFrames = await ActivityPixelCapture.SampleAsync(window, firstPane, 4, "activity-composited-edge");
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
            Check(await ActivityPixelCapture.SampleAsync(window, canvas, 3) == 1, "Reduced-motion pixels must stay static.");
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
        canvas.Children.Remove(firstPane);
        var split = new Grid { Children = { firstPane } };
        canvas.Children.Add(split);
        await Task.Delay(150);
        Check(edge.IsLoaded && edge.IsAnimating, "Reparenting an active pane into a split must preserve its activity.");
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

    private static async Task CheckTransitionsAsync(Window window)
    {
        void Checkpoint(string step)
        {
            var path = Path.GetFullPath("artifacts/activity-optimization/production/transition-checkpoints.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.UtcNow:O} {step}{Environment.NewLine}");
        }
        Application.Current.UnhandledException += (_, args) => Checkpoint("Unhandled " + args.Exception);
        Checkpoint("Transition fixture entered; parsing visual-state host");
        var host = (UserControl)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Grid x:Name="MotionRoot" Width="600" Height="200" Background="Black">
                <Grid.RenderTransform><TranslateTransform /></Grid.RenderTransform>
                <VisualStateManager.VisualStateGroups>
                  <VisualStateGroup x:Name="DragStates">
                    <VisualStateGroup.Transitions><VisualTransition GeneratedDuration="0:0:0.2" /></VisualStateGroup.Transitions>
                    <VisualState x:Name="NotDragging"><Storyboard><DoubleAnimation Storyboard.TargetName="MotionRoot" Storyboard.TargetProperty="(UIElement.RenderTransform).(TranslateTransform.X)" To="0" Duration="0" /></Storyboard></VisualState>
                    <VisualState x:Name="Dragging"><Storyboard><DoubleAnimation Storyboard.TargetName="MotionRoot" Storyboard.TargetProperty="(UIElement.RenderTransform).(TranslateTransform.X)" To="20" Duration="0" /></Storyboard></VisualState>
                  </VisualStateGroup>
                  <VisualStateGroup x:Name="ReorderHintStates">
                    <VisualStateGroup.Transitions><VisualTransition GeneratedDuration="0:0:0.2" /></VisualStateGroup.Transitions>
                    <VisualState x:Name="NoReorderHint"><Storyboard><DoubleAnimation Storyboard.TargetName="MotionRoot" Storyboard.TargetProperty="(UIElement.RenderTransform).(TranslateTransform.Y)" To="0" Duration="0" /></Storyboard></VisualState>
                    <VisualState x:Name="ReorderHint"><Storyboard><DoubleAnimation Storyboard.TargetName="MotionRoot" Storyboard.TargetProperty="(UIElement.RenderTransform).(TranslateTransform.Y)" To="12" Duration="0" /></Storyboard></VisualState>
                  </VisualStateGroup>
                </VisualStateManager.VisualStateGroups>
              </Grid>
            </UserControl>
            """);
        Checkpoint("Visual-state host parsed");
        var content = (Grid)host.Content;
        var edge = new ProgramActivity(ProgramActivityKind.PaneEdge, () => true) { Width = 317, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(edge);
        window.Content = host;
        window.Activate();
        Checkpoint("Window activated; waiting edge Loaded");
        await Program.WaitAsync(() => edge.IsLoaded);
        Checkpoint("Edge loaded; entering initial visual states");
        Check(VisualStateManager.GoToState(host, "NotDragging", false) && VisualStateManager.GoToState(host, "NoReorderHint", false),
            "Motion fixture must enter both initial steady visual states.");
        var clock = Stopwatch.StartNew();
        Checkpoint("Initial visual states entered; starting Working");
        edge.Working = true;
        await Program.WaitAsync(() => UsesSystemCompositor(edge));
        Checkpoint("Native renderer ready; waiting400ms");
        await Task.Delay(400);
        Checkpoint("Setting host opacity .8");
        host.Opacity = .8;
        Checkpoint("Host opacity .8 set; waiting fallback");
        await Program.WaitAsync(() => !UsesSystemCompositor(edge));
        await Task.Delay(200);
        Checkpoint("Reading fallback phase");
        AssertPhase(ReadStoryboard().GetCurrentTime().TotalSeconds, "fallback after opacity change");
        Check(await ActivityPixelCapture.SampleAsync(window, edge, 3, "activity-transitions-fallback") > 1,
            "Fallback must continue to produce moving pixels.");
        host.Opacity = 1;
        Checkpoint("Host opacity restored; waiting native");
        await Program.WaitAsync(() => UsesSystemCompositor(edge));
        Checkpoint("Reading restored native phase");
        AssertNativePhase("native after opacity restoration");

        foreach (var (groupName, steady, moving) in new[] { ("DragStates", "NotDragging", "Dragging"), ("ReorderHintStates", "NoReorderHint", "ReorderHint") })
        {
            var group = VisualStateManager.GetVisualStateGroups(content).Single(value => value.Name == groupName);
            var changing = 0;
            var changed = 0;
            var nativeAtChanging = false;
            group.CurrentStateChanging += (_, _) => { changing++; nativeAtChanging = UsesSystemCompositor(edge); };
            group.CurrentStateChanged += (_, _) => changed++;
            Checkpoint("Entering " + moving);
            Check(VisualStateManager.GoToState(host, moving, true), "Motion fixture must accept " + moving);
            Checkpoint("GoToState returned " + moving);
            await Program.WaitAsync(() => changing > 0);
            Check(!nativeAtChanging && !UsesSystemCompositor(edge), "Changing must hand rendering to XAML before the ancestor starts moving.");
            await Task.Delay(250);
            Check(changed > 0 && group.CurrentState?.Name == moving && !UsesSystemCompositor(edge),
                "A non-steady visual state must retain ordinary XAML composition.");
            AssertPhase(ReadStoryboard().GetCurrentTime().TotalSeconds, groupName + " moving phase");

            Check(VisualStateManager.GoToState(host, steady, true), "Motion fixture must begin its steady return.");
            await Task.Delay(75);
            Check(!UsesSystemCompositor(edge), "Overlay must remain suppressed during the steady-state return transition.");
            Check(VisualStateManager.GoToState(host, moving, true), "Motion fixture must interrupt its return transition.");
            await Task.Delay(250);
            Check(group.CurrentState?.Name == moving && !UsesSystemCompositor(edge), "Interrupted transition must not resume a stale overlay.");
            Check(VisualStateManager.GoToState(host, steady, true), "Motion fixture must finish in a steady state.");
            await Program.WaitAsync(() => group.CurrentState?.Name == steady && UsesSystemCompositor(edge));
            AssertNativePhase(groupName + " steady phase");
            Check(await ActivityPixelCapture.SampleAsync(window, edge, 3, "activity-transitions-" + groupName) > 1,
                "Restored native rendering must produce moving pixels.");
        }
        Console.WriteLine("Activity transitions passed. Opacity fallback and interrupted drag/reorder transitions preserve elapsed animation phase and moving pixels.");

        Storyboard ReadStoryboard() => (Storyboard)typeof(ProgramActivity).GetField("_animation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(edge)!;
        void AssertNativePhase(string context)
        {
            var native = typeof(ProgramActivity).GetField("_native", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(edge)!;
            var animations = (IEnumerable<(Windows.UI.Composition.Visual Visual, string Property)>)native.GetType()
                .GetField("_animations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(native)!;
            var (visual, property) = animations.Single();
            var controller = visual.TryGetAnimationController(property) ?? throw new Exception("Native edge has no playback controller.");
            AssertPhase(controller.Progress * 2.4, context);
        }
        void AssertPhase(double actualSeconds, string context)
        {
            var expected = clock.Elapsed.TotalSeconds % 2.4;
            var actual = actualSeconds % 2.4;
            var difference = Math.Abs(actual - expected);
            difference = Math.Min(difference, 2.4 - difference);
            Check(difference < .1, $"{context} reset or drifted. Expected phase {expected:F3}s, observed {actual:F3}s.");
            Console.WriteLine($"{context}: elapsed phase {expected:F3}s, observed playback {actual:F3}s.");
        }
    }

    private static async Task CheckOcclusionAsync(Window window)
    {
        var activity = new ProgramActivity(ProgramActivityKind.TabIcon, () => true) { Working = true };
        var host = new Grid { Width = 20, Height = 20, Children = { activity } };
        var scene = new Grid { Width = 600, Height = 360, Background = new SolidColorBrush(Microsoft.UI.Colors.Black), Children = { host } };
        window.Content = scene;
        window.Activate();
        await Program.WaitAsync(() => activity.IsAnimating);
        await Program.WaitAsync(() => UsesSystemCompositor(activity));
        Check(await ActivityPixelCapture.SampleAsync(window, activity, 4) > 1, "Uncovered indicator must visibly animate.");

        var cover = new Border { Width = 100, Height = 100, Background = new SolidColorBrush(Microsoft.UI.Colors.Blue) };
        scene.Children.Add(cover);
        Check(await ActivityPixelCapture.SampleAsync(window, activity, 4, "activity-occlusion-sibling") == 1,
            "Activity must not draw above an opaque XAML sibling.");
        scene.Children.Remove(cover);
        await Program.WaitAsync(() => UsesSystemCompositor(activity));
        Check(await ActivityPixelCapture.SampleAsync(window, activity, 4) > 1, "Removing the cover must reveal moving activity.");

        host.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 10, 20) };
        Check(await ActivityPixelCapture.SampleAsync(window, activity, 4, "activity-occlusion-clipped",
            new Windows.Foundation.Rect(10, 0, 10, 20)) == 1, "Activity must not escape its ancestor's clip.");
        Check(await ActivityPixelCapture.SampleAsync(window, activity, 4, null,
            new Windows.Foundation.Rect(0, 0, 10, 20)) > 1, "The unclipped half must keep moving.");
        host.Clip = null;

        var dialog = new ContentDialog { XamlRoot = scene.XamlRoot, Title = "Activity occlusion fixture", CloseButtonText = "Close",
            Content = new Border { Width = 400, Height = 200, Background = new SolidColorBrush(Microsoft.UI.Colors.Blue) } };
        var shown = dialog.ShowAsync();
        try
        {
            await Program.WaitAsync(() => VisualTreeHelper.GetOpenPopupsForXamlRoot(scene.XamlRoot).Count > 0);
            await Task.Delay(300);
            Check(await ActivityPixelCapture.SampleAsync(window, activity, 4, "activity-occlusion-dialog") == 1,
                "Activity must not draw above a covering ContentDialog.");
        }
        finally { dialog.Hide(); await shown; }

        var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Full,
            Content = new Border { Width = 400, Height = 200, Background = new SolidColorBrush(Microsoft.UI.Colors.Blue) } };
        var opened = false;
        flyout.Opened += (_, _) => opened = true;
        flyout.ShowAt(activity);
        try
        {
            await Program.WaitAsync(() => opened);
            await Task.Delay(300);
            Check(await ActivityPixelCapture.SampleAsync(window, activity, 4, "activity-occlusion-flyout") == 1,
                "Activity must not draw above a covering flyout.");
        }
        finally { flyout.Hide(); }

        scene.Children.Remove(host);
        var content = new Canvas { Width = 80, Height = 240 };
        Canvas.SetLeft(host, 20); Canvas.SetTop(host, 20);
        content.Children.Add(host);
        var scroll = new ScrollViewer { Width = 80, Height = 80, Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        scene.Children.Add(scroll);
        await Program.WaitAsync(() => scroll.ScrollableHeight > 100 && activity.IsAnimating);
        Check(await ActivityPixelCapture.SampleAsync(window, activity, 4) > 1, "Activity inside the visible viewport must move.");
        scroll.ChangeView(null, 100, null, true);
        await Program.WaitAsync(() => scroll.VerticalOffset == 100);
        Check(await ActivityPixelCapture.SampleAsync(window, activity, 4, "activity-occlusion-scrolled-out") == 1,
            "Activity outside the scroll viewport must not remain visible.");
        scroll.ChangeView(null, 0, null, true);
        await Program.WaitAsync(() => scroll.VerticalOffset == 0 && activity.IsAnimating);
        await Program.WaitAsync(() => UsesSystemCompositor(activity));
        Check(await ActivityPixelCapture.SampleAsync(window, activity, 4) > 1, "Returning to the viewport must restore moving pixels.");
        Console.WriteLine("Activity occlusion passed. Opaque sibling, ancestor clip, ContentDialog, flyout, and scroll viewport preserve XAML stacking.");
    }

    private static async Task CaptureParityAsync(Window window)
    {
        var renderer = "system";
        var label = Environment.GetEnvironmentVariable("AGENTTERM_ACTIVITY_PARITY_LABEL") ??
            renderer;
        var icon = new ProgramActivity(ProgramActivityKind.TabIcon, () => true) { Working = true };
        var narrow = new ProgramActivity(ProgramActivityKind.PaneEdge, () => true) { Working = true, Width = 317 };
        var fractional = new ProgramActivity(ProgramActivityKind.PaneEdge, () => true) { Working = true, Width = 600.75 };
        var canvas = new Canvas { Width = 800, Height = 180 };
        foreach (var (activity, top) in new[] { (icon, 16d), (narrow, 60d), (fractional, 100d) })
        {
            Canvas.SetLeft(activity, 16);
            Canvas.SetTop(activity, top);
            canvas.Children.Add(activity);
        }
        window.Content = canvas;
        window.Activate();
        await Program.WaitAsync(() => icon.IsAnimating && narrow.IsAnimating && fractional.IsAnimating);
        try
        {
            await Program.WaitAsync(() => UsesSystemCompositor(icon) &&
                UsesSystemCompositor(narrow) && UsesSystemCompositor(fractional));
        }
        catch (OperationCanceledException error)
        {
            throw new InvalidOperationException("Activity renderer readiness timed out. " + JsonSerializer.Serialize(new
            {
                requestedRenderer = renderer,
                hasSystemDispatcherQueue = Windows.System.DispatcherQueue.GetForCurrentThread() is not null,
                ownsTestDispatcherQueue = _ownedTestQueue is not null,
                indicators = new[] { icon, narrow, fractional }.Select(DescribeReadiness)
            }), error);
        }
        var captures = new List<object>();
        foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light })
        {
            canvas.RequestedTheme = theme;
            canvas.Background = new SolidColorBrush(theme == ElementTheme.Dark ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
            await Task.Delay(200);
            icon.RefreshPreferences(); narrow.RefreshPreferences(); fractional.RefreshPreferences();
            for (var phase = 0; phase < 4; phase++)
            {
                await FreezeActivityAsync(icon, phase * .3, 1.2);
                await FreezeActivityAsync(narrow, phase * .6, 2.4);
                await FreezeActivityAsync(fractional, phase * .6, 2.4);
                foreach (var (activity, name) in new[] { (icon, "icon"), (narrow, "edge317"), (fractional, "edge600p75") })
                {
                    var artifact = $"activity-parity/{label}/{theme.ToString().ToLowerInvariant()}-{name}-p{phase}";
                    Check(await ActivityPixelCapture.SampleAsync(window, activity, 2, artifact) == 1,
                        "Frozen animation phase must produce stable on-screen pixels.");
                    captures.Add(new { theme = theme.ToString(), name, phase, width = activity.ActualWidth,
                        height = activity.ActualHeight, rasterizationScale = activity.XamlRoot.RasterizationScale,
                        path = Path.GetFullPath(Path.Combine("artifacts", "program-status", artifact + "-0.png")) });
                }
            }
        }
        var destination = Path.GetFullPath(Path.Combine("artifacts", "activity-optimization", "parity", label + ".json"));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, JsonSerializer.Serialize(new { label, captures }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Frozen activity parity captures " + destination);
    }

    private static async Task FreezeActivityAsync(ProgramActivity activity, double seconds, double duration)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Check(UsesSystemCompositor(activity), "Parity capture must use the system renderer, not a silent fallback.");
        var native = typeof(ProgramActivity).GetField("_native", flags)!.GetValue(activity)!;
        var animations = ((IEnumerable<(Windows.UI.Composition.Visual Visual, string Property)>)native.GetType()
            .GetField("_animations", flags)!.GetValue(native)!).ToArray();
        Check(animations.Length > 0, "Parity capture must freeze at least one native animation.");
        var controllers = new List<Windows.UI.Composition.AnimationController>();
        foreach (var (visual, property) in animations)
        {
            var controller = visual.TryGetAnimationController(property) ?? throw new Exception("Missing activity animation controller.");
            controller.Pause();
            // Establish a changed progress value before seeking zero. A newly acquired
            // controller's default zero does not rewind its already-running animation.
            controller.Progress = .99f;
            controllers.Add(controller);
        }
        await animations[0].Visual.Compositor.RequestCommitAsync();
        foreach (var controller in controllers) controller.Progress = (float)(seconds / duration);
        await animations[0].Visual.Compositor.RequestCommitAsync();
    }

    private static bool UsesSystemCompositor(ProgramActivity activity) =>
        typeof(ProgramActivity).GetProperty("UsesSystemCompositor", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(activity) is true;

    private static object DescribeReadiness(ProgramActivity activity)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var native = typeof(ProgramActivity).GetField("_native", flags)?.GetValue(activity);
        var host = native?.GetType().GetField("_host", flags)?.GetValue(native);
        return new
        {
            activity.IsLoaded, activity.ActualWidth, activity.ActualHeight, activity.IsAnimating,
            appWindowId = activity.XamlRoot?.ContentIslandEnvironment.AppWindowId.Value,
            usesSystemCompositor = UsesSystemCompositor(activity), nativeType = native?.GetType().Name,
            nativeReady = native?.GetType().GetProperty("IsReady", flags)?.GetValue(native),
            hostHasCompositor = host?.GetType().GetProperty("Compositor", flags)?.GetValue(host) is not null
        };
    }

    internal static async Task ShutdownTestDispatcherQueueAsync()
    {
        var owned = _ownedTestQueue;
        _ownedTestQueue = null;
        if (owned is not null) await owned.ShutdownQueueAsync();
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
