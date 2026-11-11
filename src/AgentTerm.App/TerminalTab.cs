using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentTerm.Terminal;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace AgentTerm_App;

internal sealed class TerminalTab : UserControl, IAsyncDisposable
{
    private const double DividerSize = 10;
    private const double MinimumPaneWidth = 180;
    private const double MinimumPaneHeight = 184;

    private sealed record Pane(MainPage Page, TerminalPane View);

    private readonly Grid _layout = new() { Margin = new Thickness(10) };
    private readonly Style _splitterStyle;
    private readonly List<Pane> _panes = [];
    private readonly UISettings _uiSettings = new();
    private readonly PaneMotion _motion = new();
    private Pane _active;
    private Pane? _retiring;
    private bool _visible;
    private bool _shown;
    private Task _paneClose = Task.CompletedTask;
    private Task? _disposal;

    internal TerminalTab(ShellProfile profile, Style splitterStyle)
    {
        _splitterStyle = splitterStyle;
        _active = CreatePane(profile);
        _layout.Children.Add(_active.View);
        Content = _layout;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        _layout.Loaded += (_, _) =>
        {
            if (!_shown)
            {
                _shown = true;
                if (CanAnimate) _motion.Enter(_active.View);
            }
            FocusActivePane();
        };
    }

    internal string Title => string.Join(" · ", _panes.Select(pane => pane.Page.Profile.Name).Distinct().Take(2));
    internal int PaneCount => _panes.Count;
    internal MainPage ActivePage => _active.Page;
    internal bool IsClosing => _disposal is not null || (_panes.Count == 1 && _retiring is not null);
    internal event Action? TitleChanged;
    internal event Action? CloseRequested;
    private bool CanAnimate => _visible && _layout.IsLoaded && _disposal is null && _uiSettings.AnimationsEnabled;

    internal void SetVisible(bool visible)
    {
        _visible = visible;
        if (!visible) _motion.FinishAll();
        foreach (var pane in _panes)
            pane.Page.SetActive(visible && !ReferenceEquals(pane, _retiring));
        UpdateFocus();
    }

