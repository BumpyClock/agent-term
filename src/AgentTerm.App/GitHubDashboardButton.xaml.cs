using System;
using System.IO;
using System.Threading.Tasks;
using AgentTerm_App.GitHub;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AgentTerm_App;

internal sealed partial class GitHubDashboardButton : UserControl
{
    private const double MaximumWidth = 420;
    private const double MaximumHeight = 720;
    private const double WindowMargin = 8;
    private const double AvatarRightInset = 8;
    private const double AvatarSize = 24;
    private static readonly TimeSpan IdentityDelay = TimeSpan.FromSeconds(5);

    private readonly GitHubAvatar _avatar = new(AvatarSize, 96);
    private readonly GitHubReveal _reveal = new();
    private XamlRoot? _root;
    private FrameworkElement? _presenter;
    private Size? _openSize;
    private double _avatarTop;
    private double _avatarRight;
    private bool _openedWithKeyboard;
    private bool _closeNow;
    private bool _warmed;
    private GitHubDashboardView? _dashboard;

    public GitHubDashboardButton()
    {
        InitializeComponent();
        AutomationProperties.SetName(DashboardButton, GitHubStrings.Dashboard);
        ToolTipService.SetToolTip(DashboardButton, GitHubStrings.ButtonToolTip);
        AvatarHost.Child = _avatar;
        GitHubMotion.AttachPress(DashboardButton, _avatar, 0.88f);
        _avatar.PictureFailed += () => ToolTipService.SetToolTip(DashboardButton, GitHubStrings.AvatarUnavailable);
        DashboardButton.PointerEntered += (_, _) => EnsureDashboard();
        Loaded += (_, _) =>
        {
            _dashboard?.Start();
            Warm();
        };
        Unloaded += (_, _) => Shutdown();
    }

    public event Action? FocusReturnRequested;

    private GitHubDashboardView Dashboard => EnsureDashboard();

    private GitHubDashboardView EnsureDashboard()
    {
        if (_dashboard is { } existing) return existing;
        var view = _dashboard = new GitHubDashboardView();
        view.IdentityChanged += (login, url) =>
        {
            ToolTipService.SetToolTip(DashboardButton, GitHubStrings.ButtonToolTip);
            _avatar.Set(login, url);
        };
        view.CloseRequested += () => DashboardFlyout.Hide();
        view.Loaded += (_, _) => PrepareReveal();
        DashboardFlyout.Content = view;
        view.Start();
        return view;
    }

    private async void Warm()
    {
        if (_warmed) return;
        _warmed = true;
        var queue = DispatcherQueue;
        try
        {
            var cached = await Task.Run(() => GitHubCache.Read(GitHubCache.DefaultPath()));
            if (_dashboard is null && cached.Login.Length != 0) _avatar.Set(cached.Login, cached.AvatarUrl);
        }
        catch (Exception exception) when (exception is GitHubException or IOException or UnauthorizedAccessException)
        {
        }
        var idle = queue.CreateTimer();
        idle.Interval = IdentityDelay;
        idle.IsRepeating = false;
        idle.Tick += (_, _) => EnsureDashboard();
        idle.Start();
    }

    public void Shutdown()
    {
        HideNow();
        _dashboard?.Shutdown();
    }

    public void SetAvailable(bool available)
    {
        if (!available) HideNow();
        Visibility = available ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideNow()
    {
        _closeNow = true;
        DashboardFlyout.Hide();
        _closeNow = false;
    }

    private void DashboardButton_Click(object sender, RoutedEventArgs e)
    {
        var panel = Panel();
        var origin = DashboardButton.TransformToVisual(null).TransformPoint(new Point(0, 0));
        // With a point, BottomEdgeAlignedRight puts the panel's top-right corner on it.
        DashboardFlyout.ShowAt(DashboardButton, new FlyoutShowOptions
        {
            Position = new Point(panel.Right - origin.X, panel.Top - origin.Y),
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShowMode = FlyoutShowMode.Standard
        });
    }

    private void DashboardFlyout_Opening(object sender, object e)
    {
        _openedWithKeyboard = DashboardButton.FocusState == FocusState.Keyboard;
        Layout();
        _reveal.Arm();
        _openSize = XamlRoot?.Size;
        _root = XamlRoot;
        if (_root is not null) _root.Changed += Root_Changed;
        Dashboard.Opening(_avatar);
    }

    private void DashboardFlyout_Opened(object sender, object e)
    {
        PrepareReveal();
        _reveal.Open();
        Dashboard.Opened(_openedWithKeyboard ? FocusState.Keyboard : FocusState.Programmatic);
    }

    private void DashboardFlyout_Closing(FlyoutBase sender, FlyoutBaseClosingEventArgs args)
    {
        if (_closeNow || _reveal.Folding || !_reveal.CanFold || XamlRoot?.Size != _openSize) return;
        args.Cancel = true;
        Dashboard.IsHitTestVisible = false;
        _reveal.Close(() =>
        {
            if (DashboardFlyout.IsOpen) HideNow();
        });
    }

    private void DashboardFlyout_Closed(object sender, object e)
    {
        if (_root is not null) _root.Changed -= Root_Changed;
        _root = null;
        _reveal.Stop();
        Dashboard.IsHitTestVisible = true;
        Dashboard.Closed();
        if (!_openedWithKeyboard) FocusReturnRequested?.Invoke();
    }

    private void PrepareReveal()
    {
        _presenter ??= Presenter();
        if (_presenter is not null)
            _reveal.Prepare(_presenter, (float)_avatarTop, (float)_avatarRight, (float)AvatarSize,
                (float)((CornerRadius)Application.Current.Resources["OverlayCornerRadius"]).TopRight);
    }

    private FrameworkElement? Presenter()
    {
        for (DependencyObject? node = Dashboard; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FlyoutPresenter presenter) return presenter;
        return null;
    }

    // A window resize closes the flyout on its own; this keeps the content sized through DPI and visibility changes.
    private void Root_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => Layout();

    private void Layout()
    {
        var panel = Panel();
        var border = (Thickness)Application.Current.Resources["FlyoutBorderThemeThickness"];
        var avatar = AvatarHost.TransformToVisual(null).TransformBounds(new Rect(0, 0, AvatarSize, AvatarSize));
        _avatarTop = avatar.Top - panel.Top;
        _avatarRight = panel.Right - avatar.Right;
        Dashboard.Resize(
            Math.Max(0, panel.Width - border.Left - border.Right),
            Math.Max(0, panel.Height - border.Top - border.Bottom),
            XamlRoot?.RasterizationScale ?? 1,
            _avatarTop - border.Top,
            _avatarRight - border.Right);
    }

    private Rect Panel()
    {
        var size = XamlRoot?.Size ?? new Size(MaximumWidth, MaximumHeight);
        var avatar = AvatarHost.TransformToVisual(null).TransformBounds(new Rect(0, 0, AvatarSize, AvatarSize));
        var right = avatar.Right + AvatarRightInset;
        var width = Math.Max(0, Math.Min(MaximumWidth, right - WindowMargin));
        var height = Math.Max(0, Math.Min(MaximumHeight, size.Height - WindowMargin));
        return new Rect(right - width, 0, width, height);
    }
}
