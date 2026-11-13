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

        await WaitAsync(() => Field<int>(page, "_outputRefreshQueued") == 0, "last output notification before scrolling");
        var offset = Presented().Viewport.Offset;
        typeof(MainPage).GetField("_animationsEnabled", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, true);
        Invoke("ScrollHistoryWheel", session, -5d);
        await WaitAsync(() => Field<DispatcherQueueTimer?>(page, "_scrollTimer")?.IsRunning == false, "fractional scrolling completion");
        if (Presented().Viewport.Offset != offset - 5 || Field<double>(surface, "_scrollFraction") != 0 ||
            Presented().OverscanAbove.Length != 0 || Presented().OverscanBelow.Length != 0)
            throw new Exception($"Smooth scrolling did not settle. Offset {offset} -> {Presented().Viewport.Offset}, fraction {Field<double>(surface, "_scrollFraction")}, overscan {Presented().OverscanAbove.Length}/{Presented().OverscanBelow.Length}.");
        report.Add("PASS real scroll timer settles five history rows and releases overscan.");

        var find = (TextBox)page.FindName("FindBox");
        find.Text = "history-020";
        Invoke("FindMatch", true);
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
    }
}