    internal void FocusActivePane()
    {
        var pane = _active;
        if (!_visible || ReferenceEquals(pane, _retiring)) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_visible && !IsClosing && ReferenceEquals(_active, pane) && !ReferenceEquals(pane, _retiring))
                pane.Page.FocusTerminal();
        });
    }

    internal bool CanSplit(bool sideBySide) =>
        !IsClosing && _paneClose.IsCompleted && (sideBySide
            ? _active.View.ActualWidth >= 2 * MinimumPaneWidth + DividerSize
            : _active.View.ActualHeight >= 2 * MinimumPaneHeight + DividerSize);

    internal void SplitDuplicate(ShellProfile? profile = null)
    {
        var sideBySide = _active.View.ActualWidth >= _active.View.ActualHeight;
        Split(CanSplit(sideBySide) ? sideBySide : !sideBySide, profile);
    }

    internal void Split(bool sideBySide, ShellProfile? profile = null)
    {
        if (!CanSplit(sideBySide)) return;

        _motion.FinishAll();
        var old = _active;
        var branch = new Grid();
        var next = CreatePane(profile ?? old.Page.Profile);
        var splitter = new PaneDivider(sideBySide, _splitterStyle);
        if (sideBySide)
        {
            branch.ColumnDefinitions.Add(new ColumnDefinition());
            branch.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DividerSize) });
            branch.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(splitter, 1);
        }
        else
        {
            branch.RowDefinitions.Add(new RowDefinition());
            branch.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DividerSize) });
            branch.RowDefinitions.Add(new RowDefinition());
            Grid.SetRow(splitter, 1);
        }
        splitter.Handle.DragStarted += (_, _) => _motion.FinishAll(_retiring?.View);
        splitter.Handle.DragDelta += (_, args) => ResizeBranch(branch, sideBySide ? args.HorizontalChange : args.VerticalChange);
        branch.SizeChanged += (_, _) => ResizeBranch(branch, 0);

        Replace(old.View, branch);
        Grid.SetColumn(old.View, 0);
        Grid.SetRow(old.View, 0);
        Grid.SetColumn(next.View, sideBySide ? 2 : 0);
        Grid.SetRow(next.View, sideBySide ? 0 : 2);
        branch.Children.Add(old.View);
        branch.Children.Add(next.View);
        branch.Children.Add(splitter);
        _active = next;
        UpdateFocus();
        TitleChanged?.Invoke();
        FocusActivePane();
        if (CanAnimate) _motion.Enter(next.View);
    }

    internal Task CloseActivePaneAsync()
    {
        if (IsClosing || !_paneClose.IsCompleted || _retiring is not null) return Task.CompletedTask;
        return _paneClose = ClosePaneAsync(_active);
    }

    private async Task ClosePaneAsync(Pane old)
    {
        _motion.FinishAll();
        _retiring = old;
        old.Page.SetActive(false);
        old.View.IsHitTestVisible = false;
        old.View.IsEnabled = false;
        if (_panes.Count == 1)
        {
            UpdateFocus();
            if (CanAnimate) await _motion.ExitAsync(old.View);
            if (_disposal is null) CloseRequested?.Invoke();
            return;
        }

        var branch = (Grid)old.View.Parent;
        var sibling = PaneChildren(branch).Single(child => !ReferenceEquals(child, old.View));
        FrameworkElement first = sibling;
        while (first is Grid nested)
            first = PaneChildren(nested).First();
        _active = _panes.First(pane => ReferenceEquals(pane.View, first));
        UpdateFocus();
        FocusActivePane();
        if (CanAnimate) await _motion.ExitAsync(old.View);

        var previousBounds = CanAnimate
            ? _panes.Where(pane => !ReferenceEquals(pane, old)).ToDictionary(pane => pane, pane => Bounds(pane.View))
            : null;
        branch.Children.Clear();
        Replace(branch, sibling);
        _panes.Remove(old);
        _retiring = null;
        UpdateFocus();
        TitleChanged?.Invoke();
        FocusActivePane();
        if (previousBounds is not null)
        {
            _layout.UpdateLayout();
            foreach (var (pane, previous) in previousBounds)
                _motion.Expand(pane.View, previous, Bounds(pane.View));
        }
        await old.Page.DisposeAsync();
    }

    internal bool ResizeActivePane(int horizontal, int vertical)
    {
        if (!_visible || IsClosing) return false;
        bool sideBySide = horizontal != 0;
        for (FrameworkElement view = _active.View; view.Parent is Grid branch && branch != _layout; view = branch)
        {
            if ((branch.ColumnDefinitions.Count != 0) != sideBySide) continue;
            _motion.FinishAll(_retiring?.View);
            var extent = sideBySide ? branch.ActualWidth : branch.ActualHeight;
            ResizeBranch(branch, (sideBySide ? horizontal : vertical) * (extent - DividerSize) * 0.05);
            return true;
        }
        return false;
    }

    private static IEnumerable<FrameworkElement> PaneChildren(Grid branch) =>
        branch.Children.OfType<FrameworkElement>().Where(child => child is not PaneDivider);

    private static double MinimumExtent(FrameworkElement pane, bool width)
    {
        if (pane is not Grid branch) return width ? MinimumPaneWidth : MinimumPaneHeight;

        var children = PaneChildren(branch).ToArray();
        var first = MinimumExtent(children[0], width);
        var second = MinimumExtent(children[1], width);
        return (branch.ColumnDefinitions.Count != 0) == width
            ? first + DividerSize + second
            : Math.Max(first, second);
    }

    private static void ResizeBranch(Grid branch, double delta)
    {
        bool sideBySide = branch.ColumnDefinitions.Count != 0;
        var available = (sideBySide ? branch.ActualWidth : branch.ActualHeight) - DividerSize;
        if (available <= 0 || branch.Children.Count != 3) return;

        var first = sideBySide ? branch.ColumnDefinitions[0].Width : branch.RowDefinitions[0].Height;
        var second = sideBySide ? branch.ColumnDefinitions[2].Width : branch.RowDefinitions[2].Height;
        var share = first.Value / (first.Value + second.Value);
        var children = PaneChildren(branch).ToArray();
        var minFirst = MinimumExtent(children[0], sideBySide);
        var minSecond = MinimumExtent(children[1], sideBySide);
        // When the window is narrower than the combined minimums, share the available space.
        var scale = Math.Min(1, available / (minFirst + minSecond));
        var lower = minFirst * scale;
        var upper = Math.Max(lower, available - minSecond * scale);
        var adjusted = Math.Clamp(share * available + delta, lower, upper) / available;
        if (Math.Abs(adjusted - share) < 0.00001) return;

        first = new GridLength(adjusted, GridUnitType.Star);
        second = new GridLength(1 - adjusted, GridUnitType.Star);
        if (sideBySide)
        {
            branch.ColumnDefinitions[0].Width = first;
            branch.ColumnDefinitions[2].Width = second;
        }
        else
        {
            branch.RowDefinitions[0].Height = first;
            branch.RowDefinitions[2].Height = second;
        }
    }

    private sealed class PaneDivider : UserControl
    {
        internal Thumb Handle { get; }

        internal PaneDivider(bool sideBySide, Style style)
        {
            ProtectedCursor = InputSystemCursor.Create(sideBySide
                ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeNorthSouth);
            IsTabStop = false;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            Handle = new Thumb { Style = style };
            AutomationProperties.SetName(Handle, "Resize panes");
            Content = Handle;
        }
    }

    internal void FocusAdjacentPane(int horizontal, int vertical)
    {
        if (!_visible || IsClosing || _panes.Count < 2 || !_layout.IsLoaded) return;

        var origin = Bounds(_active.View);
        Pane? nearest = null;
        (double Gap, double Distance, double Offset) best = (double.PositiveInfinity, 0, 0);
        foreach (var pane in _panes)
        {
            if (ReferenceEquals(pane, _active) || ReferenceEquals(pane, _retiring)) continue;
            var target = Bounds(pane.View);
            double dx = target.X + target.Width / 2 - (origin.X + origin.Width / 2);
            double dy = target.Y + target.Height / 2 - (origin.Y + origin.Height / 2);
            double direction = dx * horizontal + dy * vertical;
            if (direction <= 0) continue;

            var gap = horizontal != 0
                ? Math.Max(0, Math.Max(origin.Top, target.Top) - Math.Min(origin.Bottom, target.Bottom))
                : Math.Max(0, Math.Max(origin.Left, target.Left) - Math.Min(origin.Right, target.Right));
            var rank = (gap, direction, Math.Abs(horizontal != 0 ? dy : dx));
            if (rank.CompareTo(best) >= 0) continue;
            best = rank;
            nearest = pane;
        }

        if (nearest is null) return;
        _active = nearest;
        UpdateFocus();
        FocusActivePane();
    }

    private Pane CreatePane(ShellProfile profile)
    {
        var page = new MainPage(profile);
        page.SetActive(_visible);
        var view = new TerminalPane(page);
        var pane = new Pane(page, view);
        page.SessionReady += () =>
        {
            if (ReferenceEquals(_active, pane)) FocusActivePane();
        };
        view.GotFocus += (_, _) => ActivatePane(pane);
        view.SplitRequested += sideBySide =>
        {
            if (ActivatePane(pane)) Split(sideBySide);
        };
        view.CloseRequested += async () =>
        {
            if (ActivatePane(pane)) await CloseActivePaneAsync();
        };
        _panes.Add(pane);
        return pane;
    }

    private bool ActivatePane(Pane pane)
    {
        if (!_visible || IsClosing || ReferenceEquals(pane, _retiring) || !_panes.Contains(pane)) return false;
        _active = pane;
        UpdateFocus();
        return true;
    }

    private void UpdateFocus()
    {
        foreach (var pane in _panes)
            pane.View.SetFocused(_visible && ReferenceEquals(pane, _active) && !ReferenceEquals(pane, _retiring));
    }

    private Rect Bounds(FrameworkElement pane) => pane.TransformToVisual(_layout)
        .TransformBounds(new Rect(0, 0, pane.ActualWidth, pane.ActualHeight));

    private static void Replace(FrameworkElement old, FrameworkElement replacement)
    {
        var parent = (Grid)old.Parent;
        var index = parent.Children.IndexOf(old);
        Grid.SetColumn(replacement, Grid.GetColumn(old));
        Grid.SetRow(replacement, Grid.GetRow(old));
        parent.Children.RemoveAt(index);
        parent.Children.Insert(index, replacement);
    }

    public ValueTask DisposeAsync() => new(_disposal ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        SetVisible(false);
        // A closing pane may still be attached while fading. Finish that operation before enumerating survivors.
        try { await _paneClose; }
        finally { await Task.WhenAll(_panes.Select(pane => pane.Page.DisposeAsync().AsTask())); }
    }
}
