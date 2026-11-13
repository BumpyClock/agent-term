using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.UI;
using WinRT;
using Request = AgentTerm_App.TerminalSurface.RasterRequest;
using Presentation = AgentTerm_App.TerminalSurface.RasterPresentation;

internal static class RasterWorkerChecks
{
    private const float CellWidth = 10, CellHeight = 20;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    internal static void Run(CanvasDevice reference)
    {
        CheckSharedRaster();
        CheckContendedHandoff();
        CheckPublishedParity(reference);
        CheckHeldPaneIndependence(reference);
        CheckReadbackAfterQueuedFrame(reference);
        CheckCloseWhileHeld();
        CheckCloseAfterDispatcherShutdown();
        CheckCloseWhileIdle();
        CheckFailureSurfaces();
        CheckCoalescedDelivery();
        CheckPendingHandoff(reference);
        CheckCloseWhilePending();
        CheckFailureWhilePending();
        Console.WriteLine("Raster worker ordering, publication and lifecycle checks passed.");
    }

    private static void CheckSharedRaster()
    {
        using var raster = new CanvasDevice();
        using var presentation = new CanvasDevice();
        foreach (var dpi in new[] { 96f, 120f, 144f, 168f, 192f })
            foreach (var dips in new[] { 1f, 7.3f, 333.3f, 517.5f, 640.2f, 1001f, 1366.6f })
            {
                using var native = new CanvasRenderTarget(raster, dips, dips, dpi);
                var shared = TerminalSurface.SharedRasterSink.PixelsOf(dips, dpi);
                if (native.SizeInPixels.Width != shared)
                    throw new Exception($"Shared raster sizes {dips} DIPs at {dpi} DPI as {shared} pixels; Win2D allocates {native.SizeInPixels.Width}.");
            }
        using var texture = new TerminalSurface.SharedTexture(raster, presentation, 64, 32);
        using var target = CanvasRenderTarget.CreateFromDirect3D11Surface(raster, texture.RasterSurface, 192);
        using var staging = new CanvasRenderTarget(presentation, 64, 32, 96);
        using (var fill = staging.CreateDrawingSession()) fill.Clear(Color.FromArgb(255, 255, 0, 255));
        (Rect Dirty, Color Fill)[] frames =
        [
            (new(0, 0, 64, 32), Color.FromArgb(128, 128, 0, 0)), (new(0, 0, 32, 32), Color.FromArgb(255, 0, 200, 0)),
            (new(32, 8, 32, 16), Color.FromArgb(0, 0, 0, 0)), (new(5, 3, 1, 1), Color.FromArgb(64, 0, 0, 64)),
            (new(0, 0, 32, 32), Color.FromArgb(255, 10, 20, 30)), (new(40, 0, 24, 32), Color.FromArgb(200, 200, 100, 0)),
        ];
        foreach (var (dirty, fill) in frames)
        {
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Blend = CanvasBlend.Copy;
                drawing.FillRectangle(new Rect(dirty.X / 2, dirty.Y / 2, dirty.Width / 2, dirty.Height / 2), fill);
            }
            texture.Yield();
            if (!texture.TryStage(staging, dirty) || !texture.TryReclaim()) throw new Exception($"An uncontended handoff of {dirty} did not complete.");
            AssertMatches($"shared raster staged {dirty}", staging.GetPixelBytes(), target.GetPixelBytes());
        }
    }

    private static unsafe void CheckContendedHandoff()
    {
        using var raster = new CanvasDevice();
        using var presentation = new CanvasDevice();
        using var foreign = new CanvasDevice();
        using var texture = new TerminalSurface.SharedTexture(raster, presentation, 16, 8);
        using var target = CanvasRenderTarget.CreateFromDirect3D11Surface(raster, texture.RasterSurface, 96);
        using var staging = new CanvasRenderTarget(presentation, 16, 8, 96);
        var all = new Rect(0, 0, 16, 8);
        var owner = ForeignKeyedMutex(texture.RasterSurface, foreign);
        ulong? held = null;
        try
        {
            using (var drawing = target.CreateDrawingSession()) drawing.Clear(Color.FromArgb(128, 0, 64, 128));
            var before = staging.GetPixelBytes();
            texture.Yield();
            Acquire(1);
            if (Bounded(() => texture.TryStage(staging, all)) || texture.Holder != TerminalSurface.SharedHolder.AwaitingPresentation)
                throw new Exception("Presentation staged while another device owned the presentation key.");
            if (!staging.GetPixelBytes().SequenceEqual(before)) throw new Exception("A refused handoff changed the presentation copy.");
            Release();
            if (!Bounded(() => texture.TryStage(staging, all))) throw new Exception("Presentation did not stage after the other owner released.");
            Acquire(0);
            if (Bounded(texture.TryReclaim) || texture.Holder != TerminalSurface.SharedHolder.AwaitingRaster)
                throw new Exception("The raster reclaimed its texture while another device owned the raster key.");
            Release();
            if (!Bounded(texture.TryReclaim) || texture.Holder != TerminalSurface.SharedHolder.Raster)
                throw new Exception("The raster did not reclaim its texture after the other owner released.");
            AssertMatches("contended handoff", staging.GetPixelBytes(), target.GetPixelBytes());
        }
        finally
        {
            if (held is not null) Release();
            Marshal.Release(owner);
        }

        static bool Bounded(Func<bool> attempt) => Task.Run(attempt).WaitAsync(Patience).GetAwaiter().GetResult();

        void Acquire(ulong key)
        {
            Check(((delegate* unmanaged[Stdcall]<nint, ulong, uint, int>)(*(nint**)owner)[8])(owner, key, 0));
            held = key;
        }

        void Release()
        {
            Check(((delegate* unmanaged[Stdcall]<nint, ulong, int>)(*(nint**)owner)[9])(owner, held!.Value));
            held = null;
        }

        static void Check(int result)
        {
            if (result != 0) throw new Exception($"The foreign keyed mutex owner received 0x{result:X8}.");
        }
    }

    private static unsafe nint ForeignKeyedMutex(IDirect3DSurface surface, CanvasDevice device)
    {
        Guid access = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), resourceIid = new("035F3AB4-482E-4E50-B41F-8A7F8BD8960B"),
            deviceIid = new("DB6F6DDB-AC77-4E88-8253-819DF9BBF140"), mutexIid = new("9D8E1289-D7B3-465F-8126-250E349AF85D");
        nint resource = Interface(((IWinRTObject)surface).NativeObject.ThisPtr, resourceIid), d3d = 0;
        try
        {
            nint handle, opened;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)resource)[8])(resource, &handle));
            d3d = Interface(((IWinRTObject)device).NativeObject.ThisPtr, deviceIid);
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)d3d)[28])(d3d, handle, &mutexIid, &opened));
            return opened;
        }
        finally
        {
            Marshal.Release(resource);
            if (d3d != 0) Marshal.Release(d3d);
        }

        nint Interface(nint winrt, Guid iid)
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(winrt, in access, out var dxgi));
            try
            {
                nint result;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)dxgi)[3])(dxgi, &iid, &result));
                return result;
            }
            finally { Marshal.Release(dxgi); }
        }
    }

    private static void CheckPendingHandoff(CanvasDevice reference)
    {
        using var terminal = new GhosttyTerminal(20, 4);
        using var pane = new Pane();
        Write(terminal, "held");
        var first = For(terminal.ReadFrame());
        Write(terminal, "\r\nnext");
        var second = For(terminal.ReadFrame());
        Write(terminal, "\r\nlatest");
        var latest = For(terminal.ReadFrame(), dpi: 120);
        pane.Sink.Held = true;
        pane.Rasterizer.Submit(first);
        AwaitRetries(pane, "first pending handoff");
        pane.Rasterizer.Submit(second);
        pane.Rasterizer.Submit(latest);
        var read = pane.Rasterizer.ReadRasterAsync();
        AwaitRetries(pane, "coalesced pending handoff");
        if (pane.Presented.Count != 0 || pane.Rasterizer.Preparations != 1 || read.IsCompleted || !pane.Rasterizer.Running)
            throw new Exception("The worker repainted, read or delivered while the presentation handoff was pending.");
        pane.Sink.Held = false;
        var shown = pane.Next("released handoff");
        var after = pane.Next("latest after handoff");
        if (!ReferenceEquals(shown.Request, first) || !ReferenceEquals(after.Request, latest) || pane.Rasterizer.Preparations != 2)
            throw new Exception($"A released handoff must deliver its request and then only the latest one; prepared {pane.Rasterizer.Preparations}.");
        var pixels = read.WaitAsync(Patience).GetAwaiter().GetResult();
        if (!ReferenceEquals(pixels.Request, latest)) throw new Exception("A readback queued during a handoff returned an older raster.");
        AssertMatches("pending-handoff-latest", pane.Sink.Screen!.GetPixelBytes(), Expected(reference, latest));
        AssertMatches("pending-handoff-readback", pixels.Pixels, Expected(reference, latest));
        if (!SpinWait.SpinUntil(() => !pane.Rasterizer.Running, Patience)) throw new Exception("The worker stayed scheduled after its handoff.");
    }

    private static void CheckCloseWhilePending()
    {
        using var terminal = new GhosttyTerminal(20, 4);
        using var pane = new Pane();
        Write(terminal, "closing held");
        pane.Sink.Held = true;
        pane.Rasterizer.Submit(For(terminal.ReadFrame()));
        AwaitRetries(pane, "handoff before close");
        var read = pane.Rasterizer.ReadRasterAsync();
        pane.Close();
        if (!read.IsFaulted) throw new Exception("Close left a readback waiting on a pending handoff.");
        if (!pane.ReleaseCalled.Wait(Patience) || !pane.Rasterizer.Released || pane.Rasterizer.Running)
            throw new Exception("Close did not release a worker whose handoff never completed.");
        var attempts = pane.Sink.Attempts;
        Thread.Sleep(100);
        if (pane.Sink.Attempts != attempts || pane.Presented.Count != 0 || pane.Failed.Count != 0)
            throw new Exception("A closed worker kept retrying, presented or failed its abandoned handoff.");
    }

    private static void CheckFailureWhilePending()
    {
        using var terminal = new GhosttyTerminal(20, 4);
        using var pane = new Pane();
        pane.Sink.Held = true;
        pane.Rasterizer.Submit(For(terminal.ReadFrame()));
        AwaitRetries(pane, "handoff before failure");
        var read = pane.Rasterizer.ReadRasterAsync();
        var failure = new InvalidOperationException("handoff failed");
        pane.Sink.Failure = failure;
        if (!pane.Failed.TryTake(out var reported, Patience) || !ReferenceEquals(reported.Error, failure) || reported.Lost)
            throw new Exception("A failed handoff retry was not reported to its owner.");
        if (!read.IsFaulted || !SpinWait.SpinUntil(() => !pane.Rasterizer.Running, Patience) || pane.Presented.Count != 0)
            throw new Exception("A failed handoff retry left work pending or delivered.");
    }

    private static void AwaitRetries(Pane pane, string name)
    {
        var attempts = pane.Sink.Attempts;
        if (!SpinWait.SpinUntil(() => pane.Sink.Attempts >= attempts + 2, Patience)) throw new Exception($"{name} was not retried.");
    }

    private static void CheckPublishedParity(CanvasDevice reference)
    {
        using var terminal = new GhosttyTerminal(40, 12);
        using var pane = new Pane();
        Write(terminal, "\x1b[38;2;230;230;230;48;2;12;12;12m\x1b[2J\x1b[HWorker raster\r\n\x1b[1;31mBold red\x1b[0m \x1b[3mItalic\x1b[0m é 界 🙂\r\n" +
            "x => y != z === ->\r\n\x1b[5mblinking row\x1b[0m\r\n\x1b[48;2;20;80;140mColor band\x1b[0m");
        Show("initial", For(terminal.ReadFrame()));
        var full = pane.Sink.LastUpdate;
        Write(terminal, "\x1b[1;8HX");
        Show("sparse-edit", For(terminal.ReadFrame()));
        if (pane.Sink.LastUpdate.Width * pane.Sink.LastUpdate.Height >= full.Width * full.Height)
            throw new Exception($"A one-cell edit published {pane.Sink.LastUpdate} instead of a dirty rectangle inside {full}.");
        Write(terminal, "\x1b[1;1Hdrop");
        terminal.ReadFrame();
        Write(terminal, "\x1b[2;1Hnext");
        Show("dropped-intermediate-frame", For(terminal.ReadFrame()));
        var blinking = terminal.ReadFrame();
        if (!Show("blink-off", For(blinking, phase: false)).HasBlinkingText)
            throw new Exception("The worker did not report the visible blinking row.");
        Show("blink-on", For(blinking, phase: true));
        terminal.BeginSelection(2, 2);
        terminal.ExtendSelection(9, 2);
        terminal.EndSelection();
        Show("selection", For(terminal.ReadFrame()));
        terminal.ClearSelection();
        Show("selection-cleared", For(terminal.ReadFrame()));
        Show("transparent", For(terminal.ReadFrame(), transparent: true));
        Show("dpi-120", For(terminal.ReadFrame(), dpi: 120));
        Write(terminal, "\x1b[3;3H\x1b[K\x1b[4;1H\x1b[2K");
        if (Show("dpi-120-erase", For(terminal.ReadFrame(), dpi: 120)).HasBlinkingText)
            throw new Exception("The worker still reported blinking text after its only row was erased.");
        terminal.Resize(30, 9);
        Show("resized-grid", For(terminal.ReadFrame(), dpi: 144));
        var last = For(terminal.ReadFrame(), dpi: 144);
        var read = pane.Rasterizer.ReadRasterAsync().WaitAsync(Patience).GetAwaiter().GetResult();
        if (!read.Pixels.SequenceEqual(pane.Sink.Screen!.GetPixelBytes()))
            throw new Exception("The worker's retained raster and its last publication differ.");
        if (read.Request.Metrics.Dpi != 144 || read.Request.Frame.Columns != 30)
            throw new Exception("The readback did not identify the request its pixels were painted from.");
        pane.Rasterizer.Submit(last);
        var ordered = pane.Rasterizer.ReadRasterAsync().WaitAsync(Patience).GetAwaiter().GetResult();
        if (!ReferenceEquals(ordered.Request, last)) throw new Exception("A readback requested after a submission returned an older raster.");
        AssertMatches("ordered-readback", ordered.Pixels, Expected(reference, last));
        pane.Next("ordered readback");
        if (!SpinWait.SpinUntil(() => !pane.Rasterizer.Running, Patience))
            throw new Exception("The raster worker stayed scheduled with no pending request.");

        Presentation Show(string name, Request request)
        {
            var updates = pane.Sink.Updates;
            pane.Rasterizer.Submit(request);
            var shown = pane.Next(name);
            if (!ReferenceEquals(shown.Request, request)) throw new Exception($"{name} presented a different request.");
            if (pane.Sink.Updates > updates + 1) throw new Exception($"{name} opened {pane.Sink.Updates - updates} publication sessions.");
            AssertMatches(name, pane.Sink.Screen!.GetPixelBytes(), Expected(reference, request));
            return shown;
        }
    }

    private static void CheckHeldPaneIndependence(CanvasDevice reference)
    {
        using var terminal = new GhosttyTerminal(20, 4);
        using var held = new Pane();
        using var free = new Pane();
        Write(terminal, "first");
        var first = For(terminal.ReadFrame());
        Write(terminal, "\r\nsecond");
        var second = For(terminal.ReadFrame());
        Write(terminal, "\r\nthird");
        var third = For(terminal.ReadFrame());
        var lockTaken = held.Device.Lock();
        try
        {
            held.Rasterizer.Submit(first);
            if (!SpinWait.SpinUntil(() => held.Rasterizer.Preparations == 1, Patience))
                throw new Exception("Pane A never started its first preparation.");
            held.Rasterizer.Submit(second);
            held.Rasterizer.Submit(third);
            free.Rasterizer.Submit(third);
            var shown = free.Next("pane B while pane A's device is locked");
            AssertMatches("independent-pane", free.Sink.Screen!.GetPixelBytes(), Expected(reference, shown.Request));
            if (held.Presented.Count != 0 || held.Rasterizer.Preparations != 1)
                throw new Exception("Pane A progressed while its device lock was held.");
        }
        finally { lockTaken.Dispose(); }
        var resumed = held.Next("pane A held request");
        var latest = held.Next("pane A latest request");
        if (!ReferenceEquals(resumed.Request, first) || !ReferenceEquals(latest.Request, third) || held.Rasterizer.Preparations != 2)
            throw new Exception($"Pane A must finish the held request and then only the latest pending request; prepared {held.Rasterizer.Preparations}.");
        AssertMatches("coalesced-latest", held.Sink.Screen!.GetPixelBytes(), Expected(reference, third));
    }

    private static void CheckReadbackAfterQueuedFrame(CanvasDevice reference)
    {
        using var terminal = new GhosttyTerminal(20, 4);
        using var pane = new Pane();
        Write(terminal, "first");
        var first = For(terminal.ReadFrame());
        Write(terminal, "\r\nsecond");
        var second = For(terminal.ReadFrame());
        Task<TerminalSurface.RasterReadback> read;
        using (pane.Device.Lock())
        {
            pane.Rasterizer.Submit(first);
            if (!SpinWait.SpinUntil(() => pane.Rasterizer.Preparations == 1, Patience))
                throw new Exception("Readback fixture never started its first preparation.");
            pane.Rasterizer.Submit(second);
            read = pane.Rasterizer.ReadRasterAsync();
            if (read.IsCompleted) throw new Exception("Readback completed while the preceding preparation was held.");
        }
        var result = read.WaitAsync(Patience).GetAwaiter().GetResult();
        if (!ReferenceEquals(result.Request, second))
            throw new Exception("Readback skipped a frame submitted before the read was queued.");
        AssertMatches("readback-after-queued-frame", result.Pixels, Expected(reference, second));
    }

    private static void CheckCloseWhileHeld()
    {
        using var terminal = new GhosttyTerminal(20, 4);
        using var pane = new Pane();
        Write(terminal, "closing");
        var lockTaken = pane.Device.Lock();
        try
        {
            pane.Rasterizer.Submit(For(terminal.ReadFrame()));
            if (!SpinWait.SpinUntil(() => pane.Rasterizer.Preparations == 1, Patience))
                throw new Exception("Close fixture never started a preparation.");
            pane.Close();
            if (pane.ReleaseCalled.IsSet || pane.Rasterizer.Released)
                throw new Exception("Close released resources still owned by an in-flight preparation.");
            pane.Rasterizer.Submit(For(terminal.ReadFrame()));
        }
        finally { lockTaken.Dispose(); }
        if (!pane.ReleaseCalled.Wait(Patience) || !pane.Rasterizer.Released)
            throw new Exception("The worker did not release its resources after the in-flight preparation finished.");
        if (pane.Presented.Count != 0 || pane.Failed.Count != 0 || pane.Rasterizer.Preparations != 1)
            throw new Exception("A closed rasterizer published, failed or prepared after closing.");
        var read = pane.Rasterizer.ReadRasterAsync();
        if (!read.IsFaulted) throw new Exception("A closed rasterizer accepted a readback.");
    }

    private static void CheckCoalescedDelivery()
    {
        using var terminal = new GhosttyTerminal(20, 4);
        var queued = new ConcurrentQueue<Action>();
        using var pane = new Pane(queued.Enqueue);
        Write(terminal, "one");
        var first = For(terminal.ReadFrame());
        Write(terminal, " two");
        var second = For(terminal.ReadFrame());
        pane.Rasterizer.Submit(first);
        if (!SpinWait.SpinUntil(() => !queued.IsEmpty, Patience)) throw new Exception("The first result was never queued for its owner.");
        pane.Rasterizer.Submit(second);
        if (!SpinWait.SpinUntil(() => pane.Rasterizer.Preparations == 2 && !pane.Rasterizer.Running, Patience))
            throw new Exception("The second preparation did not finish.");
        if (queued.Count != 1) throw new Exception($"Two completed results queued {queued.Count} owner notifications instead of one.");
        while (queued.TryDequeue(out var action)) action();
        if (!pane.Presented.TryTake(out var delivered) || !ReferenceEquals(delivered.Request, second) || pane.Presented.Count != 0)
            throw new Exception("The queued notification did not deliver only the latest result.");
        var pendingRead = pane.Rasterizer.ReadRasterAsync();
        pane.Close();
        if (!SpinWait.SpinUntil(() => pendingRead.IsCompleted, Patience)) throw new Exception("A close left a readback pending.");
        if (!SpinWait.SpinUntil(() =>
            {
                while (queued.TryDequeue(out var action)) action();
                return pane.ReleaseCalled.IsSet;
            }, Patience)) throw new Exception("A closed rasterizer never released its resources.");
    }

    private static void CheckCloseAfterDispatcherShutdown()
    {
        using var terminal = new GhosttyTerminal(20, 4);
        using var pane = new Pane(_ => { });
        var held = pane.Device.Lock();
        try
        {
            pane.Rasterizer.Submit(For(terminal.ReadFrame()));
            if (!SpinWait.SpinUntil(() => pane.Rasterizer.Preparations == 1, Patience))
                throw new Exception("Shutdown fixture never started a preparation.");
            pane.Close();
            if (pane.ReleaseCalled.IsSet) throw new Exception("Shutdown released resources before active rendering ended.");
        }
        finally { held.Dispose(); }
        if (!pane.ReleaseCalled.Wait(Patience))
            throw new Exception("Owner disposal depended on a dispatcher that no longer accepts callbacks.");
    }

    private static void CheckCloseWhileIdle()
    {
        using var terminal = new GhosttyTerminal(20, 4);
        using var pane = new Pane();
        Write(terminal, "idle");
        pane.Rasterizer.Submit(For(terminal.ReadFrame()));
        pane.Next("idle fixture");
        if (!SpinWait.SpinUntil(() => !pane.Rasterizer.Running, Patience)) throw new Exception("The worker did not become idle.");
        pane.Close();
        if (!pane.ReleaseCalled.IsSet || !pane.Rasterizer.Released)
            throw new Exception("Closing an idle rasterizer must release its resources before returning.");
    }

    private static void CheckFailureSurfaces()
    {
        using var terminal = new GhosttyTerminal(20, 4);
        using var pane = new Pane();
        var frame = terminal.ReadFrame();
        var request = For(frame);
        pane.Rasterizer.Submit(request with { Metrics = request.Metrics with { Width = -1 } });
        if (!pane.Failed.TryTake(out var failure, Patience)) throw new Exception("A failed preparation was not reported to its owner.");
        if (failure.Lost) throw new Exception("An invalid request was reported as device loss.");
        if (!SpinWait.SpinUntil(() => !pane.Rasterizer.Running, Patience)) throw new Exception("The failed worker did not stop.");
        pane.Rasterizer.Submit(request);
        if (pane.Presented.TryTake(out _, 200) || pane.Rasterizer.Preparations != 1)
            throw new Exception("A failed rasterizer accepted more work.");
        pane.Close();
        if (!pane.ReleaseCalled.IsSet || !pane.Rasterizer.Released) throw new Exception("A failed rasterizer kept its resources after close.");
    }

    private static Request For(TerminalFrame frame, float dpi = 96, bool transparent = false, bool phase = true) =>
        new(frame, new(frame.Columns * CellWidth, frame.Rows * CellHeight, dpi, CellWidth, CellHeight, 14, "Consolas", transparent, 0), phase);

    private static byte[] Expected(CanvasDevice device, Request request)
    {
        var metrics = request.Metrics;
        using var target = new CanvasRenderTarget(device, metrics.Width, metrics.Height, metrics.Dpi);
        using (var drawing = target.CreateDrawingSession())
            TerminalSurface.DrawFrame(drawing, request.Frame, metrics.CellWidth, metrics.CellHeight, true, true, metrics.FontSize,
                metrics.Transparent, metrics.FontFamily, hideCursor: true, gridTop: metrics.GridTop, textPhase: request.TextPhase);
        return target.GetPixelBytes();
    }

    private static void AssertMatches(string name, byte[] actual, byte[] expected)
    {
        if (actual.Length != expected.Length) throw new Exception($"{name} published {actual.Length} bytes; expected {expected.Length}.");
        var differences = actual.Zip(expected).Count(pair => pair.First != pair.Second);
        if (differences != 0) throw new Exception($"{name} publication differs from a full redraw in {differences} bytes.");
    }

    private static void Write(GhosttyTerminal terminal, string value) => terminal.Write(Encoding.UTF8.GetBytes(value));

    private sealed class Pane : IDisposable
    {
        internal readonly CanvasDevice Device = new();
        internal readonly ScreenSink Sink;
        internal readonly TerminalSurface.Rasterizer Rasterizer;
        internal readonly BlockingCollection<Presentation> Presented = new();
        internal readonly BlockingCollection<(Exception Error, bool Lost)> Failed = new();
        internal readonly ManualResetEventSlim ReleaseCalled = new();
        private bool _closed;

        internal Pane(Action<Action>? post = null)
        {
            Sink = new ScreenSink(Device);
            Rasterizer = new(Device, Sink, post ?? (action => action()), Presented.Add, (error, lost) => Failed.Add((error, lost)));
        }

        internal Presentation Next(string name) =>
            Presented.TryTake(out var presentation, Patience) ? presentation : throw new Exception($"{name} was not presented.");

        internal void Close()
        {
            _closed = true;
            Rasterizer.Close(ReleaseCalled.Set);
        }

        public void Dispose()
        {
            if (!_closed) Close();
            ReleaseCalled.Wait(TimeSpan.FromSeconds(1));
            Sink.Dispose();
            Device.Dispose();
        }
    }

    private sealed class ScreenSink(CanvasDevice device) : TerminalSurface.IRasterSink, IDisposable
    {
        internal CanvasRenderTarget? Screen { get; private set; }
        internal int Updates { get; private set; }
        internal Rect LastUpdate { get; private set; }
        internal volatile bool Held;
        internal volatile Exception? Failure;
        private int _attempts;
        private CanvasRenderTarget? _raster;
        internal int Attempts => Volatile.Read(ref _attempts);

        public CanvasRenderTarget CreateRaster(float width, float height, float dpi) => new(device, width, height, dpi);

        public bool IsDeviceLost(int hresult) => false;

        public bool Present(CanvasRenderTarget raster, Rect dirtyPixels)
        {
            var size = raster.SizeInPixels;
            if (Screen is null || Screen.SizeInPixels.Width != size.Width || Screen.SizeInPixels.Height != size.Height)
            {
                Screen?.Dispose();
                Screen = new CanvasRenderTarget(device, size.Width, size.Height, 96);
                using var fill = Screen.CreateDrawingSession();
                fill.Clear(Color.FromArgb(255, 255, 0, 255));
            }
            if (dirtyPixels.X < 0 || dirtyPixels.Y < 0 || dirtyPixels.Right > size.Width || dirtyPixels.Bottom > size.Height)
                throw new Exception($"Update {dirtyPixels} exceeds the {size.Width}x{size.Height} raster.");
            (_raster, LastUpdate) = (raster, dirtyPixels);
            return TryCompletePresent();
        }

        public bool TryCompletePresent()
        {
            Interlocked.Increment(ref _attempts);
            if (Failure is { } failure) throw failure;
            if (Held) return false;
            Updates++;
            Screen!.CopyPixelsFromBitmap(_raster!, (int)LastUpdate.X, (int)LastUpdate.Y, (int)LastUpdate.X, (int)LastUpdate.Y,
                (int)LastUpdate.Width, (int)LastUpdate.Height);
            return true;
        }

        public void Dispose() => Screen?.Dispose();
    }
}
