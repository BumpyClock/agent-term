using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace AgentTerm_App;

internal sealed record ActivityOverlayGeometry(Vector2 Position, Vector2 Size, Rect Clip, Rect[] Elements, float TravelFrom, float TravelTo)
{
    internal bool Matches(ActivityOverlayGeometry other) => Position == other.Position && Size == other.Size && Clip == other.Clip &&
        TravelFrom == other.TravelFrom && TravelTo == other.TravelTo && Elements.SequenceEqual(other.Elements);
}

internal sealed class ProgramActivityOverlay : IDisposable
{
    private readonly ProgramActivityKind _kind;
    private readonly ActivityOverlayHost _host;
    private ActivityOverlayGeometry? _geometry;
    private ContainerVisual? _tree;
    private CompositionColorBrush? _inkBrush;
    private CompositionLinearGradientBrush? _edgeBrush;
    private SpriteVisual? _edgeBeam;
    private Color _ink, _accent;
    private readonly List<CompositionObject> _resources = [];
    private readonly List<CanvasGeometry> _paths = [];
    private readonly List<(Visual Visual, string Property)> _animations = [];
    private bool _disposed;
    internal bool IsReady => _tree is not null;

    internal ProgramActivityOverlay(ProgramActivityKind kind, XamlRoot root)
    {
        _kind = kind;
        _host = ActivityOverlayHost.Acquire(root);
    }

    internal void SetColors(Color ink, Color accent)
    {
        _ink = ink;
        _accent = accent;
        if (_inkBrush is not null) _inkBrush.Color = ink;
        if (_edgeBrush is not null)
        {
            _edgeBrush.ColorStops[0].Color = Color.FromArgb(0, accent.R, accent.G, accent.B);
            _edgeBrush.ColorStops[1].Color = accent;
            _edgeBrush.ColorStops[2].Color = Color.FromArgb(0, accent.R, accent.G, accent.B);
        }
    }

    internal void SetGeometry(ActivityOverlayGeometry geometry, double phaseSeconds)
    {
        if (_disposed || _host.Compositor is not { } compositor || _geometry?.Matches(geometry) == true) return;
        if (_tree is not null && _geometry is { } previous && previous.Size == geometry.Size &&
            previous.TravelFrom == geometry.TravelFrom && previous.TravelTo == geometry.TravelTo && previous.Elements.SequenceEqual(geometry.Elements))
        {
            _geometry = geometry;
            PlaceTree();
            return;
        }
        // A resizing pane changes the beam's size and travel on every layout pass. Update the
        // existing visual instead of rebuilding the graph, which also restarts its animation.
        if (_tree is not null && _edgeBeam is not null && geometry.Elements.Length == 1)
        {
            var travelChanged = _geometry!.TravelFrom != geometry.TravelFrom || _geometry.TravelTo != geometry.TravelTo;
            _geometry = geometry;
            PlaceBeam(geometry.Elements[0]);
            if (travelChanged) StartBeam(compositor, phaseSeconds);
            PlaceTree();
            return;
        }
        ClearGraph();
        _geometry = geometry;
        try
        {
            _tree = Own(compositor.CreateContainerVisual());
            _tree.Clip = Own(compositor.CreateInsetClip());
            using var linear = compositor.CreateLinearEasingFunction();
            if (_kind == ProgramActivityKind.TabIcon)
            {
                _inkBrush = Own(compositor.CreateColorBrush(_ink));
                for (var diagonal = 0; diagonal < 9; diagonal++)
                {
                    var visual = Own(compositor.CreateShapeVisual());
                    visual.Size = geometry.Size;
                    for (var index = 0; index < geometry.Elements.Length; index++)
                    {
                        if (index % 5 + index / 5 != diagonal) continue;
                        var bounds = geometry.Elements[index];
                        var path = CanvasGeometry.CreateEllipse(null,
                            (float)(bounds.X + bounds.Width / 2), (float)(bounds.Y + bounds.Height / 2),
                            (float)bounds.Width / 2, (float)bounds.Height / 2);
                        _paths.Add(path);
                        var ellipse = Own(compositor.CreatePathGeometry(new CompositionPath(path)));
                        var shape = Own(compositor.CreateSpriteShape(ellipse));
                        shape.FillBrush = _inkBrush;
                        visual.Shapes.Add(shape);
                    }
                    _tree.Children.InsertAtTop(visual);
                    using var animation = compositor.CreateScalarKeyFrameAnimation();
                    animation.Duration = TimeSpan.FromSeconds(ProgramActivity.SweepCycle);
                    animation.IterationBehavior = AnimationIterationBehavior.Forever;
                    var shift = diagonal * ProgramActivity.SweepStep;
                    var initial = (float)ProgramActivity.OpacityAt((1 - shift) % 1);
                    animation.InsertKeyFrame(0, initial, linear);
                    foreach (var phase in ProgramActivity.SweepPhases)
                    {
                        var time = (phase + shift) % 1;
                        if (time != 0) animation.InsertKeyFrame((float)time, (float)ProgramActivity.OpacityAt(phase), linear);
                    }
                    animation.InsertKeyFrame(1, initial, linear);
                    visual.StartAnimation("Opacity", animation);
                    _animations.Add((visual, "Opacity"));
                }
            }
            else
            {
                var beam = _edgeBeam = Own(compositor.CreateSpriteVisual());
                PlaceBeam(geometry.Elements[0]);
                _edgeBrush = Own(compositor.CreateLinearGradientBrush());
                _edgeBrush.StartPoint = Vector2.Zero;
                _edgeBrush.EndPoint = Vector2.UnitX;
                _edgeBrush.InterpolationSpace = CompositionColorSpace.Rgb;
                var clear = Color.FromArgb(0, _accent.R, _accent.G, _accent.B);
                foreach (var (offset, color) in new[] { (0f, clear), (.5f, _accent), (1f, clear) })
                    _edgeBrush.ColorStops.Add(Own(compositor.CreateColorGradientStop(offset, color)));
                beam.Brush = _edgeBrush;
                _tree.Children.InsertAtTop(beam);
                StartBeam(compositor, phaseSeconds);
            }
            var duration = _kind == ProgramActivityKind.TabIcon ? ProgramActivity.SweepCycle : 2.4;
            foreach (var (visual, property) in _animations)
            {
                // The running animation owns its controller. Closing it after StopAnimation
                // leaves DirectComposition with a freed object and fail-fasts the process.
                var controller = visual.TryGetAnimationController(property);
                controller.Pause();
                controller.Progress = (float)(phaseSeconds / duration);
                controller.Resume();
            }
            PlaceTree();
            _host.Add(_tree);
        }
        catch (COMException error)
        {
            Debug.WriteLine($"Activity desktop overlay unavailable: {error.Message}");
            ClearGraph();
        }
    }

