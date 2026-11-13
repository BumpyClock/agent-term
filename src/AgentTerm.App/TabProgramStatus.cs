using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using AgentTerm.Terminal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace AgentTerm_App;

internal sealed class TabProgramStatus
{
    private readonly TabViewItem item;
    private readonly Flyout _details = new();
    private readonly ToolTip _suppressedTitleTip = new() { IsEnabled = false, Visibility = Visibility.Collapsed };
    private readonly ProgramStatusCard _card = new();
    private readonly DispatcherTimer _openTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private FrameworkElement? _hoveredTarget;
    private FrameworkElement? _target;
    private bool _inCard;
    private bool _pinned;
    private XamlRoot? _root;
    private static TabProgramStatus? _openStatus;
    internal ProgramStatus? Primary => ProgramStatusPresentation.ActivePrimary(_records);
    private string? _app;
    private IconSource? _programIcon;

    internal TabProgramStatus(TabViewItem item)
    {
        this.item = item;
        _details.Content = _card;
        _details.FlyoutPresenterStyle = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="FlyoutPresenter">
                <Setter Property="Background" Value="{ThemeResource AcrylicInAppFillColorDefaultBrush}"/>
                <Setter Property="Padding" Value="16"/>
                <Setter Property="CornerRadius" Value="8"/>
            </Style>
            """);
        _details.Opened += (_, _) =>
        {
            _card.Scroll.ChangeView(null, 0, null, true);
            if (_pinned) _card.Scroll.Focus(FocusState.Programmatic);
        };
        _details.Closed += (_, _) =>
        {
            _closeTimer.Stop();
            _inCard = false;
            if (_root is { } root)
            {
                root.Content.RemoveHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(RootKeyDown));
                root.Changed -= RootChanged;
                _root = null;
            }
            if (_openStatus == this) _openStatus = null;
        };
        _card.KeepOpen += () => { if (_target is { } target) ShowDetails(target); };
        _card.Dismiss += Hide;
        _card.PointerEntered += (_, _) => { _inCard = true; _closeTimer.Stop(); };
        _card.PointerExited += (_, _) => { _inCard = false; ScheduleClose(); };
        _card.KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape) { Hide(); e.Handled = true; } };
        _openTimer.Tick += (_, _) =>
        {
            _openTimer.Stop();
            if (_hoveredTarget is { } target && _records.Count > 0) Show(target, false);
        };
        _closeTimer.Tick += (_, _) => { _closeTimer.Stop(); if (!_inCard && _hoveredTarget is null && !_pinned) Hide(); };
        Attach(item);
    }

    internal void Attach(FrameworkElement target)
    {
        target.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(PointerEntered), true);
        target.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(PointerExited), true);
        target.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PointerPressed), true);
        target.KeyDown += KeyDown;
        target.Unloaded += TargetUnloaded;
    }

    internal void Detach(FrameworkElement target)
    {
        target.RemoveHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(PointerEntered));
        target.RemoveHandler(UIElement.PointerExitedEvent, new PointerEventHandler(PointerExited));
        target.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PointerPressed));
        target.KeyDown -= KeyDown;
        target.Unloaded -= TargetUnloaded;
        if (_hoveredTarget == target) { _hoveredTarget = null; _openTimer.Stop(); }
        if (_target == target) Hide();
    }

    private void TargetUnloaded(object sender, RoutedEventArgs e)
    {
        _openTimer.Stop();
        _hoveredTarget = null;
        if (ReferenceEquals(_target, sender)) Hide();
    }

    private void PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _hoveredTarget = (FrameworkElement)sender;
        _closeTimer.Stop();
        if (!_details.IsOpen && _records.Count > 0) _openTimer.Start();
    }

    private void PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hoveredTarget = null;
        _openTimer.Stop();
        ScheduleClose();
    }

    private void PointerPressed(object sender, PointerRoutedEventArgs e) => Hide();
    private void ScheduleClose() { if (!_pinned) _closeTimer.Start(); }
    private void KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _details.IsOpen) { Hide(); e.Handled = true; }
        else if (e.Key == VirtualKey.Down && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            ShowDetails((FrameworkElement)sender);
            e.Handled = true;
        }
    }

    internal void ShowDetails(FrameworkElement target) => Show(target, true);

    private void RootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        Hide();
        e.Handled = _pinned;
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeCard();

    private void SizeCard()
    {
        if (_target is not { } target) return;
        _card.RequestedTheme = target.ActualTheme;
        _card.Width = Math.Max(180, Math.Min(_records.Count <= 1 ? 320 : 360, target.XamlRoot.Size.Width - 48));
        _card.MaxHeight = Math.Max(120, Math.Min(480, target.XamlRoot.Size.Height - 80));
    }

    private void Show(FrameworkElement target, bool pinned)
    {
        _openTimer.Stop();
        _closeTimer.Stop();
        if (_openStatus is { } previous && previous != this) previous.Hide();
        _openStatus = this;
        _target = target;
        _pinned = pinned;
        if (_root is null)
        {
            _root = target.XamlRoot;
            _root.Content.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(RootKeyDown), true);
            _root.Changed += RootChanged;
        }
        SizeCard();
        _card.Update(_title, _records, pinned);
        _details.ShowAt(target, new FlyoutShowOptions
        {
            Placement = target is SidebarTabItem ? FlyoutPlacementMode.RightEdgeAlignedTop : FlyoutPlacementMode.BottomEdgeAlignedLeft,
            ShowMode = pinned ? FlyoutShowMode.Standard : FlyoutShowMode.Transient
        });
        if (pinned && _details.IsOpen) _card.Scroll.Focus(FocusState.Programmatic);
    }

    internal void Hide()
    {
        _openTimer.Stop();
        _closeTimer.Stop();
        _details.Hide();
    }

    internal static readonly DependencyProperty DetailsProperty = DependencyProperty.RegisterAttached(
        "Details", typeof(string), typeof(TabProgramStatus), new PropertyMetadata(""));
    private IReadOnlyList<PaneProgramStatus> _records = [];
    private string _title = "";
    private IconSource? _shellIcon;
    private ProgramStatusPresentation _presentation = new("", "", "");

    internal void SetShellIcon(IconSource icon)
    {
        _shellIcon = icon;
        RefreshIcon();
    }

    internal void Update(IReadOnlyList<PaneProgramStatus> records)
    {
        var title = item.Header as string ?? "";
        if (_title == title && _records.SequenceEqual(records)) return;
        var hadRecords = _records.Count > 0;
        _title = title;
        _records = records;
        _presentation = ProgramStatusPresentation.Create(records);
        if (!hadRecords && records.Count > 0 && _hoveredTarget is not null) _openTimer.Start();
        if (_details.IsOpen)
        {
            if (records.Count == 0) Hide();
            else _card.Update(title, records, _pinned);
        }
        var app = Primary?.EffectiveApp;
        if (_app != app)
        {
            _app = app;
            _programIcon = AgentMarks.For(app);
            if (_programIcon is null) _ = LoadProgramIconAsync(app);
        }
        RefreshIcon();
        item.SetValue(DetailsProperty, _presentation.Details);
        AutomationProperties.SetHelpText(item, records.Count == 0 ? "" : _presentation.Details + "\nAlt+Down opens program status. Escape dismisses it.");
        ToolTipService.SetToolTip(item, records.Count == 0 ? null : _suppressedTitleTip);
    }

    private void RefreshIcon() => item.IconSource = Primary?.State switch
    {
        ProgramState.Blocked or ProgramState.Error => new FontIconSource { Glyph = _presentation.Glyph },
        _ => _programIcon ?? _shellIcon
    };

    private async Task LoadProgramIconAsync(string? app)
    {
        if (app is null) return;
        try
        {
            var bytes = await Task.Run(() =>
            {
                foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                {
                    if (!Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal)) continue;
                    var path = Path.Combine(directory, app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app : app + ".exe");
                    if (File.Exists(path)) return ExecutableIcon.Read(path);
                }
                return null;
            });
            if (bytes is null || _app != app) return;
            using var stream = new MemoryStream(bytes).AsRandomAccessStream();
            var image = new BitmapImage { DecodePixelWidth = 32 };
            await image.SetSourceAsync(stream);
            if (_app != app) return;
            _programIcon = new ImageIconSource { ImageSource = image };
            RefreshIcon();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException or ArgumentException) { }
    }
}
