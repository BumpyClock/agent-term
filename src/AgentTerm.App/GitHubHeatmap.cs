using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using AgentTerm_App.GitHub;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;
using Colors = Microsoft.UI.Colors;

namespace AgentTerm_App;

internal sealed partial class GitHubHeatmap : UserControl
{
    private const double LabelBand = 16;
    private const int PlaceholderColumns = 53;
    private const float LensScale = 1.5f;
    private const float LensInset = 6;

    private static readonly Color[] DarkLevels =
    [
        Color.FromArgb(255, 55, 65, 60), Color.FromArgb(255, 24, 78, 42), Color.FromArgb(255, 40, 120, 65),
        Color.FromArgb(255, 64, 170, 85), Color.FromArgb(255, 120, 220, 115)
    ];
    private static readonly Color[] LightLevels =
    [
        Color.FromArgb(255, 235, 237, 240), Color.FromArgb(255, 155, 233, 168), Color.FromArgb(255, 64, 196, 99),
        Color.FromArgb(255, 48, 161, 78), Color.FromArgb(255, 33, 110, 57)
    ];

    private readonly Canvas _labels = new() { Height = LabelBand, IsHitTestVisible = false };
    private readonly Grid _surface = new() { HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Compositor _compositor;
    private readonly ContainerVisual _cells;
    private readonly ShapeVisual _lensVisual;
    private readonly CompositionSpriteShape _lens;
    private readonly CompositionRoundedRectangleGeometry _cellShape;
    private readonly CompositionRoundedRectangleGeometry _lensShape;
    private readonly CompositionColorBrush[] _levels;
    private readonly CompositionColorBrush _outline;
    private readonly CompositionColorBrush _emptyStroke;
    private readonly AccessibilitySettings _accessibility = new();
    private readonly List<ShapeVisual> _columns = [];
    private readonly List<CompositionSpriteShape> _shapes = [];
    private IReadOnlyList<GitHubDay> _days = [];
    private GitHubHeatmapGeometry _geometry;
    private int _selected = -1;
    private bool _highContrast;
    private (ElementTheme, bool, Color?)? _palette;
    private bool _loading;
    private bool _pointerOver;
    private bool _raised;

    public GitHubHeatmap()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        FocusVisualMargin = new Thickness(-4, LabelBand - 4, -4, -4);
        var root = new StackPanel();
        root.Children.Add(_labels);
        root.Children.Add(_surface);
        Content = root;

        _compositor = GitHubMotion.Of(this).Compositor;
        _levels = new CompositionColorBrush[5];
        for (var level = 0; level < _levels.Length; level++) _levels[level] = _compositor.CreateColorBrush();
        _outline = _compositor.CreateColorBrush();
        _emptyStroke = _compositor.CreateColorBrush();
        _cellShape = _compositor.CreateRoundedRectangleGeometry();
        _lensShape = _compositor.CreateRoundedRectangleGeometry();
        _cells = _compositor.CreateContainerVisual();
        _lensVisual = _compositor.CreateShapeVisual();
        _lensVisual.Offset = new Vector3(-LensInset, -LensInset, 0);
        _lens = _compositor.CreateSpriteShape(_lensShape);
        _lens.StrokeBrush = _outline;
        _lens.StrokeThickness = 1;
        _lensVisual.Shapes.Add(_lens);
        _lensVisual.Opacity = 0;
        var host = _compositor.CreateContainerVisual();
        host.Children.InsertAtTop(_cells);
        host.Children.InsertAtTop(_lensVisual);
        ElementCompositionPreview.SetElementChildVisual(_surface, host);

        _surface.PointerMoved += (_, args) => SelectAt(args.GetCurrentPoint(_surface).Position);
        _surface.PointerEntered += (_, _) =>
        {
            _pointerOver = true;
            UpdateLens();
        };
        _surface.PointerExited += (_, _) =>
        {
            _pointerOver = false;
            UpdateLens();
        };
        GotFocus += (_, _) => UpdateLens();
        LostFocus += (_, _) => UpdateLens();
        KeyDown += OnKeyDown;
        ActualThemeChanged += (_, _) => ApplyPalette();
        Loaded += (_, _) => ApplyPalette();
        Rebuild();
    }

    public event Action? SelectionChanged;
    public event Action? PaletteChanged;
    public event Action? GeometryChanged;

    public int Columns => _days.Count > 0 ? GitHubPresentation.Columns(_days) : PlaceholderColumns;
    public int LatestYear => _days.Count == 0 ? 0 : int.Parse(_days[^1].Date.AsSpan(0, 4), CultureInfo.InvariantCulture);
    public int Selected => _selected;
    public GitHubDay? SelectedDay => _selected >= 0 && _selected < _days.Count ? _days[_selected] : null;
    public Color[] Palette { get; private set; } = DarkLevels;

    public void SetDays(IReadOnlyList<GitHubDay>? days)
    {
        var date = SelectedDay?.Date;
        _days = days ?? [];
        _selected = -1;
        if (_days.Count > 0)
        {
            _selected = _days.Count - 1;
            for (var index = 0; index < _days.Count; index++)
                if (_days[index].Date == date) _selected = index;
        }
        Rebuild();
        InvalidateMeasure();
        SelectionChanged?.Invoke();
    }

