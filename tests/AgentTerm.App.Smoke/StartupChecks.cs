using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;
using AgentTerm.Agents;
using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

internal static class StartupChecks
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
                if (Environment.GetCommandLineArgs().Contains("--tab-title"))
                {
                    addTab.Invoke(window, [profile]);
                    await AssertOutputAsync((TerminalTab)((TabViewItem)tabs.SelectedItem).Content, report, "title fixture");
                    await AssertTabTitleAsync(window, tabs, report);
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
                        if (Field<CanvasRenderTarget?>(hiddenSurface, "_renderTarget") is not null)
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
                await WaitAsync(() => active.PaneCount == 2, "split pane creation");
                await AssertOutputAsync(active, report, "split pane");
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
            var painted = Field<TerminalFrame?>(surface, "_paintedFrame");
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
            await WaitAsync(() => Field<TerminalFrame?>(surface, "_paintedFrame") is { } frame &&
                FrameHasOutput(frame), context + " painted output");
        }
        catch (TimeoutException)
        {
            var failedCanvas = Field<CanvasControl>(surface, "_canvas");
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(_activeWindow!);
            report.Add($"Window state: hwnd={handle}; foreground={GetForegroundWindow()}; isForeground={GetForegroundWindow() == handle}; iconic={IsIconic(handle)}; visible={IsWindowVisible(handle)}; appWindowVisible={_activeWindow!.AppWindow.IsVisible}.");
            var supplied = Field<TerminalFrame?>(surface, "_frame");
            report.Add($"Painting failure: page loaded={page.IsLoaded}; active={Field<bool>(page, "_viewActive")}; subscribed={Field<bool>(page, "_renderingSubscribed")}; queued={Field<bool>(page, "_frameRefreshQueued")}; canvas loaded={failedCanvas.IsLoaded}; ready={failedCanvas.ReadyToDraw}; suspended={Field<bool>(surface, "_suspended")}; resourcesReady={Field<bool>(surface, "_resourcesReady")}; raster={Field<CanvasRenderTarget?>(surface, "_renderTarget") is not null}; suppliedMarker={supplied is not null && string.Concat(supplied.Cells.Select(cell => cell.Text)).Contains(marker)}; metrics={surface.CellWidth}x{surface.CellHeight}");
            for (DependencyObject? ancestor = failedCanvas; ancestor is not null; ancestor = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(ancestor))
                if (ancestor is FrameworkElement element)
                    report.Add($"Ancestor {element.GetType().Name} {element.Name}: {element.Visibility}, {element.ActualWidth}x{element.ActualHeight}, opacity={element.Opacity}");
            var recovery = new TaskCompletionSource();
            void RecoveryDraw(CanvasControl _, CanvasDrawEventArgs __) => recovery.TrySetResult();
            failedCanvas.Draw += RecoveryDraw;
            failedCanvas.Invalidate();
            await Task.WhenAny(recovery.Task, Task.Delay(2000));
            failedCanvas.Draw -= RecoveryDraw;
            report.Add($"Diagnostic late invalidation drew={recovery.Task.IsCompleted}; raster={Field<CanvasRenderTarget?>(surface, "_renderTarget") is not null}.");
            throw;
        }
        var canvas = Field<CanvasControl>(surface, "_canvas");
        var drawn = new TaskCompletionSource();
        void Draw(CanvasControl sender, CanvasDrawEventArgs args) => drawn.TrySetResult();
        canvas.Draw += Draw;
        try
        {
            canvas.Invalidate();
            await drawn.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { canvas.Draw -= Draw; }
        var raster = Field<CanvasRenderTarget>(surface, "_renderTarget");
        var pixels = raster.GetPixelColors();
        if (pixels.Length == 0 || !pixels.Any(pixel => pixel != pixels[0]))
            throw new Exception(context + ": terminal raster contains no visible text.");
        try { await WaitAsync(() => surface.Input.FocusState != FocusState.Unfocused, context + " terminal focus"); }
        catch (TimeoutException)
        {
            var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(tab.XamlRoot);
            report.Add($"Focus failure: tab loaded={tab.IsLoaded}; page loaded={page.IsLoaded}; active={Field<bool>(page, "_viewActive")}; input active={Field<bool>(page, "_inputActive")}; owner={Field<object>(page, "_inputOwner")}; focused={focused?.GetType().FullName}; focused name={(focused as FrameworkElement)?.Name}");
            throw;
        }
        report.Add($"PASS {context}: shell PID {session.ProcessId}, executed marker, canvas draw, nonblank raster and terminal focus.");
    }

    private static async Task AssertIdleAndWakeAsync(TerminalTab tab, List<string> report)
    {
        var page = tab.ActivePage;
        var surface = (TerminalSurface)page.FindName("Terminal");
        await Task.Delay(750);
        var painted = Field<TerminalFrame?>(surface, "_paintedFrame");
        for (var sample = 0; sample < 3; sample++)
        {
            await Task.Delay(250);
            if (Field<bool>(page, "_renderingSubscribed") || Field<bool>(page, "_frameRefreshQueued") ||
                Field<int>(page, "_outputRefreshQueued") != 0 || !ReferenceEquals(painted, Field<TerminalFrame?>(surface, "_paintedFrame")))
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
        bool Painted(string marker) => Field<TerminalFrame?>(surface, "_paintedFrame") is { } frame &&
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
        if (Field<TerminalFrame>(surface, "_frame").HasSelection != true)
            throw new Exception("Input regression fixture did not create a visible selection.");
        surface.Input.Text = "x";
        if (Field<TerminalFrame>(surface, "_frame").HasSelection)
            throw new Exception("Text input did not immediately refresh selection before process-output callbacks could run.");
        session.Send("\u0003");
        await AssertOutputAsync(tab, report, "input selection clear");
        report.Add("PASS local input updates the selected frame synchronously, without waiting for process echo.");
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
                if (!Field<TerminalFrame>(surface, "_frame").HasSelection)
                    throw new Exception("Mouse regression fixture did not create a visible selection.");
                var point = new Windows.Foundation.Point(surface.CellWidth, surface.Geometry.RowTop(row) + surface.CellHeight / 2);
                if (!(bool)mouse.Invoke(page, [point, TerminalMouseAction.Press, button])! ||
                    Field<TerminalFrame>(surface, "_frame").HasSelection)
                    throw new Exception("Application mouse press did not refresh the cleared selection before process echo.");
            }
        }
        finally { WriteTerminal(session, "\x1b[?1000l\x1b[?1006l"); }
        session.Send("\u0003");
        await AssertOutputAsync(tab, report, "mouse selection clear");
        report.Add("PASS application click and wheel clear selection synchronously, without process echo.");
    }

    private static async Task AssertHiddenCursorIdleAsync(TerminalTab tab, List<string> report)
    {
        var page = tab.ActivePage;
        var surface = (TerminalSurface)page.FindName("Terminal");
        var session = Field<TerminalSession>(page, "_session");
        var canvas = Field<CanvasControl>(surface, "_canvas");
        var draws = 0;
        void Draw(CanvasControl _, CanvasDrawEventArgs __) => draws++;
        WriteTerminal(session, "\x1b[1 q\x1b[?25l");
        typeof(MainPage).GetMethod("RefreshOutput", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [session]);
        try
        {
            await WaitAsync(() => Field<TerminalFrame?>(surface, "_paintedFrame")?.Cursor is { Visible: false, Blink: true },
                "hidden blinking cursor paint");
            await Task.Delay(750);
            if (Field<DispatcherQueueTimer>(page, "_cursorTimer").IsRunning)
                throw new Exception("Hidden cursor kept its blink timer running.");
            canvas.Draw += Draw;
            await Task.Delay(1200);
            if (draws != 0) throw new Exception("Hidden blinking cursor kept drawing an idle terminal.");
        }
        finally
        {
            canvas.Draw -= Draw;
            WriteTerminal(session, "\x1b[?25h\x1b[0 q");
            typeof(MainPage).GetMethod("RefreshOutput", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [session]);
        }
        report.Add("PASS hidden blinking cursor has no blink timer or idle draws.");
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
                Field<CanvasControl?>(surface, "_canvas") is not null || Field<CanvasRenderTarget?>(surface, "_renderTarget") is not null)
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
