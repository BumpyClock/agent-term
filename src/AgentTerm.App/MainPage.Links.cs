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
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;

namespace AgentTerm_App;

/// <summary>
/// OSC 8 hyperlinks and application pointer shapes. A link is looked up in the terminal at the displayed cell when the
/// user hovers, clicks or opens the menu. Nothing opens because output arrived. Ctrl+Click opens, the right-click menu
/// copies, and the terminal's own mouse tracking keeps every other event.
/// </summary>
public sealed partial class MainPage
{
    private sealed record LinkPress(TerminalLink Link, int Column, int Row);

    private Grid? _linkOverlay;
    private Canvas? _linkLines;
    private Border? _linkChip;
    private TextBlock? _linkChipTarget;
    private TextBlock? _linkChipHint;
    private readonly List<Rectangle> _linkUnderlines = [];
    private TerminalLink? _hoverLink;
    private TerminalFrame? _hoverFrame;
    private TerminalGridGeometry? _hoverGeometry;
    private bool _hoverEmphasis;
    private bool _hoverLookedUp;
    private int _hoverColumn = -1, _hoverRow = -1;
    private Point _linkPointer;
    private bool _pointerOverTerminal;
    private LinkPress? _linkPress;
    private bool _linkDialogOpen;

    private void InitializeLinks()
    {
        Terminal.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(Terminal_LinkPointerMoved), true);
        Terminal.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Terminal_LinkPointerMoved), true);
        Terminal.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(Terminal_LinkPointerExited), true);
        Terminal.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Terminal_LinkModifierChanged), true);
        Terminal.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(Terminal_LinkModifierChanged), true);
        AutomationProperties.SetHelpText(Terminal, AutomationProperties.GetHelpText(Terminal) +
            " Ctrl+click opens a link. Right-click a link, or press the Menu key at the text cursor, for the link menu.");
    }

    private static bool LinkModifierDown() => TerminalLinkGesture.IsOpenModifier(Modifiers());

    private static bool PointerShowsHover(PointerRoutedEventArgs e) =>
        e.Pointer.PointerDeviceType is PointerDeviceType.Mouse or PointerDeviceType.Pen;

    private void Terminal_LinkPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!PointerShowsHover(e)) return;
        _pointerOverTerminal = true;
        _linkPointer = e.GetCurrentPoint(Terminal).Position;
        RefreshLinkFeedback();
    }

    private void Terminal_LinkPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Exits from children such as the composition box bubble here while the pointer is still over the surface.
        var position = e.GetCurrentPoint(Terminal).Position;
        if (position.X >= 0 && position.Y >= 0 && position.X < Terminal.ActualWidth && position.Y < Terminal.ActualHeight) return;
        _pointerOverTerminal = false;
        ClearLinkHover();
        Terminal.ResetPointerKind();
    }

    private void Terminal_LinkModifierChanged(object sender, KeyRoutedEventArgs e)
    {
        if (_pointerOverTerminal && e.Key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
            VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu)
            RefreshLinkFeedback();
    }

    /// <summary>Re-reads what is under the pointer. Cheap when nothing moved and the frame is unchanged.</summary>
    private void RefreshLinkFeedback()
    {
        if (!_pointerOverTerminal) return;
        try
        {
            if (_session is not { } session || _state is not (SessionState.Running or SessionState.Idle) || _presentedFrame is not { } frame)
            {
                ClearLinkHover();
                Terminal.ResetPointerKind();
                return;
            }
            var mode = session.ReadPointerMode();
            var tracking = _state == SessionState.Running && mode.MouseTracking;
            var modifiers = Modifiers();
            var onGrid = Terminal.Geometry.TryCellAt(_linkPointer, out var column, out var row);
            var link = _hoverLink;
            var lookedUp = _hoverLookedUp;
            // The program owns plain mouse events while it tracks the mouse. Only the Ctrl gesture reaches for a link then.
            if (!onGrid || !TerminalLinkGesture.ShowsHover(modifiers, tracking, _selecting))
            {
                link = null;
                lookedUp = false;
            }
            else if (!lookedUp || column != _hoverColumn || row != _hoverRow || !ReferenceEquals(frame, _hoverFrame))
            {
                link = session.ReadLinkAt(column, row, frame);
                lookedUp = true;
            }
            ApplyLinkHover(link, lookedUp, frame, onGrid ? column : -1, onGrid ? row : -1, TerminalLinkGesture.IsOpenModifier(modifiers));
            Terminal.SetPointerKind(TerminalLinkGesture.PointerFor(mode.Shape, link, modifiers, _selecting));
        }
        catch (ObjectDisposedException) { ClearLinkHover(); }
        catch (Exception exception)
        {
            // Stop hover work until the pointer moves again, so one failure shows one banner.
            _pointerOverTerminal = false;
            ClearLinkHover();
            ShowInteractionFailure(exception);
        }
    }

    private void ApplyLinkHover(TerminalLink? link, bool lookedUp, TerminalFrame frame, int column, int row, bool emphasis)
    {
        var geometry = Terminal.Geometry;
        var changed = emphasis != _hoverEmphasis || !SameLink(link, _hoverLink) || geometry != _hoverGeometry;
        _hoverLink = link;
        _hoverLookedUp = lookedUp;
        _hoverFrame = frame;
        _hoverGeometry = geometry;
        _hoverColumn = column;
        _hoverRow = row;
        _hoverEmphasis = emphasis;
        if (changed) RenderLinkOverlay(link, frame, column, row, emphasis);
    }

    private static bool SameLink(TerminalLink? left, TerminalLink? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null && left.SameDestination(right) &&
        left.Spans.SequenceEqual(right.Spans);

    private void ClearLinkHover()
    {
        if (_hoverLink is null && _linkOverlay is null)
        {
            _hoverColumn = _hoverRow = -1;
            return;
        }
        _hoverLink = null;
        _hoverLookedUp = false;
        _hoverFrame = null;
        _hoverGeometry = null;
        _hoverColumn = _hoverRow = -1;
        _hoverEmphasis = false;
        if (_linkOverlay is not null) _linkOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>Called when a session is released or the page unloads. Drops every hover, press and pointer override.</summary>
    private void ResetLinkInteraction()
    {
        _linkPress = null;
        _pointerOverTerminal = false;
        ClearLinkHover();
        Terminal.ResetPointerKind();
    }

    private void EnsureLinkOverlay()
    {
        if (_linkOverlay is not null || Terminal.Parent is not Grid host) return;
        var overlay = (Grid)XamlReader.Load("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" IsHitTestVisible="False" Visibility="Collapsed">
              <Canvas x:Name="Lines" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" />
              <Border x:Name="Chip" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" HorizontalAlignment="Left"
                      VerticalAlignment="Bottom" Margin="8" Padding="10,5" CornerRadius="6" BorderThickness="1"
                      Background="{ThemeResource SolidBackgroundFillColorSecondaryBrush}"
                      BorderBrush="{ThemeResource ControlStrokeColorSecondaryBrush}">
                <StackPanel Spacing="1">
                  <TextBlock x:Name="Target" FontSize="12" TextTrimming="CharacterEllipsis" MaxLines="1"
                             Foreground="{ThemeResource TextFillColorPrimaryBrush}" />
                  <TextBlock x:Name="Hint" FontSize="11" TextTrimming="CharacterEllipsis" MaxLines="1"
                             Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                </StackPanel>
              </Border>
            </Grid>
            """);
        _linkLines = (Canvas)overlay.FindName("Lines");
        _linkChip = (Border)overlay.FindName("Chip");
        _linkChipTarget = (TextBlock)overlay.FindName("Target");
        _linkChipHint = (TextBlock)overlay.FindName("Hint");
        Grid.SetRow(overlay, Grid.GetRow(Terminal));
        Grid.SetColumn(overlay, Grid.GetColumn(Terminal));
        Grid.SetColumnSpan(overlay, Grid.GetColumnSpan(Terminal));
        host.Children.Add(overlay);
        _linkOverlay = overlay;
    }

    private void RenderLinkOverlay(TerminalLink? link, TerminalFrame frame, int column, int row, bool emphasis)
    {
        if (link is null)
        {
            if (_linkOverlay is not null) _linkOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        EnsureLinkOverlay();
        if (_linkOverlay is null || _linkLines is null || _linkChip is null || _linkChipTarget is null || _linkChipHint is null) return;

        var geometry = Terminal.Geometry;
        _linkOverlay.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Terminal.ActualWidth, Terminal.ActualHeight) };
        var index = row * frame.Columns + column;
        var color = index >= 0 && index < frame.Cells.Length ? frame.Cells[index].Foreground : frame.Foreground;
        var brush = new SolidColorBrush(Windows.UI.Color.FromArgb(emphasis ? (byte)255 : (byte)170, color.R, color.G, color.B));
        var thickness = emphasis ? 2d : 1d;
        for (var i = 0; i < link.Spans.Count; i++)
        {
            if (i == _linkUnderlines.Count)
            {
                _linkUnderlines.Add(new Rectangle { IsHitTestVisible = false });
                _linkLines.Children.Add(_linkUnderlines[i]);
            }
            var span = link.Spans[i];
            var line = _linkUnderlines[i];
            line.Visibility = Visibility.Visible;
            line.Fill = brush;
            line.Width = (span.EndColumn - span.StartColumn) * Terminal.CellWidth;
            line.Height = thickness;
            Canvas.SetLeft(line, span.StartColumn * Terminal.CellWidth);
            Canvas.SetTop(line, geometry.RowTop(span.Row) + Terminal.CellHeight - thickness - 1);
        }
        for (var i = link.Spans.Count; i < _linkUnderlines.Count; i++) _linkUnderlines[i].Visibility = Visibility.Collapsed;

        var target = link.Target;
        _linkChipTarget.Text = target.Display;
        _linkChipHint.Text = target.Decision switch
        {
            TerminalLinkDecision.Allow => "Ctrl+Click to open",
            TerminalLinkDecision.Confirm => "Ctrl+Click to review and open",
            _ => "Blocked. " + target.Reason
        };
        _linkChip.MaxWidth = Math.Max(160, Math.Min(720, Terminal.ActualWidth - 16));
        _linkChip.VerticalAlignment = row >= geometry.Rows - 2 ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        _linkOverlay.Visibility = Visibility.Visible;
    }

    // Ctrl+Click opens a link. The press is claimed so neither a selection nor the program sees it. The open happens on
    // release, and only when the release still sits on a link with the same destination.
    private bool HandleLinkPress(PointerRoutedEventArgs e, PointerPoint point)
    {
        if (point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed || !PointerShowsHover(e) || !LinkModifierDown() ||
            _selecting || _session is not { } session || _state is not (SessionState.Running or SessionState.Idle) ||
            _presentedFrame is not { } frame || !Terminal.Geometry.TryCellAt(point.Position, out var column, out var row))
            return false;
        var link = session.ReadLinkAt(column, row, frame);
        if (!TerminalLinkGesture.ClaimsPress(Modifiers(), TerminalMouseButton.Left, link is not null)) return false;
        FocusTerminal(FocusState.Pointer);
        _linkPress = new LinkPress(link!, column, row);
        Terminal.CapturePointer(e.Pointer);
        e.Handled = true;
        return true;
    }

    private bool HandleLinkRelease(PointerRoutedEventArgs e, PointerPoint point)
    {
        if (_linkPress is not { } press) return false;
        if (point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased) return true;
        _linkPress = null;
        Terminal.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        if (_session is { } session && _presentedFrame is { } frame && Terminal.Geometry.TryCellAt(point.Position, out var column, out var row) &&
            session.ReadLinkAt(column, row, frame) is { } link && TerminalLinkGesture.OpensOnRelease(press.Link, link))
            _ = OpenLinkAsync(link);
        return true;
    }

    private void CancelLinkPress() => _linkPress = null;

    /// <summary>Right-click on a link. Returns false when there is no link, so the caller keeps its own right-click action.</summary>
    private bool TryShowLinkMenu(Point position)
    {
        if (_session is not { } session || _presentedFrame is not { } frame || !Terminal.Geometry.TryCellAt(position, out var column, out var row) ||
            session.ReadLinkAt(column, row, frame) is not { } link)
            return false;
        ShowLinkMenu(position, link, session);
        return true;
    }

    // The Menu key opens the same menu without a pointer, for the link under the hovering pointer or else at the text cursor.
    private bool TryShowLinkMenuFromKeyboard(KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Application && !(e.Key == VirtualKey.F10 && Down(VirtualKey.Shift) && !Down(VirtualKey.Control) && !Down(VirtualKey.Menu)))
            return false;
        if (_session is not { } session || _presentedFrame is not { } frame) return false;
        TerminalLink? link;
        int column, row;
        if (_pointerOverTerminal && _hoverLink is { } hovered && _hoverColumn >= 0)
        {
            (column, row) = (_hoverColumn, _hoverRow);
            link = session.ReadLinkAt(column, row, frame);
        }
        else
        {
            var cursor = frame.Cursor;
            if (!frame.Viewport.IsAtBottom || cursor.Column < 0 || cursor.Row < 0 || cursor.Column >= frame.Columns) return false;
            (column, row) = (cursor.Column, cursor.Row);
            link = session.ReadLinkAt(column, row, frame);
        }
        if (link is null) return false;
        var geometry = Terminal.Geometry;
        ShowLinkMenu(new Point(column * Terminal.CellWidth, geometry.RowTop(row) + Terminal.CellHeight), link, session);
        e.Handled = true;
        return true;
    }

    private void ShowLinkMenu(Point position, TerminalLink link, TerminalSession session)
    {
        var target = link.Target;
        var open = new MenuFlyoutItem
        {
            Text = "Open link", Icon = new FontIcon { Glyph = "" }, IsEnabled = target.CanOpen,
            KeyboardAcceleratorTextOverride = "Ctrl+Click"
        };
        var copyAddress = new MenuFlyoutItem { Text = "Copy link address", Icon = new FontIcon { Glyph = "" }, IsEnabled = target.IsCopyable };
        if (!target.CanOpen) ToolTipService.SetToolTip(open, target.Reason);
        if (!target.IsCopyable) ToolTipService.SetToolTip(copyAddress, target.Reason);
        AutomationProperties.SetHelpText(open, target.Display);
        AutomationProperties.SetHelpText(copyAddress, target.Display);
        open.Click += (_, _) => _ = OpenLinkAsync(link);
        copyAddress.Click += (_, _) => CopyLinkAddress(link);

        var copy = new MenuFlyoutItem { Text = "Copy", Icon = new FontIcon { Glyph = "" }, IsEnabled = _presentedFrame?.HasSelection == true };
        copy.Click += (_, _) => CopySelection(session);
        var paste = new MenuFlyoutItem { Text = "Paste", Icon = new FontIcon { Glyph = "" }, IsEnabled = _state == SessionState.Running };
        paste.Click += async (_, _) => await PasteAsync(session);

        var flyout = new MenuFlyout();
        flyout.Items.Add(open);
        flyout.Items.Add(copyAddress);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(copy);
        flyout.Items.Add(paste);
        flyout.Closed += (_, _) => FocusTerminal(FocusState.Programmatic);
        ClearLinkHover();
        flyout.ShowAt(Terminal, new FlyoutShowOptions { Position = position });
    }

    private void CopyLinkAddress(TerminalLink link)
    {
        if (!link.Target.IsCopyable) return;
        try
        {
            var data = new DataPackage();
            data.SetText(link.Target.Text);
            Clipboard.SetContent(data);
        }
        catch (Exception exception) { ShowClipboardFailure(exception); }
    }

    /// <summary>Opens a link the user chose. Applies the URI policy again, confirms when the policy asks, and never throws.</summary>
    private async Task OpenLinkAsync(TerminalLink link)
    {
        var target = link.Target;
        try
        {
            if (!target.CanOpen || target.Uri is null)
            {
                ShowLinkNotice("Link not opened", target.Reason);
                return;
            }
            if (target.Decision == TerminalLinkDecision.Confirm && !await ConfirmLinkAsync(target)) return;
            if (!await Windows.System.Launcher.LaunchUriAsync(target.Uri))
                ShowLinkNotice("Link not opened", "Windows could not find a program for this link.");
        }
        catch (Exception exception) { ShowLinkNotice("Link not opened", exception.Message); }
        finally { FocusTerminal(FocusState.Programmatic); }
    }

    private async Task<bool> ConfirmLinkAsync(TerminalLinkTarget target)
    {
        if (_linkDialogOpen || Terminal.XamlRoot is null) return false;
        _linkDialogOpen = true;
        try
        {
            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(new TextBlock { Text = target.Reason, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock
            {
                Text = target.Uri!.AbsoluteUri, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                FontFamily = new FontFamily("Cascadia Mono, Consolas")
            });
            var dialog = new ContentDialog
            {
                Title = "Open this link?", Content = new ScrollViewer { Content = body, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, PrimaryButtonText = "Open", CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close, XamlRoot = Terminal.XamlRoot
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally { _linkDialogOpen = false; }
    }

    private void ShowLinkNotice(string title, string message)
    {
        ErrorBanner.Title = title;
        ErrorBanner.Message = message;
        ErrorBanner.IsOpen = true;
    }
}
