using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AgentTerm_App;

public sealed partial class PaneHeader : UserControl
{
    internal const double ContextMinimum = 72;
    private AgentStatusBar? _agentBar;
    internal event Action<bool>? SplitRequested;
    internal event Action? CloseRequested;

    public PaneHeader() => InitializeComponent();

    public object? Context
    {
        get => ContextHost.Content;
        set => ContextHost.Content = value;
    }

    internal bool Compact => PaneMenuButton.Visibility == Visibility.Visible;

    internal AgentStatusBar? AgentBar
    {
        get => _agentBar;
        set
        {
            if (_agentBar is not null) _agentBar.Changed -= Relayout;
            _agentBar = value;
            AgentHost.Content = value;
            if (value is not null) value.Changed += Relayout;
            Relayout();
        }
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => Relayout();

    private void Relayout()
    {
        if (Root.ActualWidth <= 0) return;
        var actions = PaneActions.Children.Sum(child => ((FrameworkElement)child).Width) + PaneActions.Spacing * (PaneActions.Children.Count - 1);
        var agent = 0.0;
        if (_agentBar is not null)
        {
            _agentBar.Compact = false;
            AgentHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            agent = AgentHost.DesiredSize.Width;
        }
        var available = Root.ActualWidth - Root.Padding.Left - Root.Padding.Right - 2 * Root.ColumnSpacing - ContextMinimum;
        var compact = agent + actions > available;
        if (_agentBar is not null) _agentBar.Compact = compact;
        PaneActions.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PaneMenuButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SplitRight_Click(object sender, RoutedEventArgs e) => SplitRequested?.Invoke(true);
    private void SplitDown_Click(object sender, RoutedEventArgs e) => SplitRequested?.Invoke(false);
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
