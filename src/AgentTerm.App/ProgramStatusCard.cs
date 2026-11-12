using System;
using System.Collections.Generic;
using System.Linq;
using AgentTerm.Terminal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
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
    private readonly Dictionary<(int Pane, string Id), Grid> _entries = [];
    private (int Pane, string Shell, string Id)[] _order = [];
    private readonly Dictionary<(int Pane, string Shell), TextBlock> _headings = [];
    internal ScrollViewer Scroll { get; }
    internal event Action? KeepOpen;
    internal event Action? Dismiss;

    internal ProgramStatusCard()
    {
        var layout = (Grid)XamlReader.Load("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
                <StackPanel x:Name="Header" Spacing="2" VerticalAlignment="Center">
                    <TextBlock x:Name="Title" FontSize="14" FontWeight="SemiBold" TextWrapping="Wrap"/>
                    <TextBlock x:Name="Count" FontSize="12" Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
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
        _count.Text = records.Count == 0 ? "No program status reported." : paneCount > 1 ? $"{records.Count} programs · {paneCount} panes" : $"{records.Count} programs";
        var urgent = records.OrderBy(r => ProgramStatusPresentation.Priority(r.Status.State)).FirstOrDefault();
        if (records.Count > 1 && urgent?.Status.State is ProgramState.Blocked or ProgramState.Error)
            _count.Text = $"{records.Count} programs · {ProgramStatusPresentation.Label(urgent.Status)}";
        _count.Visibility = records.Count == 1 ? Visibility.Collapsed : Visibility.Visible;
        _pin.Visibility = pinned ? Visibility.Collapsed : Visibility.Visible;
        _close.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
        var duplicateTitle = records.Count == 1 && DisplayName(records[0].Status).Equals(title, StringComparison.OrdinalIgnoreCase);
        _header.Visibility = duplicateTitle ? Visibility.Collapsed : Visibility.Visible;
        _header.Margin = new Thickness(0, 0, pinned ? 44 : 0, 0);
        Scroll.Margin = new Thickness(0, duplicateTitle ? 0 : 12, duplicateTitle && pinned ? 40 : 0, 0);
        SetRow(_close, duplicateTitle ? 1 : 0);
        var groups = records.GroupBy(r => (r.Pane, r.Shell))
            .OrderBy(g => g.Min(r => ProgramStatusPresentation.Priority(r.Status.State))).ThenBy(g => g.Key.Pane);
        var ordered = groups.SelectMany(g => g.OrderBy(r => r.Status.Id, Comparer<string>.Create(ProgramStatusPresentation.CompareIds))).ToArray();
        var order = ordered.Select(r => (r.Pane, r.Shell, r.Status.Id)).ToArray();
        Grid? anchor = null;
        double anchorTop = 0;
        if (!_order.SequenceEqual(order))
        {
            _order = order;
            anchor = _entries.Values.FirstOrDefault(row => row.ActualHeight > 0 && row.TransformToVisual(Scroll).TransformPoint(new(0, row.ActualHeight)).Y > 0);
            anchorTop = anchor?.TransformToVisual(Scroll).TransformPoint(new(0, 0)).Y ?? 0;
            var desired = new List<UIElement>();
            int? previousPane = null;
            foreach (var record in ordered)
            {
                if (record.Pane != previousPane && paneCount > 1)
                {
                    if (!_headings.TryGetValue((record.Pane, record.Shell), out var heading))
                        _headings[(record.Pane, record.Shell)] = heading = (TextBlock)XamlReader.Load("""
                        <TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" FontSize="12"
                                   TextWrapping="Wrap" Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                        """);
                    heading.Text = $"Pane {record.Pane} · {record.Shell}";
                    heading.Margin = new Thickness(0, previousPane is null ? 0 : 12, 0, -4);
                    desired.Add(heading);
                }
                previousPane = record.Pane;
                if (!_entries.TryGetValue((record.Pane, record.Status.Id), out var row))
                {
                    row = (Grid)XamlReader.Load("""
                    <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" RowSpacing="4" Padding="0,4">
                        <Grid.Resources>
                            <Style x:Key="Done" TargetType="TextBlock"><Setter Property="Foreground" Value="{ThemeResource SystemFillColorSuccessBrush}"/></Style>
                            <Style x:Key="Blocked" TargetType="TextBlock"><Setter Property="Foreground" Value="{ThemeResource SystemFillColorCautionBrush}"/></Style>
                            <Style x:Key="Error" TargetType="TextBlock"><Setter Property="Foreground" Value="{ThemeResource SystemFillColorCriticalBrush}"/></Style>
                            <Style x:Key="Normal" TargetType="TextBlock"><Setter Property="Foreground" Value="{ThemeResource TextFillColorPrimaryBrush}"/></Style>
                        </Grid.Resources>
                        <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
                        <Grid ColumnSpacing="12">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                            <TextBlock x:Name="Name" FontSize="14" FontWeight="SemiBold" TextWrapping="Wrap" IsTextSelectionEnabled="True"/>
                            <TextBlock x:Name="State" Grid.Column="1" FontSize="12" MaxWidth="150" TextWrapping="Wrap" VerticalAlignment="Top" Margin="0,2,0,0"/>
                        </Grid>
                        <TextBlock x:Name="Message" Grid.Row="1" FontSize="14" TextWrapping="Wrap" IsTextSelectionEnabled="True"/>
                        <TextBlock x:Name="Identity" Grid.Row="2" FontSize="12" TextWrapping="Wrap" IsTextSelectionEnabled="True"
                                   Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                    </Grid>
                    """);
                    row.KeyDown += (_, e) =>
                    {
                        // Text navigation belongs to the selectable text, not its enclosing ScrollViewer.
                        if (e.OriginalSource is TextBlock && e.Key is VirtualKey.Home or VirtualKey.End) e.Handled = true;
                    };
                    _entries.Add((record.Pane, record.Status.Id), row);
                }
                desired.Add(row);
            }
            foreach (var stale in _entries.Keys.Where(key => !ordered.Any(r => r.Pane == key.Pane && r.Status.Id == key.Id)).ToArray()) _entries.Remove(stale);
            foreach (var stale in _headings.Keys.Where(key => !ordered.Any(r => r.Pane == key.Pane && r.Shell == key.Shell)).ToArray()) _headings.Remove(stale);
            foreach (var child in _rows.Children.Where(child => !desired.Contains(child)).ToArray()) _rows.Children.Remove(child);
            foreach (var child in desired.Where(child => !_rows.Children.Contains(child))) _rows.Children.Add(child);
            var selected = _entries.Values.FirstOrDefault(row => new[] { "Name", "Message", "Identity" }.Any(name => ((TextBlock)row.FindName(name)).SelectedText.Length > 0));
            TabSidebarLayout.ReorderAround(_rows.Children, desired, selected);

        }
        foreach (var record in ordered)
        {
            var status = record.Status;
            var row = _entries[(record.Pane, status.Id)];
            var name = DisplayName(status);
            SetSelectableText((TextBlock)row.FindName("Name"), name);
            var mark = status.State switch { ProgramState.Done => "✓", ProgramState.Error => "!", ProgramState.Blocked => "!", ProgramState.Working => "◌", _ => "·" };
            var state = (TextBlock)row.FindName("State");
            state.Text = $"{mark} {ProgramStatusPresentation.Label(status)}";
            state.Style = (Style)row.Resources[status.State is ProgramState.Done or ProgramState.Blocked or ProgramState.Error ? status.State.ToString() : "Normal"];
            var message = (TextBlock)row.FindName("Message");
            SetSelectableText(message, ProgramStatusPresentation.Clean(status.Message ?? ""));
            message.Visibility = message.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            var identity = (TextBlock)row.FindName("Identity");
            var time = status.UpdatedAt.ToLocalTime();
            var timestamp = time.Date == DateTime.Now.Date ? $"{time:HH:mm:ss}" : $"{time:MMM d, HH:mm:ss}";
            SetSelectableText(identity, string.Join(" · ", new[] { status.EffectiveApp != name ? status.EffectiveApp : null, status.Id.Length > 0 && !status.Id.Equals(name, StringComparison.OrdinalIgnoreCase) ? status.Id : null, timestamp }.Where(s => !string.IsNullOrEmpty(s))));
            AutomationProperties.SetName(identity, $"{identity.Text}. Updated {time:g}");
            var ancestors = ordered.Count(parent => parent.Pane == record.Pane && parent.Status.Id != status.Id &&
                (parent.Status.Id.Length == 0 || status.Id.StartsWith(parent.Status.Id + "/", StringComparison.Ordinal)));
            row.Margin = new Thickness(ancestors * 12, 0, 0, 0);
        }
        if (anchor is not null && _rows.Children.Contains(anchor))
        {
            UpdateLayout();
            var shift = anchor.TransformToVisual(Scroll).TransformPoint(new(0, 0)).Y - anchorTop;
            Scroll.ChangeView(null, Scroll.VerticalOffset + shift, null, true);
        }
    }
    private static string DisplayName(ProgramStatus status) => ProgramStatusPresentation.Clean(status.Title ?? status.EffectiveApp ?? (status.Id.Length == 0 ? "Program" : status.Id));
    private static void SetSelectableText(TextBlock block, string text)
    {
        // Assigning even the same value clears WinUI's text selection.
        if (block.Text != text) block.Text = text;
    }
}
