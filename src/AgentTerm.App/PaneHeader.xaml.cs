using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AgentTerm_App;

public sealed partial class PaneHeader : UserControl
{
    internal const double ContextMinimum = 72;
    private PromptModeBar? _modeBar;
    internal event Action<bool>? SplitRequested;
    internal event Action? CloseRequested;

    public PaneHeader() => InitializeComponent();

    public object? Context
    {
        get => ContextHost.Content;
        set => ContextHost.Content = value;
    }

    internal bool Compact => PaneMenuButton.Visibility == Visibility.Visible;

    internal PromptModeBar? ModeBar
    {
        get => _modeBar;
        set
        {
            if (_modeBar is not null) _modeBar.Changed -= Relayout;
            _modeBar = value;
            ModeHost.Content = value;
            if (value is not null) value.Changed += Relayout;
            Relayout();
        }
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => Relayout();

    private void Relayout()
    {
        if (Root.ActualWidth <= 0) return;
        var actions = PaneActions.Children.Sum(child => ((FrameworkElement)child).Width) + PaneActions.Spacing * (PaneActions.Children.Count - 1);
        var mode = 0.0;
        if (_modeBar is not null)
        {
            _modeBar.Compact = false;
            ModeHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            mode = ModeHost.DesiredSize.Width;
        }
        var available = Root.ActualWidth - Root.Padding.Left - Root.Padding.Right - 2 * Root.ColumnSpacing - ContextMinimum;
        var compact = mode + actions > available;
        if (_modeBar is not null) _modeBar.Compact = compact;
        PaneActions.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PaneMenuButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SplitRight_Click(object sender, RoutedEventArgs e) => SplitRequested?.Invoke(true);
    private void SplitDown_Click(object sender, RoutedEventArgs e) => SplitRequested?.Invoke(false);
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
