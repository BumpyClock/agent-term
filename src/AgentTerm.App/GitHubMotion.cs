using System;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AgentTerm_App;

// Compositor-driven motion for the GitHub dashboard. Only Opacity, Translation, Scale, and rotation of handoff visuals
// are animated, and XAML never sets those properties on the same elements. With Windows animations off, every helper
// applies its end state immediately.
internal static class GitHubMotion
{
    public static readonly TimeSpan Short = TimeSpan.FromMilliseconds(120);
    public static readonly TimeSpan Standard = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan Entrance = TimeSpan.FromMilliseconds(240);

    private static readonly UISettings Settings = new();

    public static bool Enabled => Settings.AnimationsEnabled;

    public static Visual Of(UIElement element) => ElementCompositionPreview.GetElementVisual(element);

    public static CompositionEasingFunction Decelerate(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

    public static CompositionEasingFunction PointToPoint(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.55f, 0.55f), new Vector2(0f, 1f));

    public static Visual Centered(UIElement element)
    {
        var visual = Of(element);
        var center = visual.Compositor.CreateExpressionAnimation("Vector3(this.Target.Size.X / 2, this.Target.Size.Y / 2, 0)");
        visual.StartAnimation("CenterPoint", center);
        return visual;
    }

    public static void EnableTranslation(UIElement element) => ElementCompositionPreview.SetIsTranslationEnabled(element, true);

    public static void Enter(UIElement element, TimeSpan delay, float rise = 8, float slide = 0)
    {
        EnableTranslation(element);
        var visual = Of(element);
        if (!Enabled)
        {
            Settle(visual, 1, Vector3.Zero);
            return;
        }
        var compositor = visual.Compositor;
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, 0);
        opacity.InsertKeyFrame(1, 1, Decelerate(compositor));
        opacity.Duration = Entrance;
        opacity.DelayTime = delay;
        opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        var translation = compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(0, new Vector3(slide, rise, 0));
        translation.InsertKeyFrame(1, Vector3.Zero, Decelerate(compositor));
        translation.Duration = Entrance;
        translation.DelayTime = delay;
        translation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Translation", translation);
    }

    public static void FadeTo(UIElement element, float to, TimeSpan duration, Action? completed = null)
    {
        var visual = Of(element);
        if (!Enabled)
        {
            visual.StopAnimation("Opacity");
            visual.Opacity = to;
            completed?.Invoke();
            return;
        }
        var compositor = visual.Compositor;
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, to, Decelerate(compositor));
        animation.Duration = duration;
        Run(visual, "Opacity", animation, completed);
    }

    public static void Show(UIElement element, float to = 1)
    {
        var visual = Of(element);
        visual.StopAnimation("Opacity");
        visual.Opacity = to;
    }

    public static void Slide(UIElement element, Vector3 to, TimeSpan duration)
    {
        EnableTranslation(element);
        var visual = Of(element);
        if (!Enabled)
        {
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", to);
            return;
        }
        var compositor = visual.Compositor;
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, to, PointToPoint(compositor));
        animation.Duration = duration;
        visual.StartAnimation("Translation", animation);
    }

    public static void Place(UIElement element, Vector3 to)
    {
        EnableTranslation(element);
        var visual = Of(element);
        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", to);
    }

    public static void ScaleTo(UIElement element, float to, TimeSpan duration)
    {
        var visual = Centered(element);
        if (!Enabled)
        {
            visual.StopAnimation("Scale");
            visual.Scale = new Vector3(to, to, 1);
            return;
        }
        var compositor = visual.Compositor;
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, new Vector3(to, to, 1), Decelerate(compositor));
        animation.Duration = duration;
        visual.StartAnimation("Scale", animation);
    }

    // Keeps the follower at the leader's scale, so an avatar that covers another picks up its press animation midway.
    // The follower's own next ScaleTo replaces the binding.
    public static void FollowScale(UIElement follower, UIElement leader)
    {
        var visual = Centered(follower);
        var scale = visual.Compositor.CreateExpressionAnimation("leader.Scale");
        scale.SetReferenceParameter("leader", Centered(leader));
        visual.StartAnimation("Scale", scale);
    }

    public static void AttachPress(UIElement source, UIElement target, float pressed = 0.9f)
    {
        void Down(object sender, PointerRoutedEventArgs args) => ScaleTo(target, pressed, TimeSpan.FromMilliseconds(90));
        void Up(object sender, PointerRoutedEventArgs args) => ScaleTo(target, 1, TimeSpan.FromMilliseconds(180));
        source.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Down), true);
        foreach (var routed in new[] { UIElement.PointerReleasedEvent, UIElement.PointerCanceledEvent,
            UIElement.PointerCaptureLostEvent, UIElement.PointerExitedEvent })
            source.AddHandler(routed, new PointerEventHandler(Up), true);
    }

    public static void Bar(FrameworkElement track, Color color, double from, double to)
    {
        var host = Of(track);
        var compositor = host.Compositor;
        var bar = compositor.CreatePropertySet();
        bar.InsertScalar("Fraction", (float)to);
        var shape = compositor.CreateRoundedRectangleGeometry();
        shape.CornerRadius = new Vector2(2);
        var size = compositor.CreateExpressionAnimation("Vector2(host.Size.X * bar.Fraction, host.Size.Y)");
        size.SetReferenceParameter("host", host);
        size.SetReferenceParameter("bar", bar);
        shape.StartAnimation("Size", size);
        var visual = compositor.CreateShapeVisual();
        var sprite = compositor.CreateSpriteShape(shape);
        sprite.FillBrush = compositor.CreateColorBrush(color);
        visual.Shapes.Add(sprite);
        var bounds = compositor.CreateExpressionAnimation("host.Size");
        bounds.SetReferenceParameter("host", host);
        visual.StartAnimation("Size", bounds);
        ElementCompositionPreview.SetElementChildVisual(track, visual);
        if (!Enabled || from == to) return;
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, (float)from);
        animation.InsertKeyFrame(1, (float)to, Decelerate(compositor));
        animation.Duration = TimeSpan.FromMilliseconds(320);
        bar.StartAnimation("Fraction", animation);
    }

    private static void Settle(Visual visual, float opacity, Vector3 translation)
    {
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        visual.Opacity = opacity;
        visual.Properties.InsertVector3("Translation", translation);
    }

    private static void Run(Visual visual, string property, CompositionAnimation animation, Action? completed)
    {
        if (completed is null)
        {
            visual.StartAnimation(property, animation);
            return;
        }
        var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation(property, animation);
        batch.End();
        batch.Completed += (_, _) => completed();
    }
}

