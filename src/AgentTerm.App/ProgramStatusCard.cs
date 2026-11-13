using System;
using System.Collections.Generic;
using System.Linq;
using AgentTerm.Terminal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace AgentTerm_App;

// Hover and keyboard access use the same live, selectable content.
internal sealed partial class ProgramStatusCard : Grid
{
    private readonly TextBlock _title;
    private readonly TextBlock _count;
    private readonly FrameworkElement _header;
    private readonly StackPanel _rows;
    private readonly Button _pin;
    private readonly Button _close;
    private readonly Dictionary<(int Pane, string Id), ReportRow> _entries = [];
    private readonly IProgramIconPreferences? _iconPreferences;
    private (int Pane, string Id)[] _order = [];
    private readonly Dictionary<int, TextBlock> _headings = [];
    internal ScrollViewer Scroll { get; }
    internal event Action? KeepOpen;
    internal event Action? Dismiss;

    internal ProgramStatusCard(IProgramIconPreferences? iconPreferences = null)
    {
        _iconPreferences = iconPreferences;
        var layout = (Grid)XamlReader.Load("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
                <StackPanel x:Name="Header" Spacing="2" VerticalAlignment="Center">
                    <TextBlock x:Name="Title" FontSize="14" FontWeight="SemiBold" TextWrapping="Wrap"/>
                    <TextBlock x:Name="Count" FontSize="12" TextWrapping="Wrap" Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                </StackPanel>
                <Button x:Name="Close" Width="32" Height="32" Padding="0" VerticalAlignment="Top" HorizontalAlignment="Right"
                        Style="{ThemeResource SubtleButtonStyle}" AutomationProperties.Name="Dismiss program status">
                    <FontIcon Glyph="&#xE711;" FontSize="12"/>
                </Button>
                <ScrollViewer x:Name="Scroll" Grid.Row="1" VerticalScrollBarVisibility="Auto"
                              HorizontalScrollMode="Disabled" HorizontalScrollBarVisibility="Disabled"
                              IsTabStop="True" AutomationProperties.Name="Program status reports">
                    <StackPanel x:Name="Rows" Spacing="8" Margin="4,2,12,4"/>
                </ScrollViewer>
                <Button x:Name="Pin" Grid.Row="2" HorizontalAlignment="Stretch" HorizontalContentAlignment="Left"
                        Style="{ThemeResource SubtleButtonStyle}" Padding="8,6" Margin="0,12,0,0" AutomationProperties.Name="Keep program status open">
                    <Grid ColumnSpacing="16">
                        <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                        <TextBlock Text="Keep open" FontSize="12"/>
                        <TextBlock Grid.Column="1" Text="Alt+↓" FontSize="12" Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                    </Grid>
                </Button>
            </Grid>
            """);
        Children.Add(layout);
        _title = (TextBlock)layout.FindName("Title");
        _count = (TextBlock)layout.FindName("Count");
        _header = (FrameworkElement)layout.FindName("Header");
        _rows = (StackPanel)layout.FindName("Rows");
        _pin = (Button)layout.FindName("Pin");
        Scroll = (ScrollViewer)layout.FindName("Scroll");
        _pin.Click += (_, _) => KeepOpen?.Invoke();
        _close = (Button)layout.FindName("Close");
        _close.Click += (_, _) => Dismiss?.Invoke();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (VirtualKey.Home or VirtualKey.End) ||
                !ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot), Scroll)) return;
            Scroll.ChangeView(null, e.Key == VirtualKey.Home ? 0 : Scroll.ScrollableHeight, null, true);
            e.Handled = true;
        };
    }

    internal void Update(string title, IReadOnlyList<PaneProgramStatus> records, bool pinned)
    {
        _title.Text = title;
        var paneCount = records.Select(r => r.Pane).Distinct().Count();
        _count.Text = Summary(records, paneCount);
        _count.Visibility = records.Count == 1 ? Visibility.Collapsed : Visibility.Visible;
        _pin.Visibility = pinned ? Visibility.Collapsed : Visibility.Visible;
        _close.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
        var duplicateTitle = records.Count == 1 && ProgramStatusPresentation.Name(records[0].Status).Equals(title, StringComparison.OrdinalIgnoreCase);
        _header.Visibility = duplicateTitle ? Visibility.Collapsed : Visibility.Visible;
        _header.Margin = new Thickness(0, 0, pinned ? 44 : 0, 0);
        Scroll.Margin = new Thickness(0, duplicateTitle ? 0 : 12, duplicateTitle && pinned ? 40 : 0, 0);
        SetRow(_close, duplicateTitle ? 1 : 0);
        var groups = records.GroupBy(r => r.Pane)
            .OrderBy(g => g.Min(r => ProgramStatusPresentation.Priority(r.Status.State))).ThenBy(g => g.Key);
        var ordered = groups.SelectMany(g => g.OrderBy(r => r.Status.Id, Comparer<string>.Create(ProgramStatusPresentation.CompareIds))).ToArray();
        var order = ordered.Select(r => (r.Pane, r.Status.Id)).ToArray();
        var retainedRows = ordered.Select(record => _entries.GetValueOrDefault((record.Pane, record.Status.Id))?.Root).OfType<Grid>().ToHashSet();
        var anchor = _rows.Children.OfType<Grid>().FirstOrDefault(row => retainedRows.Contains(row) && row.ActualHeight > 0 &&
            row.TransformToVisual(Scroll).TransformPoint(new(0, 0)).Y < Scroll.ViewportHeight &&
            row.TransformToVisual(Scroll).TransformPoint(new(0, row.ActualHeight)).Y > 0);
        var anchorTop = anchor?.TransformToVisual(Scroll).TransformPoint(new(0, 0)).Y ?? 0;
        if (!_order.SequenceEqual(order))
        {
            _order = order;
            var desired = new List<UIElement>();
            int? previousPane = null;
            foreach (var record in ordered)
            {
                if (record.Pane != previousPane && paneCount > 1)
                {
                    if (!_headings.TryGetValue(record.Pane, out var heading))
                        _headings[record.Pane] = heading = (TextBlock)XamlReader.Load("""
                        <TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" FontSize="12"
                                   TextWrapping="Wrap" TextTrimming="CharacterEllipsis" MaxLines="2"
                                   Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                        """);
                    heading.Margin = new Thickness(0, previousPane is null ? 0 : 12, 0, -4);
                    desired.Add(heading);
                }
                previousPane = record.Pane;
                if (!_entries.TryGetValue((record.Pane, record.Status.Id), out var row))
                    _entries.Add((record.Pane, record.Status.Id), row = new ReportRow(_iconPreferences));
                desired.Add(row.Root);
            }
            foreach (var stale in _entries.Keys.Where(key => !ordered.Any(r => r.Pane == key.Pane && r.Status.Id == key.Id)).ToArray()) _entries.Remove(stale);
            foreach (var stale in _headings.Keys.Where(pane => !ordered.Any(r => r.Pane == pane)).ToArray()) _headings.Remove(stale);
            foreach (var child in _rows.Children.Where(child => !desired.Contains(child)).ToArray()) _rows.Children.Remove(child);
            foreach (var child in desired.Where(child => !_rows.Children.Contains(child))) _rows.Children.Add(child);
            var selected = _entries.Values.FirstOrDefault(row => row.HasSelection)?.Root;
            TabSidebarLayout.ReorderAround(_rows.Children, desired, selected);

        }
        // Pane labels follow program titles, which change without reordering any report.
        foreach (var record in ordered)
            if (_headings.TryGetValue(record.Pane, out var heading)) SetSelectableText(heading, ProgramStatusPresentation.PaneHeading(record));
        foreach (var record in ordered)
        {
            var ancestors = ordered.Count(parent => parent.Pane == record.Pane && parent.Status.Id != record.Status.Id &&
                (parent.Status.Id.Length == 0 || record.Status.Id.StartsWith(parent.Status.Id + "/", StringComparison.Ordinal)));
            _entries[(record.Pane, record.Status.Id)].Update(record.Status, ancestors);
        }
        if (anchor is not null && _rows.Children.Contains(anchor))
        {
            UpdateLayout();
            var shift = anchor.TransformToVisual(Scroll).TransformPoint(new(0, 0)).Y - anchorTop;
            if (shift != 0) Scroll.ChangeView(null, Scroll.VerticalOffset + shift, null, true);
        }
    }
    // A single blocked report leads with its exact request; everything else counts by state.
    private static string Summary(IReadOnlyList<PaneProgramStatus> records, int paneCount)
    {
        var parts = records.GroupBy(r => r.Status.State).OrderBy(g => ProgramStatusPresentation.Priority(g.Key)).Select(g => g.Key switch
        {
            ProgramState.Blocked when g.Count() == 1 => ProgramStatusPresentation.Label(g.Single().Status),
            ProgramState.Error => $"{g.Count()} failed",
            _ => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}"
        }).ToList();
        if (paneCount > 1) parts.Add($"{paneCount} panes");
        return string.Join(" · ", parts);
    }

    private static void SetSelectableText(TextBlock block, string text)
    {
        // Assigning even the same value clears WinUI's text selection.
        if (block.Text != text) block.Text = text;
    }

    // One report: its mark or state symbol, the tab's dot sweep while working, and selectable text.
    private sealed class ReportRow
    {
        private readonly TextBlock _name;
        private readonly TextBlock _state;
        private readonly TextBlock _message;
        private readonly TextBlock _identity;
        private readonly ProgramIconPresentation _icon;
        internal Grid Root { get; }

        internal ReportRow(IProgramIconPreferences? preferences)
        {
            Root = (Grid)XamlReader.Load("""
                <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" ColumnSpacing="8" RowSpacing="2" Padding="0,4">
                    <Grid.Resources>
                        <SolidColorBrush x:Key="Done" Color="{ThemeResource SystemFillColorSuccess}"/>
                        <SolidColorBrush x:Key="Blocked" Color="{ThemeResource SystemFillColorCaution}"/>
                        <SolidColorBrush x:Key="Error" Color="{ThemeResource SystemFillColorCritical}"/>
                        <SolidColorBrush x:Key="Normal" Color="{ThemeResource TextFillColorSecondary}"/>
                    </Grid.Resources>
                    <Grid.ColumnDefinitions><ColumnDefinition Width="20"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                    <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
                    <ContentPresenter x:Name="Lead" Width="20" Height="20" VerticalAlignment="Top"
                                    Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                    <TextBlock x:Name="Name" Grid.Column="1" FontSize="14" FontWeight="SemiBold" TextWrapping="Wrap" IsTextSelectionEnabled="True"/>
                    <TextBlock x:Name="State" Grid.Column="2" FontSize="12" MaxWidth="150" TextWrapping="Wrap" VerticalAlignment="Top" Margin="0,2,0,0"/>
                    <TextBlock x:Name="Message" Grid.Row="1" Grid.Column="1" Grid.ColumnSpan="2" FontSize="14" TextWrapping="Wrap" IsTextSelectionEnabled="True"
                               Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                    <TextBlock x:Name="Identity" Grid.Row="2" Grid.Column="1" Grid.ColumnSpan="2" FontSize="12" TextWrapping="Wrap" IsTextSelectionEnabled="True"
                               Foreground="{ThemeResource TextFillColorTertiaryBrush}"/>
                </Grid>
                """);
            _name = (TextBlock)Root.FindName("Name");
            _state = (TextBlock)Root.FindName("State");
            _message = (TextBlock)Root.FindName("Message");
            _identity = (TextBlock)Root.FindName("Identity");
            _icon = new ProgramIconPresentation(preferences);
            ((ContentPresenter)Root.FindName("Lead")).Content = _icon;
            Root.KeyDown += (_, e) =>
            {
                // Text navigation belongs to the selectable text, not its enclosing ScrollViewer.
                if (e.OriginalSource is TextBlock && e.Key is VirtualKey.Home or VirtualKey.End) e.Handled = true;
            };
        }

        internal bool HasSelection => _name.SelectedText.Length > 0 || _message.SelectedText.Length > 0 || _identity.SelectedText.Length > 0;

        internal void Update(ProgramStatus status, int depth)
        {
            var name = ProgramStatusPresentation.Name(status);
            SetSelectableText(_name, name);
            _state.Text = ProgramStatusPresentation.Label(status);
            var accent = status.State is ProgramState.Done or ProgramState.Blocked or ProgramState.Error ? (Brush)Root.Resources[status.State.ToString()] : null;
            _state.Foreground = accent ?? (Brush)Root.Resources["Normal"];
            SetSelectableText(_message, ProgramStatusPresentation.Clean(status.Message ?? ""));
            _message.Visibility = _message.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            var time = status.UpdatedAt.ToLocalTime();
            var timestamp = time.Date == DateTime.Now.Date ? $"{time:HH:mm:ss}" : $"{time:MMM d, HH:mm:ss}";
            var app = status.Title is null ? null : ProgramStatusPresentation.OwnApp(status);
            var identityText = app is null || app == name ? timestamp : $"{app} · {timestamp}";
            if (depth == 0 && status.Id.Contains('/')) identityText = $"{ProgramStatusPresentation.Clean(status.Id)} · {identityText}";
            SetSelectableText(_identity, identityText);
            AutomationProperties.SetName(_identity, $"{_identity.Text}. Updated {time:g}");
            // Nested rows start their symbol under the parent's name, so the tree reads as one column.
            Root.Margin = new Thickness(depth * 28, 0, 0, 0);

            // Attention states replace identity, as on the tab. Otherwise a known agent keeps its mark.
            var mark = status.State is not (ProgramState.Blocked or ProgramState.Error) &&
                ProgramStatusPresentation.IdentityApp(status) is { } identity && AgentMarks.Has(identity) ? identity : null;
            var glyph = ProgramStatusPresentation.GlyphFor(status);
            var source = mark is not null ? AgentMarks.For(mark) : new FontIconSource { Glyph = glyph, Foreground = accent };
            // Retained reports outlived their command, so only live work animates.
            _icon.Update(source, status.IsActive && status.State == ProgramState.Working, mark is null ? 14 : 16);
        }
    }
}
