using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentTerm.Terminal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace AgentTerm_App;

internal sealed class TerminalTab : UserControl, IAsyncDisposable
{
    private sealed record Pane(MainPage Page, Border View);

    private readonly Grid _layout = new();
    private readonly List<Pane> _panes = [];
    private readonly SolidColorBrush _focusBorder = new(new UISettings().GetColorValue(UIColorType.Accent));
    private Pane _active;
    private bool _visible;
    private Task _paneClose = Task.CompletedTask;
    private Task? _disposal;

    internal TerminalTab(ShellProfile profile)
    {
        _active = CreatePane(profile);
        _layout.Children.Add(_active.View);
        Content = _layout;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Loaded += (_, _) => FocusActivePane();
    }

    internal string Title => string.Join(" · ", _panes.Select(pane => pane.Page.Profile.Name).Distinct().Take(2));
    internal int PaneCount => _panes.Count;
    internal MainPage ActivePage => _active.Page;
    internal bool IsClosing => _disposal is not null;
    internal event Action? TitleChanged;

    internal void SetVisible(bool visible)
    {
        _visible = visible;
        foreach (var pane in _panes)
            pane.Page.SetActive(visible);
        UpdateFocusBorder();
    }

    internal void FocusActivePane()
    {
        var pane = _active;
        if (!_visible) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_visible && !IsClosing && ReferenceEquals(_active, pane)) pane.Page.FocusTerminal();
        });
    }

    internal void SplitDuplicate(ShellProfile? profile = null) =>
        Split(_active.View.ActualWidth >= _active.View.ActualHeight, profile);

    internal void Split(bool sideBySide, ShellProfile? profile = null)
    {
        if (IsClosing || !_paneClose.IsCompleted) return;

        var old = _active;
        var branch = new Grid();
        var next = CreatePane(profile ?? old.Page.Profile);
        if (sideBySide)
        {
            branch.ColumnSpacing = 4;
            branch.ColumnDefinitions.Add(new ColumnDefinition());
            branch.ColumnDefinitions.Add(new ColumnDefinition());
        }
        else
        {
            branch.RowSpacing = 4;
            branch.RowDefinitions.Add(new RowDefinition());
            branch.RowDefinitions.Add(new RowDefinition());
        }

        Replace(old.View, branch);
        Grid.SetColumn(old.View, 0);
        Grid.SetRow(old.View, 0);
        Grid.SetColumn(next.View, sideBySide ? 1 : 0);
        Grid.SetRow(next.View, sideBySide ? 0 : 1);
        branch.Children.Add(old.View);
        branch.Children.Add(next.View);
        _active = next;
        UpdateFocusBorder();
        TitleChanged?.Invoke();
        FocusActivePane();
    }

    internal Task CloseActivePaneAsync()
    {
        if (IsClosing || !_paneClose.IsCompleted || _panes.Count == 1) return Task.CompletedTask;

        var old = _active;
        var branch = (Grid)old.View.Parent;
        var sibling = (FrameworkElement)branch.Children[ReferenceEquals(branch.Children[0], old.View) ? 1 : 0];
        old.Page.SetActive(false);
        branch.Children.Remove(old.View);
        branch.Children.Remove(sibling);
        Replace(branch, sibling);
        _panes.Remove(old);

        FrameworkElement first = sibling;
        while (first is Grid nested)
            first = (FrameworkElement)nested.Children[0];
        _active = _panes.First(pane => ReferenceEquals(pane.View, first));
        UpdateFocusBorder();
        TitleChanged?.Invoke();
        FocusActivePane();
        return _paneClose = old.Page.DisposeAsync().AsTask();
    }

    internal void FocusAdjacentPane(int horizontal, int vertical)
    {
        if (!_visible || IsClosing || _panes.Count < 2 || !_layout.IsLoaded) return;

        var origin = Bounds(_active.View);
        Pane? nearest = null;
        (double Gap, double Distance, double Offset) best = (double.PositiveInfinity, 0, 0);
        foreach (var pane in _panes)
        {
            if (ReferenceEquals(pane, _active)) continue;
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
        UpdateFocusBorder();
        FocusActivePane();
    }

    private Pane CreatePane(ShellProfile profile)
    {
        var page = new MainPage(profile);
        page.SetActive(_visible);
        var pane = new Pane(page, new Border { Child = page });
        page.SessionReady += () =>
        {
            if (ReferenceEquals(_active, pane)) FocusActivePane();
        };
        page.GotFocus += (_, _) =>
        {
            if (!_visible || !_panes.Contains(pane)) return;
            _active = pane;
            UpdateFocusBorder();
        };
        _panes.Add(pane);
        return pane;
    }

    private void UpdateFocusBorder()
    {
        bool split = _panes.Count > 1;
        foreach (var pane in _panes)
        {
            pane.View.BorderThickness = new Thickness(split ? 1 : 0);
            pane.View.BorderBrush = _visible && split && ReferenceEquals(pane, _active) ? _focusBorder : null;
        }
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
        await Task.WhenAll(_panes.Select(pane => pane.Page.DisposeAsync().AsTask()).Append(_paneClose));
    }
}
