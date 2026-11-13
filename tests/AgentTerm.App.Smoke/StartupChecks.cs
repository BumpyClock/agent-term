using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;
using AgentTerm.Agents;
using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

internal static partial class StartupChecks
{
    private static int _result;
    private static MainWindow? _activeWindow;
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    private static readonly string Report = Path.GetFullPath(Environment.GetCommandLineArgs().ElementAtOrDefault(1) ?? @"artifacts\startup-smoke.txt");

    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var app = new SmokeApplication();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
        });
        return _result;
    }

    private sealed class SmokeApplication : App
    {
        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            MainWindow? window = null;
            var report = new List<string>();
            try
            {
                var position = Environment.GetCommandLineArgs().ElementAtOrDefault(2) == "Top" ? TabPosition.Top : TabPosition.Left;
                var settings = Field<object>(TerminalPreferences.Current, "_settings");
                settings.GetType().GetProperty("TabPosition")!.SetValue(settings, position.ToString());
                window = new MainWindow();
                _activeWindow = window;
                var tabs = (TabView)((FrameworkElement)window.Content).FindName("Tabs");
                window.Activate();
                await AssertOutputAsync((TerminalTab)((TabViewItem)tabs.SelectedItem).Content, report, "initial " + position);

                var profile = new ShellProfile("startup-smoke", "Command Prompt",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), "/D /Q", Environment.CurrentDirectory);
                var addTab = typeof(MainWindow).GetMethod("AddTab", BindingFlags.Instance | BindingFlags.NonPublic)!;
                if (Environment.GetCommandLineArgs().Contains("--libghostty"))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Report)!);
                    addTab.Invoke(window, [profile]);
                    var tab = (TerminalTab)((TabViewItem)tabs.SelectedItem).Content;
                    await AssertOutputAsync(tab, report, "libghostty fixture");
                    await AssertLibghosttyAsync(tab, report);
                    return;
                }
                if (Environment.GetCommandLineArgs().Contains("--tab-title"))
                {
                    addTab.Invoke(window, [profile]);
                    await AssertOutputAsync((TerminalTab)((TabViewItem)tabs.SelectedItem).Content, report, "title fixture");
                    await AssertTabTitleAsync(window, tabs, report);
                    return;
                }
                if (Environment.GetCommandLineArgs().Contains("--program-activity"))
                {
                    addTab.Invoke(window, [profile]);
                    await AssertOutputAsync((TerminalTab)((TabViewItem)tabs.SelectedItem).Content, report, "activity fixture");
                    await AssertProgramActivityAsync(window, tabs, addTab, profile, report);
                    return;
                }
                await AssertSynchronizedTimeoutAsync(window, tabs, addTab, report);
                for (var index = 0; index < 3; index++) addTab.Invoke(window, [profile]);
                await Task.Delay(500);
                foreach (var item in tabs.TabItems.OfType<TabViewItem>().ToArray())
                {
                    tabs.SelectedItem = item;
                    await AssertOutputAsync((TerminalTab)item.Content, report, "tab switch");
                    foreach (var hidden in tabs.TabItems.OfType<TabViewItem>().Where(candidate => candidate != item))
                    {
                        var hiddenSurface = (TerminalSurface)((TerminalTab)hidden.Content).ActivePage.FindName("Terminal");
                        if (hiddenSurface.HasRenderResources)
                            throw new Exception("Hidden tab retained its terminal raster.");
                    }
                }
                await AssertIdleAndWakeAsync((TerminalTab)((TabViewItem)tabs.SelectedItem).Content, report);
                await AssertInputClearsSelectionAsync((TerminalTab)((TabViewItem)tabs.SelectedItem).Content, report);
                await AssertMouseClearsSelectionAsync((TerminalTab)((TabViewItem)tabs.SelectedItem).Content, report);
                await AssertHiddenCursorIdleAsync((TerminalTab)((TabViewItem)tabs.SelectedItem).Content, report);
                await AssertCoveredTabAsync(tabs, report);
                AssertStartupInput((TerminalTab)((TabViewItem)tabs.SelectedItem).Content, report);

                var splitter = (Style)((FrameworkElement)window.Content).Resources["PaneSplitterStyle"];
                await using (var detached = new TerminalTab(profile, splitter))
                {
                    detached.SetVisible(true);
                    detached.FocusActivePane();
                    await Task.Delay(100);
                }
                report.Add("PASS focusing a detached tab is a no-op.");

                var active = (TerminalTab)((TabViewItem)tabs.SelectedItem).Content;
                active.Split(sideBySide: true);
                await WaitAsync(() => active.CanSplit(sideBySide: false), "second split layout");
                active.Split(sideBySide: false);
                await WaitAsync(() => active.PaneCount == 3, "rapid split pane creation");
                await AssertOutputAsync(active, report, "rapid split pane");
                await active.CloseActivePaneAsync();
                await WaitAsync(() => active.PaneCount == 2, "rapid split pane close");
                await AssertOutputAsync(active, report, "split pane");
                await AssertIndependentPaneSchedulingAsync(active, report);
                await AssertRasterWorkerIsolationAsync(active, report);
                await AssertInputDoesNotWaitForCaptureAsync(active, report);
                await AssertQueueSaturationKeepsSessionAsync(active, report);
                await active.CloseActivePaneAsync();
                await AssertOutputAsync(active, report, "surviving pane");
                report.Add("PASS rapid tab creation, switching, split and pane close.");

                var root = (FrameworkElement)window.Content;
                var avatar = (FrameworkElement)root.FindName("GitHubButton");
                var spacer = Field<FrameworkElement>(window, "_captionSpacer");
                foreach (var width in new[] { 520, 1040 })
                {
                    window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 760));
                    await Task.Delay(500);
                    var right = avatar.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(avatar.ActualWidth, 0)).X;
                    if (avatar.Visibility == Visibility.Visible && right > root.ActualWidth - spacer.Width + 0.5)
                        throw new Exception("GitHub avatar overlaps the caption buttons after resize.");
                    await AssertOutputAsync(active, report, "resize " + width);
                }
            }
            catch (Exception error)
            {
                report.Add(error.ToString());
                _result = 1;
            }
            finally
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Report)!);
                File.WriteAllLines(Report, report);
                if (window is not null)
                {
                    var tabs = (TabView)((FrameworkElement)window.Content).FindName("Tabs");
                    foreach (var item in tabs.TabItems.OfType<TabViewItem>())
                        await ((TerminalTab)item.Content).DisposeAsync();
                    var closed = new TaskCompletionSource();
                    window.Closed += (_, _) => closed.TrySetResult();
                    window.Close();
                    await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Report)!);
                File.WriteAllLines(Report, report);
                Exit();
            }
        }
    }

    private static async Task AssertTabTitleAsync(MainWindow window, TabView tabs, List<string> report)
    {
        var item = (TabViewItem)tabs.SelectedItem;
        var tab = (TerminalTab)item.Content;
        var page = tab.ActivePage;
        void Emit(string value) => typeof(TerminalSession).GetMethod("ProcessOutput", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Field<TerminalSession>(page, "_session"), [new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(value))]);
        string Header() => item.Header as string ?? "";
        Emit("\u001b]0;" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") + "\a");
        await Task.Delay(600);
        if (Header() != "Command Prompt") throw new Exception($"An executable-path console title replaced the shell name: <{Header()}>.");
        Emit("\u001b]2;⠂ Fix the tab title\u001b\\");
        await WaitAsync(() => Header() == "Fix the tab title", "program title in tab header");
        if (window.Title != "Fix the tab title - AgentTerm") throw new Exception($"Window title did not follow the tab: <{window.Title}>.");
        Emit("\u001b]0;✳ Claude Code\a");
        await WaitAsync(() => Header() == "Claude Code", "spinner prefix removed from title");
        Emit("\u001b]0;\a");
        await WaitAsync(() => Header() == "Command Prompt" && window.Title == "Command Prompt - AgentTerm", "empty title restores shell name");
        await AssertOutputAsync(tab, report, "title survivor");
        report.Add("PASS OSC 0/2 titles drive tab header and window title, spinner prefixes trimmed, executable-path and empty titles fall back to the shell name.");
    }

    private static async Task AssertProgramActivityAsync(MainWindow window, TabView tabs, MethodInfo addTab, ShellProfile profile, List<string> report)
    {
        var item = (TabViewItem)tabs.SelectedItem;
        var tab = (TerminalTab)item.Content;
        var firstPage = tab.ActivePage;
        var first = Descendants(tab).OfType<TerminalPane>().Single();
        var firstEdge = Field<ProgramActivity>(first, "_activity");
        var motion = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled && !new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        var artifactPrefix = "production-activity-" + Path.GetFileNameWithoutExtension(Report);
        ProgramActivity VisibleTabActivity() => Descendants((DependencyObject)window.Content).OfType<ProgramStatusIcon>()
            .Where(icon => ReferenceEquals(icon.Tab, item)).SelectMany(Descendants).OfType<ProgramActivity>()
            .First(activity => activity.IsAnimating && activity.ActualWidth > 0 && activity.ActualHeight > 0);
        async Task AssertMovingPixels(ProgramActivity activity, string context, int samples = 4)
        {
            if (!motion) return;
            bool Native() => typeof(ProgramActivity).GetProperty("UsesSystemCompositor", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(activity) is true;
            await WaitAsync(Native, context + " native renderer");
            var frames = await ActivityPixelCapture.SampleAsync(window, activity, samples, artifactPrefix + "-" + context);
            if (frames <= 1) throw new Exception($"Production {context} reports activity but its on-screen pixels do not move.");
            if (!Native()) throw new Exception($"Production {context} fell back during native pixel validation.");
            report.Add($"PASS production {context}: {frames} distinct on-screen frames.");
        }
        void Emit(MainPage page, string value)
        {
            var bytes = Encoding.UTF8.GetBytes("\u001b]7501;" + value + "\u001b\\");
            typeof(TerminalSession).GetMethod("ProcessOutput", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Field<TerminalSession>(page, "_session"), [new ReadOnlyMemory<byte>(bytes)]);
        }
        Emit(firstPage, "app=pi:state=working:progress=42");
        await WaitAsync(() => firstEdge.Working && firstEdge.IsAnimating == motion, "session-reported Pi activity");
        await WaitAsync(() => Descendants((DependencyObject)window.Content).OfType<ProgramStatusIcon>()
            .Where(icon => ReferenceEquals(icon.Tab, item)).Any(icon => Descendants(icon).OfType<ProgramActivity>().Any(activity => activity.IsAnimating == motion)), "visible tab activity");
        if (motion) await AssertMovingPixels(VisibleTabActivity(), "selected-tab", 6);
        await AssertMovingPixels(firstEdge, "single-pane-edge");
        tab.Split(sideBySide: true);
        await WaitAsync(() => tab.PaneCount == 2, "activity split creation");
        await AssertOutputAsync(tab, report, "activity second pane");
        var secondPage = tab.ActivePage;
        var second = Descendants(tab).OfType<TerminalPane>().Single(pane => !ReferenceEquals(pane, first));
        var secondEdge = Field<ProgramActivity>(second, "_activity");
        if (!firstEdge.Working || secondEdge.Working) throw new Exception("A new pane borrowed another pane's program activity.");
        Emit(secondPage, "app=claude:state=working:progress=62");
        await WaitAsync(() => secondEdge.Working && secondEdge.IsAnimating == motion, "second pane Claude activity");
        await AssertMovingPixels(firstEdge, "unfocused-split-edge");
        await AssertMovingPixels(secondEdge, "focused-split-edge");
        Emit(firstPage, "app=pi:state=blocked:kind=permission");
        await WaitAsync(() => !firstEdge.Working && secondEdge.Working && ((TabProgramStatus)item.Tag).Primary?.State == ProgramState.Blocked, "blocked priority across panes");
        if (Descendants((DependencyObject)window.Content).OfType<ProgramStatusIcon>().Where(icon => ReferenceEquals(icon.Tab, item))
            .SelectMany(Descendants).OfType<ProgramActivity>().Any(activity => activity.IsAnimating))
            throw new Exception("A blocked tab kept its working loader.");
        Emit(firstPage, "state=clear");
        await WaitAsync(() => ((TabProgramStatus)item.Tag).Primary?.EffectiveApp == "claude", "remaining working program");
        addTab.Invoke(window, [profile]);
        await WaitAsync(() => !secondEdge.IsAnimating && !secondEdge.Active, "hidden pane animation cleanup");
        if (motion)
        {
            await WaitAsync(() => Descendants((DependencyObject)window.Content).OfType<ProgramStatusIcon>()
                .Where(icon => ReferenceEquals(icon.Tab, item)).SelectMany(Descendants).OfType<ProgramActivity>().Any(activity => activity.IsAnimating),
                "inactive tab indicator remains visible");
            await AssertMovingPixels(VisibleTabActivity(), "inactive-tab", 6);
        }
        tabs.SelectedItem = item;
        await WaitAsync(() => secondEdge.Active && secondEdge.IsAnimating == motion, "shown pane animation restart");
        Emit(secondPage, "app=claude:state=done");
        await WaitAsync(() => !secondEdge.Working && !secondEdge.IsAnimating, "completed pane animation cleanup");
        if (await ActivityPixelCapture.SampleAsync(window, secondEdge, 3, artifactPrefix + "-completed-edge") != 1)
            throw new Exception("Completed pane edge still has changing on-screen pixels.");
        if (Descendants(first).Concat(Descendants(second)).OfType<TextBlock>().Any(text => text.Text == "Agent working" && text.Visibility == Visibility.Visible))
            throw new Exception("A pane header still contains the removed working label.");
        await AssertOutputAsync(tab, report, "activity survivor");
        report.Add("PASS session-fed OSC activity in production tab layouts, independent split-pane comets, blocked priority, hidden-tab cleanup, completion and preserved shell input. Motion=" + motion);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static async Task AssertOutputAsync(TerminalTab tab, List<string> report, string context)
    {
        var page = tab.ActivePage;
        var surface = (TerminalSurface)page.FindName("Terminal");
        await WaitAsync(() => Field<TerminalSession?>(page, "_session") is { } session && !session.Completion.IsCompleted &&
            !string.IsNullOrWhiteSpace(string.Concat(session.ReadFrame().Cells.Select(cell => cell.Text))), context + " shell output");
        var session = Field<TerminalSession>(page, "_session");
        var marker = "SMK" + Guid.NewGuid().ToString("N")[..8];
        bool OutputLine(string text) => text.TrimStart().StartsWith(marker, StringComparison.Ordinal);
        bool FrameHasOutput(TerminalFrame frame) => frame.Cells.Chunk(frame.Columns)
            .Any(row => OutputLine(string.Concat(row.Select(cell => cell.Text))));
        var command = "echo " + marker + "\r";
        session.Send(command);
        try
        {
            await WaitAsync(() =>
            {
                var frame = session.ReadFrame();
                return FrameHasOutput(frame) && session.ReadText().Split('\n').Any(OutputLine);
            }, context + " command execution");
        }
        catch (TimeoutException)
        {
            var frame = session.ReadFrame();
            var text = session.ReadText();
            var diagnosticPath = Report + "." + context.Replace(' ', '-') + ".command-timeout.json";
            var supplied = Field<TerminalFrame?>(surface, "_frame");
            var painted = surface.PresentedFrame;
            var processExited = true;
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(session.ProcessId);
                processExited = process.HasExited;
            }
            catch (ArgumentException) { }
            Directory.CreateDirectory(Path.GetDirectoryName(Report)!);
            File.WriteAllText(diagnosticPath, JsonSerializer.Serialize(new
            {
                context, command, marker, processId = session.ProcessId, processExited,
                completion = session.Completion.Status.ToString(), prompt = session.PromptState.ToString(),
                pageState = Field<object>(page, "_state").ToString(), page.IsLoaded,
                viewActive = Field<bool>(page, "_viewActive"), inputActive = Field<bool>(page, "_inputActive"),
                inputText = surface.Input.Text, inputFocus = surface.Input.FocusState.ToString(),
                frame.Columns, frame.Rows, frame.Viewport, frame.Cursor, text,
                frameRows = frame.Cells.Chunk(frame.Columns).Select((row, index) => new { index, text = string.Concat(row.Select(cell => cell.Text)) }),
                suppliedRows = supplied?.Cells.Chunk(supplied.Columns).Select((row, index) => new { index, text = string.Concat(row.Select(cell => cell.Text)) }),
                paintedRows = painted?.Cells.Chunk(painted.Columns).Select((row, index) => new { index, text = string.Concat(row.Select(cell => cell.Text)) })
            }, new JsonSerializerOptions { WriteIndented = true }));
            report.Add($"Command execution timeout diagnostic: {diagnosticPath}; processExited={processExited}; nativeTextContainsMarker={text.Contains(marker)}.");
            throw;
        }
        try
        {
            await WaitAsync(() => surface.PresentedFrame is { } frame &&
                FrameHasOutput(frame), context + " presented output");
        }
        catch (TimeoutException)
        {
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(_activeWindow!);
            report.Add($"Window state: hwnd={handle}; foreground={GetForegroundWindow()}; isForeground={GetForegroundWindow() == handle}; iconic={IsIconic(handle)}; visible={IsWindowVisible(handle)}; appWindowVisible={_activeWindow!.AppWindow.IsVisible}.");
            var supplied = Field<TerminalFrame?>(surface, "_frame");
            report.Add($"Painting failure: page loaded={page.IsLoaded}; active={Field<bool>(page, "_viewActive")}; subscribed={Field<bool>(page, "_renderingSubscribed")}; queued={Field<bool>(page, "_frameRefreshQueued")}; surface loaded={surface.IsLoaded}; suspended={Field<bool>(surface, "_suspended")}; resourcesReady={Field<bool>(surface, "_resourcesReady")}; raster={surface.HasRenderResources}; presentedStats={surface.PresentedRasterStats}; suppliedMarker={supplied is not null && string.Concat(supplied.Cells.Select(cell => cell.Text)).Contains(marker)}; metrics={surface.CellWidth}x{surface.CellHeight}");
            for (DependencyObject? ancestor = surface; ancestor is not null; ancestor = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(ancestor))
                if (ancestor is FrameworkElement element)
                    report.Add($"Ancestor {element.GetType().Name} {element.Name}: {element.Visibility}, {element.ActualWidth}x{element.ActualHeight}, opacity={element.Opacity}");
            throw;
        }
        var raster = await surface.ReadPresentedRasterAsync();
        if (!FrameHasOutput(raster.Request.Frame))
            throw new Exception(context + ": raster readback frame does not contain the executed output.");
        var pixels = raster.Pixels;
        if (pixels.Length < 8 || !Enumerable.Range(1, pixels.Length / 4 - 1).Any(index => !pixels.AsSpan(index * 4, 4).SequenceEqual(pixels.AsSpan(0, 4))))
            throw new Exception(context + ": presented terminal raster contains no visible text.");
        try { await WaitAsync(() => surface.Input.FocusState != FocusState.Unfocused, context + " terminal focus"); }
        catch (TimeoutException)
        {
            var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(tab.XamlRoot);
            report.Add($"Focus failure: tab loaded={tab.IsLoaded}; page loaded={page.IsLoaded}; active={Field<bool>(page, "_viewActive")}; input active={Field<bool>(page, "_inputActive")}; owner={Field<object>(page, "_inputOwner")}; focused={focused?.GetType().FullName}; focused name={(focused as FrameworkElement)?.Name}");
            throw;
        }
        report.Add($"PASS {context}: shell PID {session.ProcessId}, executed marker, presented frame, nonblank presented raster and terminal focus.");
    }

    private static async Task AssertQueueSaturationKeepsSessionAsync(TerminalTab tab, List<string> report)
    {
        var page = tab.ActivePage;
        var session = Field<TerminalSession>(page, "_session");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocked = session.DispatchAsync(_ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Queue saturation fixture was not released.");
        });
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Queue saturation fixture did not start.");
            var accepted = Enumerable.Range(0, 1024).Select(_ => session.DispatchAsync(_ => { })).ToArray();
            typeof(MainPage).GetMethod("ForwardKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page,
                [session, new TerminalKeyboardInput.KeyEvent(0, TerminalModifiers.None, TerminalKeyAction.Press, "x")]);
            if (!ReferenceEquals(session, Field<TerminalSession>(page, "_session")) ||
                typeof(MainPage).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!.ToString() != "Running")
                throw new Exception("Queue saturation terminated the shell session.");
            release.Set();
            await blocked;
            await Task.WhenAll(accepted).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.Set(); }
        await AssertOutputAsync(tab, report, "queue saturation recovery");
        report.Add("PASS rejected input reports queue saturation without terminating the shell.");
    }

    private static async Task AssertInputDoesNotWaitForCaptureAsync(TerminalTab tab, List<string> report)
    {
        var page = tab.ActivePage;
        var surface = (TerminalSurface)page.FindName("Terminal");
        var session = Field<TerminalSession>(page, "_session");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = session.DispatchAsync(owner =>
        {
            lock (Field<object>(owner, "_renderGate"))
            {
                entered.SetResult();
                return release.Wait(TimeSpan.FromSeconds(5));
            }
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // A leading space is accepted input without leaving a partial command to cancel in cmd.exe.
        try { surface.Input.Text = " "; }
        finally { release.Set(); }
        if (!await held)
            throw new Exception("Typing waited for the render lock instead of enqueueing input on the pane worker.");
        await session.DispatchAsync(_ => { });
        await AssertOutputAsync(tab, report, "input after held capture");
        report.Add("PASS text input returns while the pane render lock is held; accepted input and subsequent shell commands retain ordering.");
    }

    private static async Task AssertIndependentPaneSchedulingAsync(TerminalTab tab, List<string> report)
    {
        var inputPage = tab.ActivePage;
        var outputPage = Descendants(tab).OfType<MainPage>().Single(page => page != inputPage);
        var outputSession = Field<TerminalSession>(outputPage, "_session");
        static bool Idle(MainPage page) => !Field<bool>(page, "_renderingSubscribed") &&
            !Field<bool>(page, "_frameRefreshQueued") && !Field<bool>(page, "_frameRefreshPending") &&
            !Field<bool>(page, "_captureInFlight") && !Field<bool>(page, "_captureRequested") &&
            Field<int>(page, "_outputRefreshQueued") == 0 &&
            !Field<DispatcherQueueTimer>(page, "_synchronizedRefreshTimer").IsRunning;
        TerminalFrame? stableOutput = null, stableInput = null;
        var stableSince = System.Diagnostics.Stopwatch.GetTimestamp();
        await WaitAsync(() =>
        {
            var output = Field<TerminalFrame>(outputPage, "_presentedFrame");
            var input = Field<TerminalFrame>(inputPage, "_presentedFrame");
            if (!Idle(outputPage) || !Idle(inputPage) ||
                !ReferenceEquals(output, stableOutput) || !ReferenceEquals(input, stableInput))
            {
                stableOutput = output;
                stableInput = input;
                stableSince = System.Diagnostics.Stopwatch.GetTimestamp();
                return false;
            }
            return System.Diagnostics.Stopwatch.GetElapsedTime(stableSince).TotalMilliseconds >= 250;
        }, "split panes keeping stable frames and empty frame queues for 250 ms before output burst");
        var outputBefore = Field<TerminalFrame>(outputPage, "_presentedFrame");
        var inputBefore = Field<TerminalFrame>(inputPage, "_presentedFrame");
        var emit = typeof(TerminalSession).GetMethod("ProcessOutput", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var marker = "PANE_BURST_" + Guid.NewGuid().ToString("N")[..8];
        for (var index = 0; index < 128; index++)
            emit.Invoke(outputSession, [new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes($"\x1b[H{marker}_{index:D3}"))]);
        var inputTurn = new TaskCompletionSource<bool>();
        inputPage.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            inputTurn.SetResult(ReferenceEquals(outputBefore, Field<TerminalFrame>(outputPage, "_presentedFrame"))));
        if (!await inputTurn.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            throw new Exception("A busy pane refreshed ahead of normal-priority input work queued after its output burst.");
        await WaitAsync(() => string.Concat(Field<TerminalFrame>(outputPage, "_presentedFrame").Cells.Select(cell => cell.Text))
            .Contains(marker + "_127"), "final output burst snapshot");
        var inputAfter = Field<TerminalFrame>(inputPage, "_presentedFrame");
        if (!ReferenceEquals(inputBefore, inputAfter))
        {
            static string Describe(TerminalFrame frame) => $"ContentVersion={frame.ContentVersion}; dimensions={frame.Columns}x{frame.Rows}; viewport={frame.Viewport}; cursor={frame.Cursor}; selection={frame.HasSelection}";
            throw new Exception($"Output in one pane replaced the independent idle pane's frame. Before [{Describe(inputBefore)}]. After [{Describe(inputAfter)}]. " +
                $"Input pane flags: capture={Field<bool>(inputPage, "_captureInFlight")}; requested={Field<bool>(inputPage, "_captureRequested")}; " +
                $"queued={Field<bool>(inputPage, "_frameRefreshQueued")}; pending={Field<bool>(inputPage, "_frameRefreshPending")}; output={Field<int>(inputPage, "_outputRefreshQueued")}.");
        }
        await AssertOutputAsync(tab, report, "input beside busy pane");
        report.Add("PASS split-pane output burst yields to queued input work, retains the final update, and leaves the other pane's frame unchanged.");
    }

    private static async Task AssertIdleAndWakeAsync(TerminalTab tab, List<string> report)
    {
        var page = tab.ActivePage;
        var surface = (TerminalSurface)page.FindName("Terminal");
        await Task.Delay(750);
        var painted = surface.PresentedFrame;
        for (var sample = 0; sample < 3; sample++)
        {
            await Task.Delay(250);
            if (Field<bool>(page, "_renderingSubscribed") || Field<bool>(page, "_frameRefreshQueued") ||
                Field<int>(page, "_outputRefreshQueued") != 0 || !ReferenceEquals(painted, surface.PresentedFrame))
                throw new Exception("Idle pane continued scheduling terminal frame refreshes.");
        }
        await AssertOutputAsync(tab, report, "output after idle");
        report.Add("PASS idle pane releases its composition subscription and wakes on new output.");
    }

    private static async Task AssertSynchronizedTimeoutAsync(MainWindow window, TabView tabs, MethodInfo addTab, List<string> report)
    {
        var ready = "SYNC_READY_" + Guid.NewGuid().ToString("N")[..8];
        var held = "SYNC_HELD_" + Guid.NewGuid().ToString("N")[..8];
        var commandPath = Path.Combine(Path.GetDirectoryName(Report)!, "synchronized-output.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(commandPath)!);
        File.WriteAllText(commandPath, $"@echo off\r\necho {ready}\r\nping -n 3 127.0.0.1 >nul\r\necho \x1b[?2026h{held}\r\nping -n 9 127.0.0.1 >nul\r\n");
        var profile = new ShellProfile("synchronized-smoke", "Synchronized output",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), "/D /Q /C \"\"" + commandPath + "\"\"", Environment.CurrentDirectory);
        addTab.Invoke(window, [profile]);
        var tab = (TerminalTab)((TabViewItem)tabs.SelectedItem).Content;
        var surface = (TerminalSurface)tab.ActivePage.FindName("Terminal");
        bool Painted(string marker) => surface.PresentedFrame is { } frame &&
            string.Concat(frame.Cells.Select(cell => cell.Text)).Contains(marker);
        try
        {
            await WaitAsync(() => Painted(ready), "synchronized-output initial paint");
            await WaitAsync(() => Field<DispatcherQueueTimer>(tab.ActivePage, "_synchronizedRefreshTimer").IsRunning,
                "synchronized-output pending hold");
            var started = Environment.TickCount64;
            await WaitAsync(() => Painted(held), "silent synchronized-output timeout paint");
            if (Environment.TickCount64 - started > 3000)
                throw new Exception("Synchronized output was not painted promptly after its hold expired.");
            report.Add("PASS synchronized output paints after its timeout without another byte or snapshot polling.");
        }
        finally
        {
            var closeTab = typeof(MainWindow).GetMethod("CloseTabAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)closeTab.Invoke(window, [tabs.SelectedItem, false])!;
            File.Delete(commandPath);
        }
    }

    private static async Task AssertInputClearsSelectionAsync(TerminalTab tab, List<string> report)
    {
        var page = tab.ActivePage;
        var surface = (TerminalSurface)page.FindName("Terminal");
        var session = Field<TerminalSession>(page, "_session");
        var frame = session.ReadFrame();
        var row = Math.Max(frame.Viewport.TopInset, frame.Cursor.Row);
        session.BeginSelection(0, row);
        session.ExtendSelection(3, row);
        session.EndSelection();
        typeof(MainPage).GetMethod("RefreshOutput", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [session]);
        await WaitAsync(() => Field<TerminalFrame>(surface, "_frame").HasSelection, "visible selection before keyboard input");
        surface.Input.Text = "x";
        if ((await session.CaptureFrameAsync()).Frame.HasSelection)
            throw new Exception("Ordered keyboard input did not clear the selection.");
        await WaitAsync(() => !Field<TerminalFrame>(surface, "_frame").HasSelection, "keyboard input clears the presented selection");
        session.Send("\u0003");
        await AssertOutputAsync(tab, report, "input selection clear");
        report.Add("PASS local input requests a new selection frame through the pane worker.");
    }

    private static void WriteTerminal(TerminalSession session, string text)
    {
        lock (Field<object>(session, "_gate"))
            Field<GhosttyTerminal>(session, "_terminal").Write(Encoding.UTF8.GetBytes(text));
    }

    private static async Task AssertMouseClearsSelectionAsync(TerminalTab tab, List<string> report)
    {
        var page = tab.ActivePage;
        var surface = (TerminalSurface)page.FindName("Terminal");
        var session = Field<TerminalSession>(page, "_session");
        var refresh = typeof(MainPage).GetMethod("RefreshOutput", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var mouse = typeof(MainPage).GetMethod("SendApplicationMouse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        WriteTerminal(session, "\x1b[?1000h\x1b[?1006h");
        try
        {
            foreach (var button in new[] { TerminalMouseButton.Left, TerminalMouseButton.WheelDown })
            {
                var row = Math.Max(session.ReadFrame().Viewport.TopInset, session.ReadFrame().Cursor.Row);
                session.BeginSelection(0, row);
                session.ExtendSelection(3, row);
                session.EndSelection();
                refresh.Invoke(page, [session]);
                await WaitAsync(() => Field<TerminalFrame>(surface, "_frame").HasSelection, "visible selection before application mouse input");
                var point = new Windows.Foundation.Point(surface.CellWidth, surface.Geometry.RowTop(row) + surface.CellHeight / 2);
                if (!(bool)mouse.Invoke(page, [point, TerminalMouseAction.Press, button, null])!)
                    throw new Exception("Application mouse press was not routed to the tracking program.");
                if ((await session.CaptureFrameAsync()).Frame.HasSelection)
                    throw new Exception("Ordered application mouse input did not clear the selection.");
                await WaitAsync(() => !Field<TerminalFrame>(surface, "_frame").HasSelection, "application mouse clears the presented selection");
            }
        }
        finally { WriteTerminal(session, "\x1b[?1000l\x1b[?1006l"); }
        session.Send("\u0003");
        await AssertOutputAsync(tab, report, "mouse selection clear");
        report.Add("PASS application click and wheel request updated selection frames through the pane worker.");
    }

    private static async Task AssertHiddenCursorIdleAsync(TerminalTab tab, List<string> report)
    {
        var page = tab.ActivePage;
        var surface = (TerminalSurface)page.FindName("Terminal");
        var session = Field<TerminalSession>(page, "_session");
        var draws = 0;
        void Draw(double _) => draws++;
        WriteTerminal(session, "\x1b[1 q\x1b[?25l");
        typeof(MainPage).GetMethod("RefreshOutput", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [session]);
        try
        {
            await WaitAsync(() => surface.PresentedFrame?.Cursor is { Visible: false, Blink: true },
                "hidden blinking cursor paint");
            await Task.Delay(750);
            if (Field<DispatcherQueueTimer>(page, "_cursorTimer").IsRunning)
                throw new Exception("Hidden cursor kept its blink timer running.");
            surface.DrawCompleted += Draw;
            await Task.Delay(1200);
            if (draws != 0) throw new Exception("Hidden blinking cursor kept drawing an idle terminal.");
            WriteTerminal(session, "\x1b7\x1b[1;1H\x1b[5mBLINK\x1b[0m\x1b8");
            typeof(MainPage).GetMethod("RefreshOutput", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [session]);
            await WaitAsync(() => surface.HasBlinkingText && Field<DispatcherQueueTimer>(page, "_cursorTimer").IsRunning,
                "worker-reported blinking text starts the timer with the cursor hidden");
            var blinkDraws = draws;
            await WaitAsync(() => draws > blinkDraws, "blinking text publishes another raster without new output");
            WriteTerminal(session, "\x1b7\x1b[1;1H     \x1b8");
            typeof(MainPage).GetMethod("RefreshOutput", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [session]);
            await WaitAsync(() => !surface.HasBlinkingText && !Field<DispatcherQueueTimer>(page, "_cursorTimer").IsRunning,
                "removing the last blinking text stops the timer");
        }
        finally
        {
            surface.DrawCompleted -= Draw;
            WriteTerminal(session, "\x1b[?25h\x1b[0 q");
            typeof(MainPage).GetMethod("RefreshOutput", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [session]);
        }
        report.Add("PASS hidden cursor has no idle draws; worker-reported blinking text starts and stops its timer.");
    }

    private static async Task AssertCoveredTabAsync(TabView tabs, List<string> report)
    {
        var item = (TabViewItem)tabs.SelectedItem;
        var tab = (TerminalTab)item.Content;
        var page = tab.ActivePage;
        var surface = (TerminalSurface)page.FindName("Terminal");
        var task = Field<AgentSessionController>(page, "_agent").BeginTask();
        typeof(MainPage).GetMethod("OpenSurface", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [task, true]);
        try
        {
            tabs.SelectedItem = tabs.TabItems.OfType<TabViewItem>().First(other => other != item);
            await Task.Delay(100);
            tabs.SelectedItem = item;
            await WaitAsync(() => page.IsLoaded && Field<bool>(page, "_viewActive"), "covered tab activation");
            await Task.Delay(250);
            if (surface.Visibility != Visibility.Collapsed || !Field<bool>(surface, "_suspended") ||
                surface.HasRenderResources || Field<object?>(surface, "_cursorCanvas") is not null)
                throw new Exception("Covered tab reactivated hidden terminal rendering resources.");
        }
        finally { page.TryLeaveAgentSurface(); }
        await AssertOutputAsync(tab, report, "terminal after covered tab");
        report.Add("PASS switching to a covered tab retains no terminal canvas and leaving the agent surface repaints.");
    }

    private static void AssertStartupInput(TerminalTab tab, List<string> report)
    {
        var page = tab.ActivePage;
        var surface = (TerminalSurface)page.FindName("Terminal");
        var state = Field<object>(page, "_state");
        var setState = typeof(MainPage).GetMethod("SetState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            setState.Invoke(page, [Enum.Parse(state.GetType(), "Starting")]);
            if (!surface.IsEnabled || !surface.Input.IsReadOnly || surface.Input.FocusState == FocusState.Unfocused)
                throw new Exception("Starting terminal must retain focus while preventing text editing.");
        }
        finally
        {
            setState.Invoke(page, [state]);
            typeof(MainPage).GetMethod("StartRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
        }
        if (surface.Input.IsReadOnly) throw new Exception("Running terminal remained read-only after startup.");
        report.Add("PASS startup input retains focus without permitting text editing until Running.");
    }

    private static async Task WaitAsync(Func<bool> ready, string condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { while (!ready()) await Task.Delay(25, timeout.Token); }
        catch (OperationCanceledException) { throw new TimeoutException("Timed out waiting for " + condition + "."); }
    }
}
