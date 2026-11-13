using System.Reflection;
using System.Text;
using AgentTerm.Terminal;
using AgentTerm_App;
using AgentTerm_App.Effects;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = System.IO.Path;

internal static partial class StartupChecks
{
    private static async Task AssertLibghosttyAsync(TerminalTab tab, List<string> report)
    {
        KeyboardAutomationChecks.Run();
        report.Add("PASS UI Automation reads Unicode text and routes native selection.");
        var page = tab.ActivePage;
        var surface = (TerminalSurface)page.FindName("Terminal");
        var session = Field<TerminalSession>(page, "_session");
        void Invoke(string name, params object?[] values) => typeof(MainPage).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, values);
        void Emit(string text) => typeof(TerminalSession).GetMethod("ProcessOutput", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(text))]);
        TerminalFrame Presented() => Field<TerminalFrame>(page, "_presentedFrame");
        void Refresh() => Invoke("RefreshOutput", session);
        session.SetBottomAnchored(false);
        session.ConfigureEffects(new TerminalEffectOptions { Notifications = NotificationStyle.InApp, ClipboardWrite = ClipboardWritePolicy.Ask });
        Emit("\u001bc" + string.Concat(Enumerable.Range(0, Presented().Rows + 80).Select(i => $"history-{i:D3}\r\n")) +
            "\x1b[2J\x1b[H\x1b[4:3;58;2;255;0;0;53mdecorated\x1b[0m\r\n" +
            "\x1b[31m\x1b]8;;https://example.com/fixture\x1b\\link\x1b]8;;\x1b\\\x1b[0m\r\n");
        var pixels = Convert.ToBase64String(Enumerable.Range(0, 16).SelectMany(_ => new byte[] { 240, 100, 20, 255 }).ToArray());
        Emit($"\x1b_Ga=T,f=32,s=4,v=4,c=4,r=2,i=991,q=2;{pixels}\x1b\\");
        Refresh();
        await WaitAsync(() => Field<TerminalFrame?>(surface, "_paintedFrame")?.Images.IsEmpty == false, "Kitty image in the live WinUI raster");
        var frame = Presented();
        if (!frame.Cells.Any(cell => cell.Overline && cell.UnderlineStyle == TerminalUnderlineStyle.Curly))
            throw new Exception("Decorations were not retained by the real terminal surface.");
        var raster = Field<CanvasRenderTarget>(surface, "_renderTarget");
        if (!raster.GetPixelColors().Any(c => c.R > 220 && c.G is > 75 and < 125 && c.B < 40))
            throw new Exception("The inline image did not reach the WinUI raster.");
        await raster.SaveAsync(Path.ChangeExtension(Report, ".png"), CanvasBitmapFileFormat.Png);
        report.Add("PASS actual WinUI raster contains native Kitty pixels and full text decorations.");

        var link = session.ReadLinkAt(0, 1, frame) ?? throw new Exception("Displayed OSC 8 link is missing.");
        Invoke("ApplyLinkHover", link, true, frame, 0, 1, false);
        var lines = Field<List<Rectangle>>(page, "_linkUnderlines");
        var before = ((SolidColorBrush)lines[0].Fill).Color;
        Emit("\x1b[2;1H\x1b[32m\x1b]8;;https://example.com/fixture\x1b\\link\x1b]8;;\x1b\\\x1b[0m");
        Refresh();
        frame = Presented();
        link = session.ReadLinkAt(0, 1, frame) ?? throw new Exception("Updated link is missing.");
        Invoke("ApplyLinkHover", link, true, frame, 0, 1, false);
        if (((SolidColorBrush)lines[0].Fill).Color == before) throw new Exception("Hover underline kept the old foreground.");
        Invoke("ClearLinkHover");
        report.Add("PASS real hyperlink overlay updates its color with the displayed cell.");

        var target = TerminalLinkPolicy.Evaluate(Encoding.UTF8.GetBytes("https://" + new string('a', 600) + "@destination.example/path"));
        var confirmation = (Task<bool>)typeof(MainPage).GetMethod("ConfirmLinkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [target])!;
        ContentDialog? dialog = null;
        await WaitAsync(() => (dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(surface.XamlRoot)
            .SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().FirstOrDefault()) is not null, "full link confirmation");
        try
        {
            await WaitAsync(() => Descendants(dialog!).OfType<TextBlock>().Any(block =>
                block.Text == target.Uri!.AbsoluteUri && block.Text.Contains("destination.example")),
                "the complete link destination in the laid-out confirmation dialog");
        }
        finally { dialog!.Hide(); }
        if (await confirmation) throw new Exception("Cancelling confirmation must not approve launching a link.");
        report.Add("PASS link confirmation shows the full validated URI and cancels without launching.");

        await WaitAsync(() => Field<int>(page, "_outputRefreshQueued") == 0, "last output notification before scrolling");
        var offset = Presented().Viewport.Offset;
        typeof(MainPage).GetField("_animationsEnabled", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, true);
        Invoke("ScrollHistoryWheel", session, -5d);
        if (!Field<TerminalScrollAnimation>(page, "_scrollAnimation").IsActive)
            throw new Exception("A running, visible page with animations enabled must animate history scrolling.");
        await WaitAsync(() => !Field<TerminalScrollAnimation>(page, "_scrollAnimation").IsActive, "fractional scrolling completion");
        await WaitAsync(() => !Field<bool>(page, "_renderingSubscribed") && !Field<bool>(page, "_frameRefreshQueued"), "scrolling frame pump becoming idle");
        if (Presented().Viewport.Offset != offset - 5 || Field<double>(surface, "_scrollFraction") != 0 ||
            Presented().OverscanAbove.Length != 0 || Presented().OverscanBelow.Length != 0)
            throw new Exception($"Smooth scrolling did not settle. Offset {offset} -> {Presented().Viewport.Offset}, fraction {Field<double>(surface, "_scrollFraction")}, overscan {Presented().OverscanAbove.Length}/{Presented().OverscanBelow.Length}.");
        report.Add("PASS composition-driven scrolling settles five history rows and releases overscan.");

        var find = (TextBox)page.FindName("FindBox");
        find.Text = "history-020";
        Invoke("FindMatch", true, false);
        await WaitAsync(() => !Field<bool>(page, "_findInFlight"), "progressive find in the live page");
        if (!session.HasSelection) throw new Exception("Find did not select its history match.");
        session.ClearSelection();
        session.ScrollToBottom();
        Refresh();
        report.Add("PASS progressive Find selects a history match in the actual page.");

        Emit("\x1b]9;4;1;37\a\x1b]9;Integration fixture\a");
        await WaitAsync(() => Field<ProgressBar>(page, "_progressBar").Visibility == Visibility.Visible, "program progress UI");
        if (Field<ProgressBar>(page, "_progressBar").Value != 37) throw new Exception("Progress UI lost its percentage.");
        await WaitAsync(() => Field<PaneEffects>(page, "_effects").Notices.Items.Count != 0, "program notification card");
        Emit("\u001bc");
        await WaitAsync(() => Field<ProgressBar>(page, "_progressBar").Visibility == Visibility.Collapsed &&
            Field<PaneEffects>(page, "_effects").Notices.Items.Count == 0, "RIS withdraws cards and progress");
        report.Add("PASS native progress and notification callbacks reach WinUI and RIS withdraws them.");

        Emit("\x1b]133;A\a$ test\r\n\x1b]133;C\a10%\rdone\r\n\x1b]133;D;0\a");
        Refresh();
        var commandFrame = Presented();
        if (!session.TrySelectFrameCommandOutput(commandFrame, 0, 1) || session.ReadSelectionText()?.Trim() != "done")
            throw new Exception("Command output selection must use native semantic rows and CR rewrites.");
        Emit("\x1b[2;1Hnew");
        if (session.TrySelectFrameCommandOutput(commandFrame, 0, 1))
            throw new Exception("Command output selection must reject a stale displayed frame.");
        Emit("\u001bc");
        report.Add("PASS native command output selection and stale-frame rejection.");

        Emit("offline-search-marker\r\n");
        var snapshot = await page.CaptureArchiveSnapshotAsync();
        var archivePath = Path.ChangeExtension(Report, ".agtvt");
        await Task.Run(() => TerminalSnapshotArchive.SaveFile(archivePath, snapshot));
        var restored = await Task.Run(() => { using var stream = File.OpenRead(archivePath); return TerminalSnapshotArchive.Open(stream); });
        var viewer = new TerminalArchiveWindow(restored, archivePath);
        try
        {
            viewer.Activate();
            var archiveSurface = Field<TerminalSurface>(viewer, "_surface");
            await WaitAsync(() => archiveSurface.IsLoaded && Field<CanvasRenderTarget?>(archiveSurface, "_renderTarget") is not null, "offline archive raster");
            if (!archiveSurface.Input.IsReadOnly) throw new Exception("Offline archive input is editable.");
            Field<TextBox>(viewer, "_query").Text = "offline-search-marker";
            typeof(TerminalArchiveWindow).GetMethod("Find", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewer, [true]);
            await Field<Task>(viewer, "_findTask");
            if (Field<TextBlock>(viewer, "_status").Text != "Match selected") throw new Exception("Offline archive find did not select a match.");
        }
        finally { viewer.Close(); }
        try { restored.ReadFrame(); throw new Exception("Closed archive retained its native terminal."); }
        catch (ObjectDisposedException) { }
        report.Add("PASS save, reopen, raster, read-only input, offline find, and close disposal through the real archive window.");

        await AssertRetainedHistoryScrollAsync(page, report);
    }

    /// <summary>
    /// A stopped or exited page keeps its terminal and history but has no frame driver (CanRefreshFrames needs Running).
    /// A wheel there must move the viewport at once. Animating would only record a target that nothing advances.
    /// </summary>
    private static async Task AssertRetainedHistoryScrollAsync(MainPage page, List<string> report)
    {
        const BindingFlags NonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
        var animations = typeof(MainPage).GetField("_animationsEnabled", NonPublic)!;
        var original = (bool)animations.GetValue(page)!;
        var surface = (TerminalSurface)page.FindName("Terminal");
        string State() => Field<object>(page, "_state").ToString()!;
        TerminalFrame Presented() => Field<TerminalFrame>(page, "_presentedFrame");
        try
        {
            foreach (var ending in new[] { "stop", "natural exit" })
            {
                if (State() == "Idle") await page.RestartSessionAsync();
                await WaitAsync(() => State() == "Running", $"{ending} fixture session running");
                var session = Field<TerminalSession>(page, "_session");
                var processId = session.ProcessId;
                var completion = session.Completion;

                typeof(TerminalSession).GetMethod("ProcessOutput", NonPublic)!.Invoke(session, [new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(
                    "\u001bc" + string.Concat(Enumerable.Range(0, Presented().Rows + 60).Select(i => $"retained-{i:D3}\r\n"))))]);
                session.ScrollToBottom();
                await WaitAsync(() => Field<int>(page, "_outputRefreshQueued") == 0, $"{ending} fixture output notification");
                typeof(MainPage).GetMethod("RefreshOutput", NonPublic)!.Invoke(page, [session]);

                if (ending == "stop") await page.StopSessionAsync();
                else
                {
                    session.Send("exit\r");
                    await WaitAsync(() => State() == "Idle" && completion.IsCompleted, "natural shell exit");
                }

                if (State() != "Idle" || !ReferenceEquals(Field<TerminalSession>(page, "_session"), session))
                    throw new Exception($"After {ending} the page must retain its session in Idle, not {State()}.");
                if (!Field<bool>(page, "_viewActive") || !page.IsLoaded || Field<bool>(page, "_surfaceOpen") || Field<bool>(page, "_selecting"))
                    throw new Exception($"After {ending} the fixture must be visible, loaded and unselected so only session state can disable animation.");
                if ((bool)typeof(MainPage).GetProperty("CanRefreshFrames", NonPublic)!.GetValue(page)!)
                    throw new Exception($"After {ending} the frame driver must be unavailable.");
                if (Presented().Viewport.MaximumOffset < 20 || Presented().Viewport.Offset < 10)
                    throw new Exception($"After {ending} the fixture lost retained history: {Presented().Viewport}.");

                // The same wheel runs with animations on and off, upward then downward, so one-step and accumulated input both count.
                foreach (var (animate, rows) in new[] { (true, -5d), (true, 2d), (false, -3d), (true, -1d), (true, -1d) })
                {
                    animations.SetValue(page, animate);
                    var context = $"{ending}, animations {(animate ? "on" : "off")}, wheel {rows:+0;-0}";
                    var before = Presented().Viewport.Offset;
                    var expected = Math.Clamp(before + (long)rows, 0, Presented().Viewport.MaximumOffset);
                    typeof(MainPage).GetMethod("ScrollHistoryWheel", NonPublic)!.Invoke(page, [session, rows]);
                    // Immediately, and again after the 140 ms animation would have finished, to catch a deferred driver.
                    for (var check = 0; check < 2; check++)
                    {
                        var animation = Field<TerminalScrollAnimation>(page, "_scrollAnimation");
                        var frame = Presented();
                        if (frame.Viewport.Offset != expected || animation.IsActive || Field<long>(page, "_scrollTick") != 0 ||
                            Field<double>(surface, "_scrollFraction") != 0 || frame.OverscanAbove.Length != 0 || frame.OverscanBelow.Length != 0)
                            throw new Exception($"Retained history did not scroll immediately ({context}, check {check}). Offset {before} -> {frame.Viewport.Offset}, expected {expected}, " +
                                $"animation active {animation.IsActive}, tick {Field<long>(page, "_scrollTick")}, fraction {Field<double>(surface, "_scrollFraction")}, overscan {frame.OverscanAbove.Length}/{frame.OverscanBelow.Length}.");
                        if (Field<bool>(page, "_renderingSubscribed") || Field<bool>(page, "_frameRefreshQueued") ||
                            Field<DispatcherQueueTimer>(page, "_chromeTimer").IsRunning || Field<DispatcherQueueTimer>(page, "_cursorTimer").IsRunning)
                            throw new Exception($"Scrolling retained history started a render subscription or timer ({context}, check {check}).");
                        if (State() != "Idle" || !ReferenceEquals(Field<TerminalSession>(page, "_session"), session) ||
                            session.ProcessId != processId || !ReferenceEquals(session.Completion, completion))
                            throw new Exception($"Scrolling retained history changed the session or restarted its process ({context}, check {check}).");
                        if (check == 0) await Task.Delay(250);
                    }
                }
                report.Add($"PASS wheel scrolls retained history immediately after {ending} with animations on and off, without a frame subscription, overscan or restart.");
            }
        }
        finally { animations.SetValue(page, original); }
    }
}