    public void SetLoading(bool loading)
    {
        loading &= GitHubMotion.Enabled;
        if (loading == _loading) return;
        _loading = loading;
        for (var column = 0; column < _columns.Count; column++)
        {
            var visual = _columns[column];
            visual.StopAnimation("Opacity");
            visual.Opacity = 1;
            if (!loading) continue;
            var pulse = _compositor.CreateScalarKeyFrameAnimation();
            pulse.InsertKeyFrame(0, 1);
            pulse.InsertKeyFrame(0.5f, 0.35f, _compositor.CreateCubicBezierEasingFunction(new(0.45f, 0), new(0.55f, 1)));
            pulse.InsertKeyFrame(1, 1, _compositor.CreateCubicBezierEasingFunction(new(0.45f, 0), new(0.55f, 1)));
            pulse.Duration = TimeSpan.FromMilliseconds(1500);
            pulse.DelayTime = TimeSpan.FromMilliseconds(column * 14);
            pulse.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Opacity", pulse);
        }
    }

    public void Reveal(TimeSpan delay)
    {
        SetLoading(false);
        foreach (var visual in _columns)
        {
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Translation");
            visual.Opacity = 1;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
        _lensVisual.Opacity = _selected >= 0 ? 1 : 0;
        if (!GitHubMotion.Enabled) return;
        var easing = GitHubMotion.Decelerate(_compositor);
        for (var column = 0; column < _columns.Count; column++)
        {
            var start = delay + TimeSpan.FromMilliseconds(column * 4);
            var opacity = _compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(0, 0);
            opacity.InsertKeyFrame(1, 1, easing);
            opacity.Duration = TimeSpan.FromMilliseconds(180);
            opacity.DelayTime = start;
            opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            var rise = _compositor.CreateVector3KeyFrameAnimation();
            rise.InsertKeyFrame(0, new Vector3(0, 3, 0));
            rise.InsertKeyFrame(1, Vector3.Zero, easing);
            rise.Duration = opacity.Duration;
            rise.DelayTime = start;
            rise.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            _columns[column].StartAnimation("Opacity", opacity);
            _columns[column].StartAnimation("Translation", rise);
        }
        var lens = _compositor.CreateScalarKeyFrameAnimation();
        lens.InsertKeyFrame(0, 0);
        lens.InsertKeyFrame(1, _selected >= 0 ? 1 : 0, easing);
        lens.Duration = TimeSpan.FromMilliseconds(180);
        lens.DelayTime = delay + TimeSpan.FromMilliseconds(_columns.Count * 4);
        lens.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        _lensVisual.StartAnimation("Opacity", lens);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 386;
        var geometry = GitHubPresentation.Heatmap(width, XamlRoot?.RasterizationScale ?? 1, Columns);
        if (geometry != _geometry)
        {
            _geometry = geometry;
            _surface.Width = geometry.Width;
            _surface.Height = geometry.Height;
            _labels.Width = geometry.Width;
            Rebuild();
            GeometryChanged?.Invoke();
        }
        return base.MeasureOverride(availableSize);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new HeatmapAutomationPeer(this);

    private void Rebuild()
    {
        _cells.Children.RemoveAll();
        _columns.Clear();
        _shapes.Clear();
        var pitch = (float)_geometry.Pitch;
        var cell = (float)_geometry.Cell;
        _cellShape.Size = new Vector2(cell);
        _cellShape.CornerRadius = new Vector2(MathF.Max(1, cell * 0.24f));
        _lensShape.Size = _cellShape.Size;
        _lensShape.CornerRadius = _cellShape.CornerRadius;
        _lens.CenterPoint = new Vector2(cell / 2);
        _lensVisual.Size = new Vector2((float)_geometry.Width + 2 * LensInset, (float)_geometry.Height + 2 * LensInset);

        for (var column = 0; column < Columns; column++)
        {
            var visual = _compositor.CreateShapeVisual();
            visual.Offset = new Vector3(column * pitch, 0, 0);
            visual.Size = new Vector2(cell, (float)_geometry.Height);
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            _columns.Add(visual);
            _cells.Children.InsertAtTop(visual);
        }
        if (_days.Count == 0)
        {
            foreach (var visual in _columns)
                for (var row = 0; row < GitHubPresentation.CalendarRows; row++)
                    visual.Shapes.Add(Cell(0, row * pitch));
        }
        else
        {
            foreach (var day in _days)
            {
                var shape = Cell(day.Level, day.Weekday * pitch);
                _shapes.Add(shape);
                _columns[(int)day.Week].Shapes.Add(shape);
            }
        }
        if (_loading)
        {
            _loading = false;
            SetLoading(true);
        }
        RebuildLabels();
        PlaceLens(false);
    }

    private CompositionSpriteShape Cell(uint level, float y)
    {
        var shape = _compositor.CreateSpriteShape(_cellShape);
        shape.Offset = new Vector2(0, y);
        shape.FillBrush = _levels[Math.Min(level, 4)];
        if (_highContrast && level == 0)
        {
            shape.StrokeBrush = _emptyStroke;
            shape.StrokeThickness = 1;
        }
        return shape;
    }

    private void RebuildLabels()
    {
        _labels.Children.Clear();
        if (_days.Count == 0 || _geometry.Pitch <= 0) return;
        var labelColumns = (int)Math.Ceiling(23 / _geometry.Pitch);
        foreach (var label in GitHubPresentation.MonthLabels(_days, labelColumns, CultureInfo.CurrentCulture))
        {
            var text = new TextBlock
            {
                Text = label.Text,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                FontSize = 11
            };
            Canvas.SetLeft(text, label.Column * _geometry.Pitch);
            _labels.Children.Add(text);
        }
    }

    private void ApplyPalette()
    {
        var highContrast = _accessibility.HighContrast;
        var key = (ActualTheme, highContrast, (Foreground as SolidColorBrush)?.Color);
        if (key.Equals(_palette)) return;
        _palette = key;
        var rebuild = highContrast != _highContrast;
        _highContrast = highContrast;
        var resources = Application.Current.Resources;
        Color Resource(string key) => ((SolidColorBrush)resources[key]).Color;
        if (_highContrast)
        {
            var window = Resource("SystemColorWindowColorBrush");
            var highlight = Resource("SystemColorHighlightColorBrush");
            Palette = [window, highlight, highlight, highlight, highlight];
            _emptyStroke.Color = Resource("SystemColorGrayTextColorBrush");
            _outline.Color = Resource("SystemColorWindowTextColorBrush");
        }
        else
        {
            Palette = ActualTheme == ElementTheme.Light ? LightLevels : DarkLevels;
            var foreground = (Foreground as SolidColorBrush)?.Color ?? Colors.White;
            _outline.Color = Color.FromArgb(220, foreground.R, foreground.G, foreground.B);
        }
        for (var level = 0; level < _levels.Length; level++) _levels[level].Color = Palette[level];
        if (rebuild) Rebuild();
        else RebuildLabels();
        PaletteChanged?.Invoke();
    }

    private void SelectAt(Point point)
    {
        if (_days.Count == 0 || _geometry.Pitch <= 0) return;
        var index = GitHubPresentation.DayAt(_days, (int)(point.X / _geometry.Pitch), (int)(point.Y / _geometry.Pitch));
        if (index >= 0) Select(index);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        GitHubDayMove? move = args.Key switch
        {
            VirtualKey.Left => GitHubDayMove.PreviousWeek,
            VirtualKey.Right => GitHubDayMove.NextWeek,
            VirtualKey.Up => GitHubDayMove.PreviousDay,
            VirtualKey.Down => GitHubDayMove.NextDay,
            VirtualKey.Home => GitHubDayMove.First,
            VirtualKey.End => GitHubDayMove.Last,
            _ => null
        };
        if (move is null || _days.Count == 0) return;
        args.Handled = true;
        Select(GitHubPresentation.MoveDay(_selected, move.Value, _days.Count));
    }

    private void Select(int index)
    {
        if (index == _selected) return;
        _selected = index;
        PlaceLens(true);
        SelectionChanged?.Invoke();
    }

    private void PlaceLens(bool pop)
    {
        if (SelectedDay is not { } day)
        {
            _lensVisual.Opacity = 0;
            return;
        }
        _lens.Offset = new Vector2((float)(day.Week * _geometry.Pitch) + LensInset, (float)(day.Weekday * _geometry.Pitch) + LensInset);
        _lens.FillBrush = _levels[Math.Min(day.Level, 4)];
        _lensVisual.Opacity = 1;
        if (pop && _raised && GitHubMotion.Enabled)
        {
            var animation = _compositor.CreateVector2KeyFrameAnimation();
            animation.InsertKeyFrame(0, new Vector2(1.15f));
            animation.InsertKeyFrame(1, new Vector2(LensScale), GitHubMotion.Decelerate(_compositor));
            animation.Duration = GitHubMotion.Short;
            _lens.StartAnimation("Scale", animation);
        }
    }

    private void UpdateLens()
    {
        var raised = _pointerOver || FocusState != FocusState.Unfocused;
        if (raised == _raised) return;
        _raised = raised;
        var to = new Vector2(raised ? LensScale : 1);
        if (!GitHubMotion.Enabled)
        {
            _lens.StopAnimation("Scale");
            _lens.Scale = to;
            return;
        }
        var animation = _compositor.CreateVector2KeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, to, GitHubMotion.Decelerate(_compositor));
        animation.Duration = GitHubMotion.Short;
        _lens.StartAnimation("Scale", animation);
    }

    private sealed partial class HeatmapAutomationPeer(GitHubHeatmap owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override string GetLocalizedControlTypeCore() => GitHubStrings.CalendarControlType;
        protected override string GetClassNameCore() => nameof(GitHubHeatmap);
    }
}
