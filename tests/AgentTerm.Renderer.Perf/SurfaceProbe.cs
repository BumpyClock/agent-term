using System.Diagnostics;
using System.Reflection;
using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.Graphics.Canvas;

internal sealed class SurfaceProbe(TerminalSurface surface)
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly PropertyInfo? PresentedFrameProperty = typeof(TerminalSurface).GetProperty("PresentedFrame", Private);
    private static readonly FieldInfo? PaintedFrameField = typeof(TerminalSurface).GetField("_paintedFrame", Private);
    private static readonly PropertyInfo? CurrentProperty = typeof(TerminalSurface).GetProperty("RasterPresentationCurrent", Private);
    private static readonly PropertyInfo? ResourcesProperty = typeof(TerminalSurface).GetProperty("HasRenderResources", Private);
    private static readonly PropertyInfo? DeviceProperty = typeof(TerminalSurface).GetProperty("RasterDevice", Private);
    private static readonly MethodInfo? ReadMethod = typeof(TerminalSurface).GetMethod("ReadPresentedRasterAsync", Private);
    private static readonly FieldInfo? LegacyRasterField = typeof(TerminalSurface).GetField("_renderTarget", Private);
    private static readonly EventInfo? PresentedEvent = typeof(TerminalSurface).GetEvent("DrawCompleted", Private);

    internal static bool WorkerOwned => ReadMethod is not null && CurrentProperty is not null;
    internal static bool ReportsPresentation => PresentedEvent is not null;

    internal TerminalFrame? Presented => (TerminalFrame?)(PresentedFrameProperty?.GetValue(surface) ?? PaintedFrameField?.GetValue(surface));
    internal bool Current => CurrentProperty is null || (bool)CurrentProperty.GetValue(surface)!;
    internal bool HasResources => ResourcesProperty is not null && (bool)ResourcesProperty.GetValue(surface)!;
    internal CanvasDevice? Device => DeviceProperty?.GetValue(surface) as CanvasDevice;
    internal float Dpi => (float)(96 * surface.XamlRoot.RasterizationScale);

    internal void OnPresented(Action<double> handler) => PresentedEvent?.GetAddMethod(true)?.Invoke(surface, [handler]);
    internal void OffPresented(Action<double> handler) => PresentedEvent?.GetRemoveMethod(true)?.Invoke(surface, [handler]);

    internal sealed record Readback(byte[] Pixels, TerminalFrame? Frame, float Dpi, float FontSize, bool Transparent, float GridTop, bool TextPhase);

    internal async Task<Readback?> ReadRasterAsync()
    {
        if (ReadMethod is null)
            return (LegacyRasterField?.GetValue(surface) as CanvasRenderTarget)?.GetPixelBytes() is { } legacy
                ? new Readback(legacy, null, Dpi, 0, false, 0, true) : null;
        var task = (Task)ReadMethod.Invoke(surface, null)!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var request = Read<object>(result, "Request");
        var metrics = Read<object>(request, "Metrics");
        return new Readback(Read<byte[]>(result, "Pixels"), Read<TerminalFrame>(request, "Frame"), Read<float>(metrics, "Dpi"),
            Read<float>(metrics, "FontSize"), Read<bool>(metrics, "Transparent"), Read<float>(metrics, "GridTop"), Read<bool>(request, "TextPhase"));

        static T Read<T>(object owner, string name) => (T)owner.GetType().GetProperty(name)!.GetValue(owner)!;
    }

    internal async Task WhenPresentedAsync(TerminalFrame frame, string name, TimeSpan? patience = null)
    {
        if (!ReportsPresentation) throw new InvalidOperationException("This renderer does not report presentation.");
        var deadline = Stopwatch.StartNew();
        var limit = patience ?? TimeSpan.FromSeconds(5);
        while (!(Current && ReferenceEquals(Presented, frame)))
        {
            var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<double> handler = _ => presented.TrySetResult();
            OnPresented(handler);
            try
            {
                if (Current && ReferenceEquals(Presented, frame)) return;
                var remaining = limit - deadline.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                await presented.Task.WaitAsync(remaining);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"{name} was not presented within {limit.TotalSeconds:0.#} s. resources={HasResources} current={Current} presentedIsTarget={ReferenceEquals(Presented, frame)}.");
            }
            finally { OffPresented(handler); }
        }
    }
}
