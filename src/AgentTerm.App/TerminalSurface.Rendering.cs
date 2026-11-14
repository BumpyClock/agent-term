using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using AgentTerm.Terminal;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace AgentTerm_App;

public sealed partial class TerminalSurface
{
    internal readonly record struct RasterMetrics(float Width, float Height, float Dpi, float CellWidth, float CellHeight,
        float FontSize, string FontFamily, bool Transparent, float GridTop);

    internal sealed record RasterRequest(TerminalFrame Frame, RasterMetrics Metrics, bool TextPhase);

    internal readonly record struct RasterStats(int GlyphLayouts, int GlyphCells, int GlyphCapacity, long GlyphsCreated,
        long ImageBytes, long ImageRequests);

    internal sealed record RasterPresentation(RasterRequest Request, RasterStats Stats, double PrepareMilliseconds, bool HasBlinkingText);

    internal sealed record RasterReadback(RasterRequest Request, byte[] Pixels);

    internal interface IRasterSink
    {
        CanvasRenderTarget CreateRaster(float width, float height, float dpi);
        bool Present(CanvasRenderTarget raster, Rect dirtyPixels);
        bool TryCompletePresent();
        bool IsDeviceLost(int hresult);
    }

    private sealed record RasterOwner(Rasterizer Rasterizer, CanvasDevice Device, CanvasDevice PresentationDevice,
        CompositionGraphicsDevice Graphics, CompositionDrawingSurface Surface, CompositionSurfaceBrush Brush, SpriteVisual Visual,
        SharedRasterSink Sink)
    {
        internal void Detach()
        {
            Visual.Dispose();
            Brush.Dispose();
            Surface.Dispose();
            Graphics.Dispose();
        }

        internal void Release()
        {
            Sink.Dispose();
            PresentationDevice.Dispose();
            Device.Dispose();
        }
    }

    private readonly Microsoft.UI.Xaml.Controls.Border _rasterHost = new() { IsHitTestVisible = true };
    private readonly SolidColorBrush _transparentHost = new(Microsoft.UI.Colors.Transparent);
    private Vector2 _brushScale;
    private RasterOwner? _raster;
    private RasterRequest? _submitted;
    private RasterPresentation? _presentation;
    private XamlRoot? _xamlRoot;

    internal event Action? BlinkingTextChanged;
    internal bool HasBlinkingText => _presentation?.HasBlinkingText == true;
    internal long ImageBitmapCacheBytes => _presentation?.Stats.ImageBytes ?? 0;
    internal RasterStats? PresentedRasterStats => _presentation?.Stats;
    internal TerminalFrame? PresentedFrame => _paintedFrame;
    internal bool HasRenderResources => _raster is not null;
    internal bool RasterPresentationCurrent => _raster is not null && (_submitted is null || ReferenceEquals(_presentation?.Request, _submitted));
    internal CanvasDevice? RasterDevice => _raster?.Device;

    internal Task<RasterReadback> ReadPresentedRasterAsync() => _raster?.Rasterizer.ReadRasterAsync()
        ?? Task.FromException<RasterReadback>(new InvalidOperationException("No terminal raster is attached."));

