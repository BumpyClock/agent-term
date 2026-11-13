using System.Reflection;
using System.Text;
using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;

internal static partial class StartupChecks
{
    private enum DeviceHoldOutcome { Released, UiBlocked, GuardExpired }

    private static TerminalSurface.Rasterizer RasterWorker(TerminalSurface surface)
    {
        var owner = Field<object?>(surface, "_raster") ?? throw new Exception("Terminal surface has no raster actor.");
        return (TerminalSurface.Rasterizer)owner.GetType().GetProperty("Rasterizer")!.GetValue(owner)!;
    }

    private static string FrameText(TerminalFrame? frame) => frame is null ? "" : string.Concat(frame.Cells.Select(cell => cell.Text));

    private static int? OutputRow(TerminalFrame? frame, string marker)
    {
        if (frame is null) return null;
        int? found = null;
        var row = 0;
        foreach (var cells in frame.Cells.Chunk(frame.Columns))
        {
            if (string.Concat(cells.Select(cell => cell.Text)).TrimStart().StartsWith(marker, StringComparison.Ordinal)) found = row;
            row++;
        }
        return found;
    }

    private static bool PaneQuiet(MainPage page, TerminalSurface surface) =>
        !Field<bool>(page, "_renderingSubscribed") && !Field<bool>(page, "_frameRefreshQueued") &&
        !Field<bool>(page, "_frameRefreshPending") && !Field<bool>(page, "_captureInFlight") &&
        !Field<bool>(page, "_captureRequested") && Field<int>(page, "_outputRefreshQueued") == 0 &&
        !Field<DispatcherQueueTimer>(page, "_synchronizedRefreshTimer").IsRunning &&
        surface.HasRenderResources && !RasterWorker(surface).Running &&
        ReferenceEquals(surface.PresentedFrame, Field<TerminalFrame>(page, "_presentedFrame"));

