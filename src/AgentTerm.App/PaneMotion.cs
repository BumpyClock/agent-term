using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace AgentTerm_App;

// Only opacity and render transforms are animated. The pane's terminal keeps its final layout.
internal sealed class PaneMotion
{
    private readonly record struct Pose(double Opacity, double X, double Y, double ScaleX, double ScaleY);
    private sealed record Running(Storyboard Storyboard, TaskCompletionSource Completion);

    private static readonly Pose Rest = new(1, 0, 0, 1, 1);
    private readonly Dictionary<FrameworkElement, Running> _running = [];

    internal void Enter(FrameworkElement pane) =>
        _ = Start(pane, new Pose(0, pane.ActualWidth * .01, pane.ActualHeight * .01 + 5, .98, .98),
            Rest, 200, EasingMode.EaseOut);

    internal Task ExitAsync(FrameworkElement pane) =>
        Start(pane, Rest, new Pose(0, pane.ActualWidth * .01, pane.ActualHeight * .01 + 3, .98, .98),
            140, EasingMode.EaseIn);

    internal void Expand(FrameworkElement pane, Rect previous, Rect current)
    {
        if (previous.Width <= 0 || previous.Height <= 0 || current.Width <= 0 || current.Height <= 0)
            return;
        var x = previous.X - current.X;
        var y = previous.Y - current.Y;
        if (Math.Abs(x) < 1 && Math.Abs(y) < 1 &&
            Math.Abs(previous.Width - current.Width) < 1 && Math.Abs(previous.Height - current.Height) < 1)
            return;

        _ = Start(pane, new Pose(1, x, y, previous.Width / current.Width, previous.Height / current.Height),
            Rest, 190, EasingMode.EaseOut);
    }

    internal void FinishAll(FrameworkElement? except = null)
    {
        foreach (var pane in _running.Keys.ToArray())
            if (!ReferenceEquals(pane, except)) Finish(pane);
    }

    private Task Start(FrameworkElement pane, Pose from, Pose to, int milliseconds, EasingMode mode)
    {
        Finish(pane);
        pane.Opacity = to.Opacity;
        var transform = new CompositeTransform
        {
            TranslateX = to.X,
            TranslateY = to.Y,
            ScaleX = to.ScaleX,
            ScaleY = to.ScaleY
        };
        pane.RenderTransform = transform;

        var storyboard = new Storyboard { FillBehavior = FillBehavior.Stop };
        Add(pane, nameof(UIElement.Opacity), from.Opacity, to.Opacity);
        Add(transform, nameof(CompositeTransform.TranslateX), from.X, to.X);
        Add(transform, nameof(CompositeTransform.TranslateY), from.Y, to.Y);
        Add(transform, nameof(CompositeTransform.ScaleX), from.ScaleX, to.ScaleX);
        Add(transform, nameof(CompositeTransform.ScaleY), from.ScaleY, to.ScaleY);

        var running = new Running(storyboard, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _running.Add(pane, running);
        storyboard.Completed += (_, _) =>
        {
            if (_running.TryGetValue(pane, out var current) && ReferenceEquals(current, running))
                Finish(pane);
        };
        storyboard.Begin();
        return running.Completion.Task;

        void Add(DependencyObject target, string property, double start, double end)
        {
            if (start == end) return;
            var animation = new DoubleAnimation
            {
                From = start,
                To = end,
                Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                EasingFunction = new CubicEase { EasingMode = mode }
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }
    }

    private void Finish(FrameworkElement pane)
    {
        if (!_running.Remove(pane, out var running)) return;
        running.Storyboard.Stop();
        pane.RenderTransform = null;
        running.Completion.SetResult();
    }
}