    private void AttachRaster()
    {
        if (_raster is not null) return;
        var device = new CanvasDevice();
        var presentationDevice = new CanvasDevice();
        var compositor = ElementCompositionPreview.GetElementVisual(_rasterHost).Compositor;
        var graphics = CanvasComposition.CreateCompositionGraphicsDevice(compositor, presentationDevice);
        var surface = graphics.CreateDrawingSurface2(new SizeInt32(0, 0), DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
        var sink = new SharedRasterSink(device, presentationDevice, surface);
        var brush = compositor.CreateSurfaceBrush(surface);
        _brushScale = Vector2.One;
        brush.Stretch = CompositionStretch.None;
        brush.HorizontalAlignmentRatio = 0;
        brush.VerticalAlignmentRatio = 0;
        brush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.NearestNeighbor;
        var visual = compositor.CreateSpriteVisual();
        visual.RelativeSizeAdjustment = Vector2.One;
        visual.Brush = brush;
        ElementCompositionPreview.SetElementChildVisual(_rasterHost, visual);
        var dispatcher = DispatcherQueue;
        RasterOwner? owner = null;
        var rasterizer = new Rasterizer(device, sink,
            action => dispatcher.TryEnqueue(new DispatcherQueueHandler(action)),
            presentation => OnRasterPresented(owner!, presentation),
            (error, deviceLost) => OnRasterFailed(owner!, error, deviceLost));
        owner = new RasterOwner(rasterizer, device, presentationDevice, graphics, surface, brush, visual, sink);
        _raster = owner;
        UpdateHostBackground();
        dispatcher.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_raster, owner) || _resourcesReady) return;
            _resourcesReady = true;
            MeasureCells();
            RequestRaster();
        });
    }

    private void ReleaseRaster()
    {
        var owner = _raster;
        var hadBlinkingText = HasBlinkingText;
        _raster = null;
        _submitted = null;
        _presentation = null;
        _paintedFrame = null;
        _resourcesReady = false;
        UpdateHostBackground();
        if (hadBlinkingText) BlinkingTextChanged?.Invoke();
        if (owner is null) return;
        ElementCompositionPreview.SetElementChildVisual(_rasterHost, null);
        owner.Detach();
        owner.Rasterizer.Close(owner.Release);
    }

    private void RequestRaster()
    {
        if (_suspended || !_resourcesReady || _raster is not { } owner || _frame is not { } frame || XamlRoot is not { } root) return;
        var width = (float)ActualWidth;
        var height = (float)ActualHeight;
        if (width <= 0 || height <= 0) return;
        var metrics = new RasterMetrics(width, height, (float)(96 * root.RasterizationScale), CellWidth, CellHeight,
            _terminalFontSize, TerminalFontFamily, _acrylicBackground, (float)GeometryFor(frame).Top);
        if (_submitted is { } last && last.Metrics == metrics && RasterEquivalent(last.Frame, frame) &&
            (last.TextPhase == _cursorPhase || !HasBlinkingText)) return;
        _submitted = new RasterRequest(frame, metrics, _cursorPhase);
        owner.Rasterizer.Submit(_submitted);
    }

    private void OnRasterPresented(RasterOwner owner, RasterPresentation presentation)
    {
        if (!ReferenceEquals(_raster, owner)) return;
        var hadBlinkingText = HasBlinkingText;
        _presentation = presentation;
        var presented = presentation.Request.Frame;
        _paintedFrame = _frame is { } current && RasterEquivalent(presented, current) ? current : presented;
        if (Changed(ref _brushScale, new Vector2(96 / presentation.Request.Metrics.Dpi))) owner.Brush.Scale = _brushScale;
        DrawCompleted?.Invoke(presentation.PrepareMilliseconds);
        if (PerformanceCapture.Enabled) PerformanceCapture.Record(PerformanceMetric.RasterPreparation, presentation.PrepareMilliseconds, CapturePaneId);
        UpdateCursorOverlay();
        if (presentation.HasBlinkingText && presentation.Request.TextPhase != _cursorPhase) RequestRaster();
        if (hadBlinkingText != presentation.HasBlinkingText) BlinkingTextChanged?.Invoke();
    }

    private void OnRasterFailed(RasterOwner owner, Exception error, bool deviceLost)
    {
        if (!ReferenceEquals(_raster, owner)) return;
        ReleaseRaster();
        if (!deviceLost) ExceptionDispatchInfo.Throw(error);
        if (!_suspended && IsLoaded) AttachRaster();
    }

    private void TrackXamlRoot(XamlRoot? root)
    {
        if (ReferenceEquals(_xamlRoot, root)) return;
        if (_xamlRoot is not null) _xamlRoot.Changed -= XamlRootChanged;
        _xamlRoot = root;
        if (root is not null) root.Changed += XamlRootChanged;
    }

    private void XamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => RequestRaster();

    private void UpdateHostBackground()
    {
        if (_raster is null) _rasterHost.Background = null;
        else if (_frame is not null || _acrylicBackground) _rasterHost.Background = _transparentHost;
        else
        {
            var shade = ActualTheme == ElementTheme.Light ? (byte)250 : (byte)0;
            _rasterHost.Background = new SolidColorBrush(Color.FromArgb(255, shade, shade, shade));
        }
    }

    private static bool RasterEquivalent(TerminalFrame previous, TerminalFrame frame) =>
        ReferenceEquals(previous.Cells, frame.Cells) && previous.Columns == frame.Columns && previous.Rows == frame.Rows &&
        previous.Viewport.TopInset == frame.Viewport.TopInset && previous.Background == frame.Background &&
        previous.SelectionBackground == frame.SelectionBackground && previous.SelectionForeground == frame.SelectionForeground &&
        ReferenceEquals(previous.Images, frame.Images) && ReferenceEquals(previous.SearchHighlights, frame.SearchHighlights) &&
        previous.OverscanAbove.AsSpan().SequenceEqual(frame.OverscanAbove) &&
        previous.OverscanBelow.AsSpan().SequenceEqual(frame.OverscanBelow);

    internal sealed class Rasterizer
    {
        // IDXGIKeyedMutex has no wait handle, and a blocking AcquireSync on the presentation device stalls composition.
        private static readonly TimeSpan HandoffRetry = TimeSpan.FromMilliseconds(16);
        private readonly object _gate = new();
        private readonly CanvasDevice _device;
        private readonly IRasterSink _sink;
        private readonly Action<Action> _post;
        private readonly Action<RasterPresentation> _presented;
        private readonly Action<Exception, bool> _failed;
        private readonly GlyphLayouts _layouts = new();
        private readonly ImageBitmaps _images = new();
        private readonly List<(int FirstRow, int EndRow, int FirstColumn, int EndColumn)> _spans = [];
        private readonly Dictionary<(int First, int End), int> _spanColumns = [];
        private readonly List<TaskCompletionSource<RasterReadback>> _reads = [];
        private RasterRequest? _pending;
        private RasterPresentation? _undelivered;
        private RasterPresentation? _handoff;
        private Timer? _retry;
        private Action? _release;
        private bool _running, _closed, _deliveryQueued;
        private bool[] _rowBlink = [];
        private TerminalFrame? _blinkFrame;
        private bool _hasBlinkingText;
        private volatile bool _released;
        private long _preparations;
        private CanvasRenderTarget? _raster;
        private RasterRequest? _painted;
        private BitmapSize _published;

        internal Rasterizer(CanvasDevice device, IRasterSink sink, Action<Action> post,
            Action<RasterPresentation> presented, Action<Exception, bool> failed) =>
            (_device, _sink, _post, _presented, _failed) = (device, sink, post, presented, failed);

        internal long Preparations => Interlocked.Read(ref _preparations);
        internal bool Released => _released;
        internal bool Running { get { lock (_gate) return _running; } }

        internal void Submit(RasterRequest request)
        {
            lock (_gate)
            {
                if (_closed) return;
                _pending = request;
                if (_running) return;
                _running = true;
            }
            ThreadPool.UnsafeQueueUserWorkItem(static rasterizer => rasterizer.Run(), this, false);
        }

        internal Task<RasterReadback> ReadRasterAsync()
        {
            var read = new TaskCompletionSource<RasterReadback>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_closed)
                {
                    read.SetException(new ObjectDisposedException(nameof(Rasterizer)));
                    return read.Task;
                }
                _reads.Add(read);
                if (_running) return read.Task;
                _running = true;
            }
            ThreadPool.UnsafeQueueUserWorkItem(static rasterizer => rasterizer.Run(), this, false);
            return read.Task;
        }

        internal void Close(Action release)
        {
            TaskCompletionSource<RasterReadback>[] abandoned;
            bool deferred;
            lock (_gate)
            {
                _closed = true;
                _pending = null;
                abandoned = [.. _reads];
                _reads.Clear();
                deferred = _running;
                if (deferred) _release = release;
            }
            foreach (var read in abandoned) read.TrySetException(new ObjectDisposedException(nameof(Rasterizer)));
            if (deferred) return;
            ReleaseOwned();
            release();
        }

        private void Run()
        {
            while (true)
            {
                RasterRequest? request;
                Action? release = null;
                lock (_gate)
                {
                    request = _pending;
                    _pending = null;
                    if (request is null && _reads.Count == 0)
                    {
                        _running = false;
                        release = _release;
                        _release = null;
                        if (release is null) return;
                    }
                }
                if (release is not null)
                {
                    ReleaseOwned();
                    release();
                    return;
                }
                if (request is not null && !Prepare(request)) return;
                TaskCompletionSource<RasterReadback>[] reads;
                lock (_gate)
                {
                    if (_pending is not null) continue;
                    reads = [.. _reads];
                    _reads.Clear();
                }
                if (reads.Length != 0) Read(reads);
            }
        }

        private void Read(TaskCompletionSource<RasterReadback>[] reads)
        {
            bool closed;
            lock (_gate) closed = _closed;
            foreach (var read in reads)
            {
                if (closed) read.TrySetException(new ObjectDisposedException(nameof(Rasterizer)));
                else if (_painted is not { } painted || _raster is null)
                    read.TrySetException(new InvalidOperationException("No terminal raster has been prepared."));
                else
                {
                    try { read.TrySetResult(new RasterReadback(painted, _raster.GetPixelBytes())); }
                    catch (Exception error) { read.TrySetException(error); }
                }
            }
        }

        private bool Prepare(RasterRequest request)
        {
            try
            {
                var (presentation, presented) = Render(request);
                if (!presented)
                {
                    _handoff = presentation;
                    _retry ??= new Timer(static rasterizer => ((Rasterizer)rasterizer!).ResumeHandoff(), this,
                        Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    _retry.Change(HandoffRetry, Timeout.InfiniteTimeSpan);
                    return false;
                }
                Complete(presentation);
            }
            catch (Exception error) { Fail(error); }
            return true;
        }

        private void ResumeHandoff()
        {
            bool closed;
            lock (_gate) closed = _closed;
            if (!closed)
            {
                try
                {
                    if (!_sink.TryCompletePresent())
                    {
                        _retry!.Change(HandoffRetry, Timeout.InfiniteTimeSpan);
                        return;
                    }
                    Complete(_handoff!);
                }
                catch (Exception error) { Fail(error); }
            }
            _handoff = null;
            Run();
        }

        private void Fail(Exception error)
        {
            var lost = _device.IsDeviceLost(error.HResult) || _sink.IsDeviceLost(error.HResult);
            TaskCompletionSource<RasterReadback>[] abandoned;
            lock (_gate)
            {
                _closed = true;
                _pending = null;
                _undelivered = null;
                abandoned = [.. _reads];
                _reads.Clear();
            }
            foreach (var read in abandoned) read.TrySetException(error);
            _post(() => _failed(error, lost));
        }

        private void Complete(RasterPresentation presentation)
        {
            lock (_gate)
            {
                if (_closed) return;
                _undelivered = presentation;
                if (_deliveryQueued) return;
                _deliveryQueued = true;
            }
            _post(Deliver);
        }

        private void Deliver()
        {
            RasterPresentation? presentation;
            lock (_gate)
            {
                presentation = _closed ? null : _undelivered;
                _undelivered = null;
                _deliveryQueued = false;
            }
            if (presentation is not null) _presented(presentation);
        }

        private void ScanBlink(TerminalFrame frame)
        {
            var previous = _blinkFrame;
            if (ReferenceEquals(previous?.Cells, frame.Cells)) return;
            var reuse = previous is not null && previous.Rows == frame.Rows && previous.Columns == frame.Columns;
            if (!reuse) _rowBlink = new bool[frame.Rows];
            for (var row = 0; row < frame.Rows; row++)
            {
                if (reuse && previous!.Cells.SharesRowWith(frame.Cells, row)) continue;
                _rowBlink[row] = false;
                for (var column = 0; column < frame.Columns; column++)
                    if (frame.Cells[row * frame.Columns + column] is { Blink: true, Hidden: false })
                    {
                        _rowBlink[row] = true;
                        break;
                    }
            }
            _hasBlinkingText = Array.IndexOf(_rowBlink, true) >= 0;
            _blinkFrame = frame;
        }

        private (RasterPresentation Presentation, bool Presented) Render(RasterRequest request)
        {
            var started = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _preparations);
            var (frame, metrics) = (request.Frame, request.Metrics);
            if (_raster is null || _painted?.Metrics is not { } last || last.Width != metrics.Width ||
                last.Height != metrics.Height || last.Dpi != metrics.Dpi)
            {
                _raster?.Dispose();
                _raster = null;
                _painted = null;
                _raster = _sink.CreateRaster(metrics.Width, metrics.Height, metrics.Dpi);
            }
            var raster = _raster;
            var previous = _painted;
            var phaseChanged = previous is not null && previous.TextPhase != request.TextPhase;
            ScanBlink(frame);
            var full = previous is null || previous.Metrics != metrics || frame.OverscanAbove.Length != 0 || frame.OverscanBelow.Length != 0 ||
                previous.Frame.OverscanAbove.Length != 0 || previous.Frame.OverscanBelow.Length != 0 ||
                previous.Frame.Columns != frame.Columns || previous.Frame.Rows != frame.Rows ||
                previous.Frame.Viewport.TopInset != frame.Viewport.TopInset || previous.Frame.Background != frame.Background ||
                previous.Frame.SelectionBackground != frame.SelectionBackground || previous.Frame.SelectionForeground != frame.SelectionForeground ||
                !ReferenceEquals(previous.Frame.SearchHighlights, frame.SearchHighlights);
            _layouts.Configure(metrics.FontFamily, metrics.FontSize, metrics.CellWidth, metrics.CellHeight);
            _layouts.FitGrid(frame.Cells.Length);
            PixelBounds? dirty = null;
            if (full)
            {
                using (var drawing = raster.CreateDrawingSession())
                    DrawFrameCore(drawing, frame, metrics.CellWidth, metrics.CellHeight, _layouts, false, false, metrics.FontSize,
                        metrics.Transparent, metrics.FontFamily, true, metrics.GridTop, request.TextPhase, _images, metrics.Height);
                _images.Trim(frame.Images);
                dirty = PixelBounds.Of(raster.SizeInPixels);
            }
            else if (!ReferenceEquals(previous!.Frame.Cells, frame.Cells) || !ReferenceEquals(previous.Frame.Images, frame.Images) ||
                phaseChanged && _hasBlinkingText)
                dirty = RepaintDamage(raster, previous.Frame, request, phaseChanged);
            _painted = request;
            var prepared = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var presented = Publish(raster, dirty);
            var (layouts, cells, capacity, created) = _layouts.Usage;
            return (new RasterPresentation(request, new RasterStats(layouts, cells, capacity, created, _images.Bytes, _images.Requests),
                prepared, _hasBlinkingText), presented);
        }

        private PixelBounds? RepaintDamage(CanvasRenderTarget raster, TerminalFrame previous, RasterRequest request, bool phaseChanged)
        {
            var (frame, metrics) = (request.Frame, request.Metrics);
            var (cellWidth, cellHeight, top) = (metrics.CellWidth, metrics.CellHeight, metrics.GridTop);
            var scale = metrics.Dpi / 96;
            var rows = TerminalGridGeometry.VisibleRows(frame);
            var spans = _spans;
            var latest = _spanColumns;
            spans.Clear();
            latest.Clear();
            if (!ReferenceEquals(previous.Images, frame.Images))
                AddImageDamage(previous.Images, frame.Images, frame.Columns, rows, cellWidth, cellHeight, top, spans);
            for (var row = 0; row < rows; row++)
            {
                if (previous.Cells.SharesRowWith(frame.Cells, row) && !(phaseChanged && _rowBlink[row])) continue;
                for (var col = 0; col < frame.Columns;)
                {
                    if (!CellNeedsRepaint(previous.Cells[row * frame.Columns + col], frame.Cells[row * frame.Columns + col], phaseChanged)) { col++; continue; }
                    var first = col;
                    while (first > 0 && (HasText(previous, row, first) || HasText(frame, row, first)) &&
                        (HasText(previous, row, first - 1) || HasText(frame, row, first - 1))) first--;
                    first = Math.Min(first, Math.Max(0, col - 2));
                    var lastChanged = col++;
                    while (col < frame.Columns && col <= lastChanged + 4)
                    {
                        if (CellNeedsRepaint(previous.Cells[row * frame.Columns + col], frame.Cells[row * frame.Columns + col], phaseChanged)) lastChanged = col;
                        col++;
                    }
                    var end = lastChanged + 1;
                    while (end < frame.Columns && (HasText(previous, row, end - 1) || HasText(frame, row, end - 1)) &&
                        (HasText(previous, row, end) || HasText(frame, row, end))) end++;
                    end = Math.Max(end, Math.Min(frame.Columns, lastChanged + 3));
                    col = Math.Max(col, end);
                    if (latest.TryGetValue((first, end), out var previousSpan) && spans[previousSpan].EndRow == row)
                    {
                        var span = spans[previousSpan];
                        spans[previousSpan] = (span.FirstRow, row + 1, first, end);
                    }
                    else
                    {
                        latest[(first, end)] = spans.Count;
                        spans.Add((row, row + 1, first, end));
                    }
                }
            }
            if (spans.Count == 0) return null;
            PixelBounds? dirty = null;
            using var drawing = raster.CreateDrawingSession();
            foreach (var span in spans)
            {
                var left = MathF.Floor(span.FirstColumn * cellWidth * scale) / scale;
                var right = MathF.Ceiling(span.EndColumn * cellWidth * scale) / scale;
                var upper = MathF.Floor((top + span.FirstRow * cellHeight) * scale) / scale;
                var lower = MathF.Ceiling((top + span.EndRow * cellHeight) * scale) / scale;
                var damage = new Rect(left, upper, right - left, lower - upper);
                using (var clip = drawing.CreateLayer(1, damage))
                {
                    drawing.Blend = CanvasBlend.Copy;
                    drawing.FillRectangle(damage, metrics.Transparent ? Microsoft.UI.Colors.Transparent : ToColor(frame.Background));
                    drawing.Blend = CanvasBlend.SourceOver;
                    DrawCells(drawing, frame, cellWidth, cellHeight, _layouts, top,
                        Math.Max(0, (int)MathF.Floor((upper - top) / cellHeight)),
                        Math.Min(rows, (int)MathF.Ceiling((lower - top) / cellHeight)),
                        Math.Max(0, (int)MathF.Floor(left / cellWidth) - 1),
                        Math.Min(frame.Columns, (int)MathF.Ceiling(right / cellWidth) + 1), request.TextPhase, _images, damageClip: damage);
                }
                dirty = PixelBounds.Union(dirty, new PixelBounds((int)MathF.Floor(left * scale), (int)MathF.Floor(upper * scale),
                    (int)MathF.Ceiling(right * scale), (int)MathF.Ceiling(lower * scale)));
            }
            return dirty;
        }

        private bool Publish(CanvasRenderTarget raster, PixelBounds? dirty)
        {
            var size = raster.SizeInPixels;
            if (size.Width != _published.Width || size.Height != _published.Height)
            {
                dirty = PixelBounds.Of(size);
                _published = size;
            }
            if (dirty?.Within(size) is not { IsEmpty: false } rect) return true;
            return _sink.Present(raster, new Rect(rect.Left, rect.Top, rect.Width, rect.Height));
        }

        private void ReleaseOwned()
        {
            _raster?.Dispose();
            _raster = null;
            _painted = null;
            _blinkFrame = null;
            _rowBlink = [];
            _layouts.Clear();
            _images.Clear();
            _retry?.Dispose();
            _retry = null;
            _released = true;
        }
    }

    private readonly record struct PixelBounds(int Left, int Top, int Right, int Bottom)
    {
        internal int Width => Right - Left;
        internal int Height => Bottom - Top;
        internal bool IsEmpty => Width <= 0 || Height <= 0;

        internal static PixelBounds Of(BitmapSize size) => new(0, 0, (int)size.Width, (int)size.Height);

        internal static PixelBounds? Union(PixelBounds? a, PixelBounds? b) =>
            a is not { } x ? b : b is not { } y ? x :
            new(Math.Min(x.Left, y.Left), Math.Min(x.Top, y.Top), Math.Max(x.Right, y.Right), Math.Max(x.Bottom, y.Bottom));

        internal PixelBounds Within(BitmapSize size) =>
            new(Math.Clamp(Left, 0, (int)size.Width), Math.Clamp(Top, 0, (int)size.Height),
                Math.Clamp(Right, 0, (int)size.Width), Math.Clamp(Bottom, 0, (int)size.Height));
    }
}