    private void PlaceBeam(Rect bounds)
    {
        _edgeBeam!.Size = new Vector2((float)bounds.Width, (float)bounds.Height);
        _edgeBeam.Offset = new Vector3((float)bounds.X, (float)bounds.Y, 0);
    }

    private void StartBeam(Compositor compositor, double phaseSeconds)
    {
        var beam = _edgeBeam!;
        var geometry = _geometry!;
        beam.StopAnimation("Offset.X");
        using var linear = compositor.CreateLinearEasingFunction();
        using var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromSeconds(2.4);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        animation.InsertKeyFrame(0, geometry.TravelFrom, linear);
        animation.InsertKeyFrame(1, geometry.TravelTo, linear);
        beam.StartAnimation("Offset.X", animation);
        if (!_animations.Contains((beam, "Offset.X"))) _animations.Add((beam, "Offset.X"));
        // The running animation owns its controller; do not dispose it.
        var controller = beam.TryGetAnimationController("Offset.X");
        controller.Pause();
        controller.Progress = (float)(phaseSeconds / 2.4);
        controller.Resume();
    }

    private void PlaceTree()
    {
        var geometry = _geometry!;
        _tree!.Offset = new Vector3(geometry.Position, 0);
        _tree.Size = geometry.Size;
        var clip = (InsetClip)_tree.Clip;
        clip.LeftInset = (float)geometry.Clip.X;
        clip.TopInset = (float)geometry.Clip.Y;
        clip.RightInset = geometry.Size.X - (float)(geometry.Clip.X + geometry.Clip.Width);
        clip.BottomInset = geometry.Size.Y - (float)(geometry.Clip.Y + geometry.Clip.Height);
    }

    private void ClearGraph()
    {
        _tree?.Parent?.Children.Remove(_tree);
        foreach (var (visual, property) in _animations) visual.StopAnimation(property);
        _animations.Clear();
        for (var index = _resources.Count - 1; index >= 0; index--) _resources[index].Dispose();
        _resources.Clear();
        foreach (var path in _paths) path.Dispose();
        _paths.Clear();
        _tree = null;
        _edgeBeam = null;
        _inkBrush = null;
        _edgeBrush = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearGraph();
        _host.Release();
    }

    private T Own<T>(T resource) where T : CompositionObject { _resources.Add(resource); return resource; }
}

internal sealed class ActivityOverlayHost
{
    private static readonly ConditionalWeakTable<XamlRoot, ActivityOverlayHost> Hosts = new();
    private readonly XamlRoot _root;
    private Compositor? _compositor;
    private DesktopWindowTarget? _target;
    private ContainerVisual? _visual;
    private int _references;
    internal Compositor? Compositor => _target is null ? null : _compositor;

    private ActivityOverlayHost(XamlRoot root)
    {
        _root = root;
        // The application already owns this queue. Bare hosts without one retain XAML.
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is null) return;
        try
        {
            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(root.ContentIslandEnvironment.AppWindowId);
            if (hwnd == IntPtr.Zero) return;
            _compositor = new Compositor();
            var hr = _compositor.As<ICompositorDesktopInterop>().CreateDesktopWindowTarget(hwnd, true, out var pointer);
            Marshal.ThrowExceptionForHR(hr);
            try { _target = MarshalInterface<DesktopWindowTarget>.FromAbi(pointer); }
            finally { Marshal.Release(pointer); }
            _visual = _compositor.CreateContainerVisual();
            _target.Root = _visual;
        }
        catch (COMException error)
        {
            Debug.WriteLine($"Activity desktop target unavailable: {error.Message}");
            Close();
        }
    }

    internal static ActivityOverlayHost Acquire(XamlRoot root)
    {
        var host = Hosts.GetValue(root, key => new ActivityOverlayHost(key));
        host._references++;
        return host;
    }
    internal void Add(Visual visual) => _visual!.Children.InsertAtTop(visual);
    internal void Release()
    {
        if (--_references != 0) return;
        Hosts.Remove(_root);
        Close();
    }
    private void Close()
    {
        if (_target is not null) { _target.Root = null; _target.Dispose(); }
        _visual?.Dispose();
        _compositor?.Dispose();
        _target = null;
        _visual = null;
        _compositor = null;
    }

    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        [PreserveSig] int CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out IntPtr result);
        [PreserveSig] int EnsureOnThread(uint threadId);
    }
}