internal sealed class GitHubReveal
{
    private static readonly TimeSpan Grow = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan Fold = TimeSpan.FromMilliseconds(160);

    private Visual? _visual;
    private CompositionPropertySet? _state;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private Action? _done;
    private int _frames;
    private bool _armed;

    public bool Folding { get; private set; }

    public bool CanFold => _state is not null && GitHubMotion.Enabled;

    public void Arm()
    {
        _armed = true;
        if (_state is not null && GitHubMotion.Enabled) Set(0);
    }

    public void Prepare(UIElement panel, float top, float right, float size, float corner)
    {
        if (!_armed || !GitHubMotion.Enabled) return;
        var visual = GitHubMotion.Of(panel);
        if (!ReferenceEquals(visual, _visual)) Attach(visual);
        _state!.InsertScalar("Top", top);
        _state.InsertScalar("Right", right);
        _state.InsertScalar("Size", size);
        _state.InsertScalar("Corner", corner);
        Set(0);
    }

    // Plays the growth once per Arm, starting on the second frame after the panel opens. The first layout of the
    // panel's content can take longer than the whole growth, so a growth started at once would mostly play unseen.
    public void Open()
    {
        if (!_armed) return;
        _armed = false;
        if (_state is null) return;
        if (!GitHubMotion.Enabled)
        {
            Set(1);
            return;
        }
        _frames = 0;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += Rendering;
    }

    private void Rendering(object? sender, object args)
    {
        if (++_frames < 2) return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= Rendering;
        var compositor = _visual!.Compositor;
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, 0);
        animation.InsertKeyFrame(1, 1, compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f)));
        animation.Duration = Grow;
        _state!.StartAnimation("Progress", animation);
    }

    // Folds back from wherever the growth is, then calls done. A timer rather than a batch ends the fold, so a hidden
    // or minimized window that stops rendering frames still closes on time.
    public void Close(Action done)
    {
        Folding = true;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= Rendering;
        var compositor = _visual!.Compositor;
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, 0, compositor.CreateCubicBezierEasingFunction(new Vector2(0.7f, 0f), new Vector2(1f, 0.5f)));
        animation.Duration = Fold;
        _state!.StartAnimation("Progress", animation);
        _done = done;
        if (_timer is null)
        {
            _timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => _done?.Invoke();
        }
        _timer.Interval = Fold;
        _timer.Start();
    }

    public void Stop()
    {
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= Rendering;
        Folding = false;
        _done = null;
        _timer?.Stop();
    }

    private void Set(float progress)
    {
        _state!.StopAnimation("Progress");
        _state.InsertScalar("Progress", progress);
    }

    private void Attach(Visual visual)
    {
        _visual = visual;
        var compositor = visual.Compositor;
        _state = compositor.CreatePropertySet();
        foreach (var name in new[] { "Progress", "Top", "Right", "Size", "Corner" }) _state.InsertScalar(name, 0);
        var shape = compositor.CreateRoundedRectangleGeometry();
        void Bind(string property, string expression)
        {
            var animation = compositor.CreateExpressionAnimation(expression);
            animation.SetReferenceParameter("host", visual);
            animation.SetReferenceParameter("s", _state);
            shape.StartAnimation(property, animation);
        }
        Bind("Offset", "Vector2(Lerp(host.Size.X - s.Right - s.Size, 0, s.Progress), Lerp(s.Top, 0, s.Progress))");
        Bind("Size", "Vector2(Lerp(s.Size, host.Size.X, s.Progress), Lerp(s.Size, host.Size.Y, s.Progress))");
        Bind("CornerRadius", "Vector2(Lerp(s.Size / 2, s.Corner, s.Progress), Lerp(s.Size / 2, s.Corner, s.Progress))");
        visual.Clip = compositor.CreateGeometricClip(shape);
    }
}