    private static async Task AssertRasterWorkerIsolationAsync(TerminalTab tab, List<string> report)
    {
        var inputPage = tab.ActivePage;
        var heldPage = Descendants(tab).OfType<MainPage>().Single(page => page != inputPage);
        var inputSurface = (TerminalSurface)inputPage.FindName("Terminal");
        var heldSurface = (TerminalSurface)heldPage.FindName("Terminal");
        var inputSession = Field<TerminalSession>(inputPage, "_session");
        var heldSession = Field<TerminalSession>(heldPage, "_session");

        await WaitAsync(() => PaneQuiet(heldPage, heldSurface) && PaneQuiet(inputPage, inputSurface),
            "both split panes presenting their latest frame with idle raster actors");
        var worker = RasterWorker(heldSurface);
        var device = heldSurface.RasterDevice ?? throw new Exception("Held pane has no raster device.");
        if (!ReferenceEquals(Field<CanvasDevice>(worker, "_device"), device))
            throw new Exception("Held pane's raster actor does not draw on the surface's raster device.");
        if (ReferenceEquals(device, inputSurface.RasterDevice) || ReferenceEquals(device, CanvasDevice.GetSharedDevice()))
            throw new Exception("Split panes must rasterize on independent, non-shared devices.");
        var heldBaseline = await heldSurface.ReadPresentedRasterAsync();
        var inputBaseline = await inputSurface.ReadPresentedRasterAsync();
        await WaitAsync(() => PaneQuiet(heldPage, heldSurface) && PaneQuiet(inputPage, inputSurface), "raster actors idle after baseline reads");

        var dispatcher = heldPage.DispatcherQueue;
        var release = new ManualResetEventSlim();
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Factory.StartNew(() =>
        {
            var uiTurn = new ManualResetEventSlim();
            using (device.Lock())
            {
                locked.SetResult();
                if (!dispatcher.TryEnqueue(DispatcherQueuePriority.Low, uiTurn.Set) || !uiTurn.Wait(TimeSpan.FromSeconds(20)))
                    return DeviceHoldOutcome.UiBlocked;
                uiRan.SetResult();
                return release.Wait(TimeSpan.FromSeconds(120)) ? DeviceHoldOutcome.Released : DeviceHoldOutcome.GuardExpired;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Exception HolderEnded() => new($"Held pane device lock ended before the test released it: {holder.Result}. " +
            (holder.Result == DeviceHoldOutcome.UiBlocked
                ? "The UI dispatcher did not run while the held pane's device was locked."
                : "The release guard expired."));
        var emit = typeof(TerminalSession).GetMethod("ProcessOutput", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var markers = Enumerable.Range(0, 3).Select(index => $"HELD{index}_" + Guid.NewGuid().ToString("N")[..8]).ToArray();
        void EmitHeld(string marker) => emit.Invoke(heldSession, [new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes($"\x1b7\x1b[1;1H{marker}\x1b8"))]);
        bool Submitted(string marker) => Field<TerminalSurface.RasterRequest?>(heldSurface, "_submitted") is { } request &&
            FrameText(request.Frame).Contains(marker);
        bool ShowsHeldMarker() => markers.Any(FrameText(heldSurface.PresentedFrame).Contains);
        Exception Ungated() => new("Held pane presented or finished raster work while its device lock was held, so the lock did not gate its raster actor and the isolation result is invalid. " +
            $"running={worker.Running}; preparations={worker.Preparations}; presentedMarker={ShowsHeldMarker()}; sameActor={ReferenceEquals(RasterWorker(heldSurface), worker)}.");
        Task WhileHeld(Func<bool> ready, string condition) => WaitAsync(() =>
            holder.IsCompleted ? throw HolderEnded() : ShowsHeldMarker() ? throw Ungated() : ready(), condition);
        long preparations, blocked;
        try
        {
            await locked.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (worker.Running) throw new Exception("Held pane raster actor started work before the device lock was taken.");
            preparations = worker.Preparations;

            EmitHeld(markers[0]);
            await WhileHeld(() => Submitted(markers[0]) && worker.Running && worker.Preparations > preparations,
                "held pane submitting changed output that its raster actor starts preparing on the locked device");
            EmitHeld(markers[1]);
            await WhileHeld(() => Submitted(markers[1]), "held pane submitting a second change behind the blocked preparation");
            EmitHeld(markers[2]);
            await WhileHeld(() => Submitted(markers[2]), "held pane submitting its latest change behind the blocked preparation");
            blocked = worker.Preparations;
            await uiRan.Task.WaitAsync(TimeSpan.FromSeconds(20));

            var first = "ISOA" + Guid.NewGuid().ToString("N")[..8];
            var second = "ISOB" + Guid.NewGuid().ToString("N")[..8];
            inputSession.Send("echo " + first + "\r");
            inputSession.Send("echo " + second + "\r");
            try
            {
                await WhileHeld(() => OutputRow(inputSurface.PresentedFrame, first) is { } a && OutputRow(inputSurface.PresentedFrame, second) is { } b && a < b,
                    "independent pane presenting both command outputs in input order while the other pane's device is locked");
            }
            catch (TimeoutException error)
            {
                throw new Exception("Independent pane did not present ordered input while the other pane's raster device was locked. " +
                    "Either pane work is not isolated or the two CanvasDevices share one D2D multithread lock.", error);
            }
            var inputPixels = await inputSurface.ReadPresentedRasterAsync();
            if (OutputRow(inputPixels.Request.Frame, first) is not { } firstRow ||
                OutputRow(inputPixels.Request.Frame, second) is not { } secondRow || firstRow >= secondRow)
                throw new Exception("Independent pane raster readback did not match ordered command output.");
            if (inputPixels.Pixels.AsSpan().SequenceEqual(inputBaseline.Pixels))
                throw new Exception("Independent pane reported new output but its presented raster pixels did not change while the other pane's device was locked.");

            if (holder.IsCompleted) throw HolderEnded();
            if (!worker.Running || worker.Preparations != blocked || ShowsHeldMarker() || !ReferenceEquals(RasterWorker(heldSurface), worker))
                throw Ungated();
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(60));
        }
        if (holder.Result != DeviceHoldOutcome.Released) throw HolderEnded();

        await WaitAsync(() => FrameText(heldSurface.PresentedFrame).Contains(markers[2]) &&
            ReferenceEquals(heldSurface.PresentedFrame, Field<TerminalFrame>(heldPage, "_presentedFrame")),
            "held pane presenting its latest frame after the device lock is released");
        var prepared = worker.Preparations - preparations;
        if (worker.Preparations - blocked >= 2)
            throw new Exception($"Held pane prepared {worker.Preparations - blocked} rasters after release for the two changes queued behind its blocked preparation; pending requests were not coalesced to the latest frame.");
        if (!ReferenceEquals(RasterWorker(heldSurface), worker))
            throw new Exception("Held pane replaced its raster actor instead of resuming after the device lock was released.");
        var heldPixels = await heldSurface.ReadPresentedRasterAsync();
        if (!FrameText(heldPixels.Request.Frame).Contains(markers[2]))
            throw new Exception("Held pane raster readback did not match its latest submitted frame.");
        if (heldPixels.Pixels.AsSpan().SequenceEqual(heldBaseline.Pixels))
            throw new Exception("Held pane presented its latest frame but its raster pixels did not change.");
        report.Add($"PASS a split pane whose raster device is locked keeps its changed request pending while the UI dispatcher runs and the other pane presents ordered input; the held pane then presents only its latest frame ({prepared} preparations for {markers.Length} submissions).");
    }
}
