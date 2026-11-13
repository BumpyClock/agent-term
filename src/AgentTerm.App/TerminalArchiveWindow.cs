using System;
using AgentTerm.Terminal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace AgentTerm_App;

/// <summary>Owns one offline terminal. All access stays on the window dispatcher; no transport or timers exist.</summary>
internal sealed class TerminalArchiveWindow : Window
{
    private readonly GhosttyTerminal _terminal;
    private readonly TerminalSurface _surface = new();
    private readonly ScrollBar _history = new() { Orientation = Orientation.Vertical, Width = 18 };
    private readonly TextBox _query = new() { PlaceholderText = "Find in archive", MinWidth = 180 };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private bool _refreshing;
    private bool _selecting;
    private bool _closed;

    public TerminalArchiveWindow(GhosttyTerminal terminal, string name)
    {
        _terminal = terminal;
        Title = name + " · Offline terminal archive";
        var root = new Grid { Padding = new Thickness(12), RowSpacing = 8 };
        root.PreviewKeyDown += (_, e) =>
        {
            var control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            var searching = _query.FocusState != FocusState.Unfocused;
            if (control && e.Key == VirtualKey.F) { _query.Focus(FocusState.Keyboard); _query.SelectAll(); e.Handled = true; }
            else if (control && e.Key == VirtualKey.C && !searching) { Copy(); e.Handled = true; }
            else if (!searching && e.Key is VirtualKey.PageUp or VirtualKey.PageDown)
            {
                _terminal.ScrollLines((e.Key == VirtualKey.PageUp ? -1 : 1) * _terminal.ReadPresentedViewport().Rows);
                Refresh(); e.Handled = true;
            }
        };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolbar.Children.Add(_query);
        var previous = new Button { Content = "Previous" };
        var next = new Button { Content = "Next" };
        var copy = new Button { Content = "Copy selection" };
        toolbar.Children.Add(previous); toolbar.Children.Add(next); toolbar.Children.Add(copy); toolbar.Children.Add(_status);
        previous.Click += (_, _) => Find(false);
        next.Click += (_, _) => Find(true);
        copy.Click += (_, _) => Copy();
        _query.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { Find(true); e.Handled = true; } };
        AutomationProperties.SetName(_query, "Find in offline terminal archive");
        var area = new Grid();
        area.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        area.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var scroll = new ScrollViewer { Content = _surface, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        area.Children.Add(scroll); area.Children.Add(_history); Grid.SetColumn(_history, 1);
        _history.ValueChanged += (_, _) => { if (!_refreshing && !_closed) { _terminal.ScrollTo((long)_history.Value); Refresh(); } };
        AutomationProperties.SetName(_history, "Archive history");
        AutomationProperties.SetName(_surface, "Offline read-only terminal");
        _surface.Input.IsReadOnly = true;
        _surface.PointerWheelChanged += (_, e) =>
        {
            _terminal.ScrollLines(-Math.Sign(e.GetCurrentPoint(_surface).Properties.MouseWheelDelta) * 3);
            Refresh(); e.Handled = true;
        };
        _surface.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (!e.GetCurrentPoint(_surface).Properties.IsLeftButtonPressed) return;
            var cell = _surface.Geometry.ClampedCellAt(e.GetCurrentPoint(_surface).Position);
            _terminal.BeginSelection(cell.Column, cell.Row);
            _selecting = _surface.CapturePointer(e.Pointer);
            Refresh(); e.Handled = true;
        }), true);
        _surface.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
        {
            if (!_selecting) return;
            var cell = _surface.Geometry.ClampedCellAt(e.GetCurrentPoint(_surface).Position);
            _terminal.ExtendSelection(cell.Column, cell.Row); Refresh(); e.Handled = true;
        }), true);
        _surface.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) =>
        {
            if (!_selecting) return;
            _terminal.EndSelection(); _selecting = false; _surface.ReleasePointerCapture(e.Pointer); Refresh(); e.Handled = true;
        }), true);
        _surface.PointerCaptureLost += (_, _) => { if (_selecting) { _terminal.EndSelection(); _selecting = false; } };
        root.Children.Add(toolbar); root.Children.Add(area); Grid.SetRow(area, 1);
        var help = new TextBlock { Text = "Offline archive. Drag to select, copy selection, scroll history, or find text. No shell is running.", TextWrapping = TextWrapping.Wrap };
        root.Children.Add(help); Grid.SetRow(help, 2);
        Content = root;
        _surface.MetricsChanged += Refresh;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 760));
        Closed += (_, _) => { _closed = true; _surface.ReleaseResources(); _terminal.Dispose(); };
        Refresh();
    }

    private void Refresh()
    {
        var frame = _terminal.ReadFrame();
        _surface.Width = frame.Columns * _surface.CellWidth;
        _surface.Height = frame.Rows * _surface.CellHeight;
        _surface.SetFrame(frame with { Cursor = frame.Cursor with { Visible = false } });
        _refreshing = true;
        try { _history.Maximum = frame.Viewport.MaximumOffset; _history.Value = frame.Viewport.Offset; _history.ViewportSize = frame.Rows; }
        finally { _refreshing = false; }
    }

    private void Find(bool forward)
    {
        _status.Text = _terminal.Find(_query.Text, forward) ? "Match selected" : "No match";
        Refresh();
    }

    private void Copy()
    {
        if (_terminal.ReadSelectionText() is not { Length: > 0 } text) return;
        var data = new DataPackage(); data.SetText(text); Clipboard.SetContent(data);
        _status.Text = "Selection copied";
    }
}