internal sealed class GitHubSpinner(UIElement icon)
{
    private static readonly TimeSpan Turn = TimeSpan.FromMilliseconds(900);
    private long _started;

    public bool Spinning { get; private set; }

    public void Start()
    {
        if (Spinning) return;
        Spinning = true;
        if (!GitHubMotion.Enabled) return;
        var visual = Prepare();
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, 0);
        animation.InsertKeyFrame(1, 360, visual.Compositor.CreateLinearEasingFunction());
        animation.Duration = Turn;
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("RotationAngleInDegrees", animation);
        _started = Stopwatch.GetTimestamp();
    }

    public void Stop()
    {
        if (!Spinning) return;
        Spinning = false;
        var visual = Prepare();
        visual.StopAnimation("RotationAngleInDegrees");
        if (!GitHubMotion.Enabled || _started == 0)
        {
            visual.RotationAngleInDegrees = 0;
            return;
        }
        var turn = Turn.TotalMilliseconds;
        var phase = Stopwatch.GetElapsedTime(_started).TotalMilliseconds % turn / turn;
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, (float)(phase * 360));
        animation.InsertKeyFrame(1, 360, GitHubMotion.Decelerate(visual.Compositor));
        animation.Duration = TimeSpan.FromMilliseconds(Math.Max(240, (1 - phase) * turn * 1.6));
        visual.StartAnimation("RotationAngleInDegrees", animation);
        _started = 0;
    }

    private Visual Prepare() => GitHubMotion.Centered(icon);
}

internal sealed class GitHubShimmer
{
    private readonly Compositor _compositor;
    private readonly CompositionLinearGradientBrush _brush;
    private readonly CompositionColorGradientStop _middle;
    private readonly CompositionColorGradientStop[] _edges;
    private bool _running;

    public GitHubShimmer(Compositor compositor)
    {
        _compositor = compositor;
        _brush = compositor.CreateLinearGradientBrush();
        _brush.ExtendMode = CompositionGradientExtendMode.Clamp;
        _edges = [compositor.CreateColorGradientStop(0, Colors(0)), compositor.CreateColorGradientStop(1, Colors(0))];
        _middle = compositor.CreateColorGradientStop(0.5f, Colors(0));
        _brush.ColorStops.Add(_edges[0]);
        _brush.ColorStops.Add(_middle);
        _brush.ColorStops.Add(_edges[1]);
        Park();
    }

    public void SetColor(Color highlight)
    {
        _middle.Color = highlight;
        foreach (var edge in _edges) edge.Color = Color.FromArgb(0, highlight.R, highlight.G, highlight.B);
    }

    public void Attach(FrameworkElement block, float radius)
    {
        var host = GitHubMotion.Of(block);
        var sprite = _compositor.CreateSpriteVisual();
        sprite.Brush = _brush;
        var size = _compositor.CreateExpressionAnimation("host.Size");
        size.SetReferenceParameter("host", host);
        sprite.StartAnimation("Size", size);
        var shape = _compositor.CreateRoundedRectangleGeometry();
        shape.CornerRadius = new Vector2(radius);
        shape.StartAnimation("Size", size);
        sprite.Clip = _compositor.CreateGeometricClip(shape);
        ElementCompositionPreview.SetElementChildVisual(block, sprite);
    }

    public void Start()
    {
        if (_running || !GitHubMotion.Enabled) return;
        _running = true;
        Sweep("StartPoint", -0.6f, 1.1f);
        Sweep("EndPoint", 0f, 1.7f);
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _brush.StopAnimation("StartPoint");
        _brush.StopAnimation("EndPoint");
        Park();
    }

    private void Park()
    {
        _brush.StartPoint = new Vector2(-0.6f, 0.5f);
        _brush.EndPoint = new Vector2(0f, 0.5f);
    }

    private void Sweep(string property, float from, float to)
    {
        var animation = _compositor.CreateVector2KeyFrameAnimation();
        animation.InsertKeyFrame(0, new Vector2(from, 0.5f));
        animation.InsertKeyFrame(0.75f, new Vector2(to, 0.5f), _compositor.CreateCubicBezierEasingFunction(new(0.4f, 0f), new(0.6f, 1f)));
        animation.InsertKeyFrame(1, new Vector2(to, 0.5f));
        animation.Duration = TimeSpan.FromMilliseconds(1600);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        _brush.StartAnimation(property, animation);
    }

    private static Color Colors(byte alpha) => Color.FromArgb(alpha, 255, 255, 255);
}
