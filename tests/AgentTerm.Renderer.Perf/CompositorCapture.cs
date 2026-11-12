using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using WinRT;

internal sealed class CompositorCapture : IDisposable
{
    private readonly CanvasDevice _device = new();
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly CanvasRenderTarget _crop;
    private readonly object _gate = new();
    private readonly List<Sample> _samples = [];
    private readonly int _left, _top, _width, _height, _stableLeft, _stableWidth;
    private bool _measuring;
    private bool _disposed;
    private string? _error;
    private sealed record Sample(double SourceTimeMs, double ReadbackCompletedQpcMs, string CounterHash, string StableLabelHash);

    internal CompositorCapture(Window window, AgentTerm_App.TerminalSurface surface)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var item = CreateItem(hwnd);
        var client = new Point();
        if (!ClientToScreen(hwnd, ref client)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(hwnd, 9, out var bounds, Marshal.SizeOf<Rect>()));
        var origin = surface.TransformToVisual((UIElement)window.Content).TransformPoint(new Windows.Foundation.Point());
        var scale = surface.XamlRoot.RasterizationScale;
        var firstRowTop = surface.Geometry.Top;
        _stableLeft = (int)Math.Round(client.X - bounds.Left + origin.X * scale);
        _left = _stableLeft + (int)Math.Floor(16 * surface.CellWidth * scale);
        _top = (int)Math.Round(client.Y - bounds.Top + (origin.Y + firstRowTop) * scale);
        _width = (int)Math.Ceiling(5 * surface.CellWidth * scale);
        _height = (int)Math.Ceiling(surface.CellHeight * scale);
        _stableWidth = (int)Math.Ceiling(4 * surface.CellWidth * scale);
        _crop = new CanvasRenderTarget(_device, _left + _width - _stableLeft, _height, 96);
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, item.Size);
        _pool.FrameArrived += FrameArrived;
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = false;
        _session.StartCapture();
    }

    internal void Begin() { lock (_gate) _measuring = true; }
    internal object End(string output)
    {
        Sample[] samples;
        lock (_gate) { _measuring = false; samples = _samples.ToArray(); }
        File.WriteAllLines(Path.ChangeExtension(output, ".capture.ndjson"), samples.Select(sample => JsonSerializer.Serialize(sample)));
        var intervals = new List<double>();
        var changedIntervals = new List<double>();
        Sample? previous = null, previousChanged = null;
        var changed = 0;
        foreach (var sample in samples)
        {
            if (previous is not null) intervals.Add(sample.SourceTimeMs - previous.SourceTimeMs);
            if (previous is null || sample.CounterHash != previous.CounterHash)
            {
                changed++;
                if (previousChanged is not null) changedIntervals.Add(sample.SourceTimeMs - previousChanged.SourceTimeMs);
                previousChanged = sample;
            }
            previous = sample;
        }
        var span = samples.Length < 2 ? 0 : samples[^1].SourceTimeMs - samples[0].SourceTimeMs;
        return new
        {
            kind = "Windows.Graphics.Capture compositor delivery; not physical scanout",
            error = _error, frames = samples.Length, changedCounterFrames = changed,
            distinctCounterHashes = samples.Select(sample => sample.CounterHash).Distinct().Count(),
            stableLabelHashes = samples.Select(sample => sample.StableLabelHash).Distinct().Count(),
            dominantStableLabelFrames = samples.GroupBy(sample => sample.StableLabelHash).Select(group => group.Count()).DefaultIfEmpty().Max(),
            sourceSpanMs = span, changedFramesPerSecond = span > 0 ? (changed - 1) * 1000 / span : 0,
            sourceIntervals = Stats(intervals), changedCounterIntervals = Stats(changedIntervals),
            crop = new { left = _left, top = _top, width = _width, height = _height }
        };
    }

    private void FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                for (var buffered = 0; buffered < 3; buffered++)
                {
                    using var frame = sender.TryGetNextFrame();
                    if (frame is null) break;
                    if (!_measuring || _error is not null) continue;
                    using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device, frame.Surface);
                    using (var drawing = _crop.CreateDrawingSession())
                        drawing.DrawImage(bitmap, new Windows.Foundation.Rect(0, 0, _crop.Size.Width, _height),
                            new Windows.Foundation.Rect(_stableLeft, _top, _crop.Size.Width, _height));
                    var counter = _crop.GetPixelBytes(_left - _stableLeft, 0, _width, _height);
                    var stable = _crop.GetPixelBytes(0, 0, _stableWidth, _height);
                    _samples.Add(new(frame.SystemRelativeTime.TotalMilliseconds, Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency,
                        Convert.ToHexString(SHA256.HashData(counter)), Convert.ToHexString(SHA256.HashData(stable))));
                }
            }
            catch (Exception error) { _error = error.ToString(); }
        }
    }

    public void Dispose()
    {
        lock (_gate) { _measuring = false; _disposed = true; }
        _pool.FrameArrived -= FrameArrived;
        _session.Dispose();
        _pool.Dispose();
        _crop.Dispose();
        _device.Dispose();
    }

    private static object Stats(List<double> values)
    {
        values.Sort();
        return new { count = values.Count, medianMs = At(.5), p95Ms = At(.95), maxMs = At(1) };
        double At(double percentile) => values.Count == 0 ? 0 : values[Math.Min(values.Count - 1, (int)(percentile * values.Count))];
    }

    private static GraphicsCaptureItem CreateItem(IntPtr hwnd)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var text));
        IntPtr factory = IntPtr.Zero, item = IntPtr.Zero;
        object? wrapper = null;
        try
        {
            var factoryId = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(text, ref factoryId, out factory));
            wrapper = Marshal.GetObjectForIUnknown(factory);
            var itemId = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            ((IGraphicsCaptureItemInterop)wrapper).CreateForWindow(hwnd, ref itemId, out item);
            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            if (item != IntPtr.Zero) Marshal.Release(item);
            if (wrapper is not null) Marshal.ReleaseComObject(wrapper);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            WindowsDeleteString(text);
        }
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        void CreateForWindow(IntPtr window, ref Guid iid, out IntPtr item);
        void CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr item);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, int length, out IntPtr text);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr text);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr text, ref Guid iid, out IntPtr factory);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
}
