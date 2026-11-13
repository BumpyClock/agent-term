using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AgentTerm_App;

public sealed partial class MainWindow
{
    private FrameworkElement? _captionSpacer;
    private double _gitHubFailedWidth;
    private bool _fittingGitHub;

    private void CaptionInset_Loaded(object sender, RoutedEventArgs e)
    {
        if (_captionSpacer is not null)
        {
            return;
        }

        _captionSpacer = (FrameworkElement)sender;
        _captionSpacer.XamlRoot.Changed += (_, _) => UpdateCaptionSpacer();
        AppWindow.Changed += (_, _) => DispatcherQueue.TryEnqueue(UpdateCaptionSpacer);
        WindowRoot.SizeChanged += (_, _) => UpdateGitHubFit();
        if (GitHubButton.Parent is FrameworkElement footer) footer.SizeChanged += (_, _) => UpdateGitHubFit();
        GitHubButton.FocusReturnRequested += () => _visibleTab?.FocusActivePane();
        GitHubButton.VerticalAlignment = VerticalAlignment.Top;
        Closed += (_, _) => GitHubButton.Shutdown();
        UpdateCaptionSpacer();
    }

    private void UpdateCaptionSpacer()
    {
        if (_captionSpacer?.XamlRoot is not { } root || AppWindow is not { } appWindow)
        {
            return;
        }

        var width = appWindow.TitleBar.RightInset / root.RasterizationScale;
        if (!double.IsFinite(width) || width < 0)
        {
            return;
        }

        _captionSpacer.Width = width;
        UpdateGitHubFit();
    }

    private void UpdateGitHubFit()
    {
        if (_closing || _captionSpacer is null || _fittingGitHub || WindowRoot.ActualWidth <= 0)
        {
            return;
        }

        _fittingGitHub = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_closing) return;
                WindowRoot.UpdateLayout();
                var width = WindowRoot.ActualWidth;
                if (GitHubButton.Visibility == Visibility.Collapsed)
                {
                    if (width <= _gitHubFailedWidth + 0.5)
                    {
                        return;
                    }

                    GitHubButton.SetAvailable(true);
                    WindowRoot.UpdateLayout();
                }

                var right = GitHubButton.TransformToVisual(WindowRoot).TransformPoint(new Point(GitHubButton.ActualWidth, 0)).X;
                if (right > width - _captionSpacer.Width + 0.5)
                {
                    _gitHubFailedWidth = width;
                    GitHubButton.SetAvailable(false);
                }
            }
            finally
            {
                _fittingGitHub = false;
            }
        })) _fittingGitHub = false;
    }
}
