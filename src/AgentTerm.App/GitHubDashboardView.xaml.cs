using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AgentTerm_App.GitHub;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;

namespace AgentTerm_App;

internal sealed partial class GitHubDashboardView : UserControl
{
    private const double MinimumListHeight = 32;
    private const double SectionMargin = 16;
    private const double ScrollGutter = 22;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly string[] SectionNames = [GitHubStrings.Activity, GitHubStrings.MyPrs, GitHubStrings.Reviews, GitHubStrings.Repos];
    private static readonly string[] EmptyTitles =
        [GitHubStrings.EmptyActivity, GitHubStrings.EmptyPulls, GitHubStrings.EmptyReviews, GitHubStrings.EmptyRepos];
    private static readonly string[] EmptyGlyphs = ["\uE81C", "\uE8AB", "\uE890", "\uE8B7"];

    private enum TakeoverState
    {
        None,
        SignedOut,
        MissingCli
    }

    private readonly DispatcherTimer _refreshTimer = new() { Interval = RefreshInterval };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly string _cachePath = GitHubCache.DefaultPath();
    private readonly RadioButton[] _tabs;
    private readonly TextBlock[] _tabCounts = new TextBlock[4];
    private readonly TextBlock[] _tabLabels = new TextBlock[4];
    private readonly GitHubAvatar _avatar = new(24, 96);
    private readonly GitHubSpinner _spinner;
    private readonly GitHubShimmer _shimmer;
    private readonly Dictionary<string, double> _bars = [];
    private readonly List<(TextBlock Text, DateTimeOffset At)> _times = [];
    private CancellationTokenSource _lifetime = new();
    private Task? _initialization;
    private Task _cacheWork = Task.CompletedTask;
    private GitHubSnapshot _snapshot = GitHubSnapshot.Empty;
    private GitHubSnapshot _lastGood = GitHubSnapshot.Empty;
    private GitHubFailure _failure;
    private TakeoverState _takeover;
    private int _generation;
    private int _section;
    private long _lastSuccess;
    private double _maxHeight = 720;
    private double _scale = 1;
    private bool _refreshing;
    private bool _refreshStarted;
    private bool _verified;
    private bool _cacheWarning;
    private bool _open;
    private bool _revealed;
    private bool _compact;
    private bool _readoutFlip;
    private bool _sectionFlip;
    private string _status = "";
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public GitHubDashboardView()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, GitHubStrings.Dashboard);
        AutomationProperties.SetName(TabStrip, GitHubStrings.Sections);
        AutomationProperties.SetName(RefreshButton, GitHubStrings.Refresh);
        ToolTipService.SetToolTip(RefreshButton, GitHubStrings.RefreshToolTip);
        AutomationProperties.SetName(CopyButton, GitHubStrings.Copy);
        ToolTipService.SetToolTip(CopyButton, GitHubStrings.Copy);
        AutomationProperties.SetHelpText(Heatmap, GitHubStrings.CalendarKeys);
        CopilotHeading.Text = GitHubStrings.Copilot;
        LegendLess.Text = GitHubStrings.Less;
        LegendMore.Text = GitHubStrings.More;
        StatusRetry.Content = GitHubStrings.Retry;
        TakeoverRetry.Content = GitHubStrings.Retry;
        _tabs = [ActivityTab, PullsTab, ReviewsTab, ReposTab];
        for (var index = 0; index < _tabs.Length; index++)
        {
            _tabCounts[index] = new TextBlock { Style = (Style)Resources["GitHubTertiaryText"], VerticalAlignment = VerticalAlignment.Center };
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            _tabLabels[index] = new TextBlock
            {
                Text = SectionNames[index],
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            label.Children.Add(_tabLabels[index]);
            label.Children.Add(_tabCounts[index]);
            _tabs[index].Content = label;
            _tabs[index].Checked += Tab_Checked;
        }
        TabStrip.PreviewKeyDown += TabStrip_KeyDown;
        ActivityTab.IsChecked = true;
        UpdateTabStops();
        HeaderAvatarHost.Child = _avatar;
        AutomationProperties.SetName(HeaderAvatarButton, GitHubStrings.CloseDashboard);
        GitHubMotion.AttachPress(HeaderAvatarButton, _avatar, 0.88f);
        _spinner = new GitHubSpinner(RefreshIcon);
        GitHubMotion.AttachPress(RefreshButton, RefreshIcon, 0.85f);
        _shimmer = new GitHubShimmer(GitHubMotion.Of(this).Compositor);
        BuildListSkeleton();
        foreach (var block in new[] { PlanSkeleton }.Concat(UsageSkeleton.Children.OfType<Grid>().SelectMany(row => row.Children.OfType<Border>()))
            .Concat(UsageSkeleton.Children.OfType<Border>()).Concat(Blocks(ListSkeleton)))
            _shimmer.Attach(block, (float)block.CornerRadius.TopLeft);
        GitHubMotion.Show(ReadoutB, 0);
        GitHubMotion.Show(SectionStatusB, 0);
        GitHubMotion.Show(CopiedIcon, 0);
        Heatmap.SelectionChanged += RenderReadout;
        Heatmap.PaletteChanged += RenderLegend;
        Heatmap.GeometryChanged += AlignToHeatmap;
        _refreshTimer.Tick += (_, _) => Refresh(GitHubRefreshReason.Automatic);
        _clock.Tick += (_, _) => RenderTimes();
        KeyDown += View_KeyDown;
        ActualThemeChanged += (_, _) =>
        {
            ApplyShimmerColor();
            RenderUsage();
            RenderList(0, false);
        };
        Loaded += (_, _) => ApplyShimmerColor();
        RenderAll();
    }

    public event Action<string, string>? IdentityChanged;

    public event Action? CloseRequested;

    private bool IsLoading => _refreshing || _initialization is { IsCompleted: false };

    public void Start()
    {
        if (_lifetime.IsCancellationRequested) _lifetime = new CancellationTokenSource();
        _initialization ??= InitializeAsync();
    }

    public void Shutdown()
    {
        _lifetime.Cancel();
        _refreshTimer.Stop();
        _clock.Stop();
    }

    public void Resize(double width, double maxHeight, double scale, double avatarTop, double avatarRight)
    {
        _scale = scale;
        Width = width;
        var hit = (HeaderAvatarButton.Width - HeaderAvatarHost.Width) / 2;
        Header.Margin = new Thickness(Header.Margin.Left, avatarTop - hit, avatarRight - hit, 0);
        _maxHeight = maxHeight;
        AlignToHeatmap();
        UpdateHeight();
    }

    private void AlignToHeatmap()
    {
        var available = Width - SectionMargin - ScrollGutter;
        if (!double.IsFinite(available) || available <= 0) return;
        var grid = GitHubPresentation.Heatmap(available, _scale, Heatmap.Columns).Width;
        var spare = Math.Max(0, Math.Floor((Width - grid) * _scale + 1e-6));
        var left = Math.Ceiling(SectionMargin * _scale - 1e-6);
        var right = Math.Max(Math.Floor(spare / 2), Math.Min(Math.Ceiling(ScrollGutter * _scale - 1e-6), spare - left));
        SetInset((spare - right) / _scale - SectionMargin, right / _scale - SectionMargin);
    }

    private void SetInset(double left, double right)
    {
        Fixed.Margin = new Thickness(left, 0, right, 0);
        ListPanel.Margin = new Thickness(8 + left, 0, 8 + right, 8);
        ListSkeleton.Margin = new Thickness(16 + left, 8, 16 + right, 0);
    }

    public void Opening(UIElement titleBarAvatar)
    {
        GitHubMotion.FollowScale(_avatar, titleBarAvatar);
        _open = true;
        _revealed = false;
        RenderTimes();
        _refreshTimer.Start();
        _clock.Start();
        if (_snapshot.Login.Length == 0 || _lastSuccess == 0 || Stopwatch.GetElapsedTime(_lastSuccess) >= RefreshInterval)
            Refresh(GitHubRefreshReason.Automatic);
        UpdateBusy();
        PlayEntrance();
    }

    public void Opened(FocusState focus)
    {
        if (_takeover != TakeoverState.None) TakeoverRetry.Focus(focus);
        else _tabs[_section].Focus(focus);
    }

    public void Closed()
    {
        _open = false;
        _refreshTimer.Stop();
        _clock.Stop();
        UpdateBusy();
    }

    private async Task InitializeAsync()
    {
        var cached = GitHubSnapshot.Empty;
        try
        {
            cached = await Task.Run(() => GitHubCache.Read(_cachePath));
        }
        catch (Exception exception) when (exception is GitHubException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"GitHub dashboard cache could not be read: {exception.GetType().Name}");
        }
        if (!_refreshStarted && cached.Login.Length != 0)
        {
            _lastGood = cached;
            try
            {
                Apply(cached);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"GitHub dashboard cached data could not be shown: {exception.GetType().Name}");
                _lastGood = GitHubSnapshot.Empty;
                try
                {
                    Apply(GitHubSnapshot.Empty);
                }
                catch (Exception)
                {
                }
            }
        }
        UpdateBusy();
        _ = ConfirmIdentityAsync(_lifetime.Token);
    }

    private async Task ConfirmIdentityAsync(CancellationToken cancellationToken)
    {
        GitHubSnapshot identity;
        try
        {
            identity = GitHubData.ParseIdentity(await GitHubCli.ReadApiAsync("user", null, cancellationToken));
        }
        catch (Exception exception) when (exception is GitHubException or OperationCanceledException)
        {
            Debug.WriteLine($"GitHub dashboard account could not be confirmed: {exception.GetType().Name}");
            return;
        }
        if (_refreshStarted) return;
        _lastGood = identity.Id == _lastGood.Id && identity.Login == _lastGood.Login
            ? _lastGood with { Name = identity.Name, AvatarUrl = identity.AvatarUrl }
            : identity;
        Apply(_lastGood);
    }

    private async void Refresh(GitHubRefreshReason reason)
    {
        if (_refreshing || _lifetime.IsCancellationRequested) return;
        _refreshing = true;
        _refreshStarted = true;
        var generation = ++_generation;
        var cancellationToken = _lifetime.Token;
        UpdateBusy();
        RenderStatus();
        RenderSectionStatus();
        if (reason == GitHubRefreshReason.Manual) Announce(GitHubStrings.Loading);
        try
        {
            if (_initialization is { } initialization) await initialization;
            var result = await GitHubData.RefreshAsync(GitHubCli.ReadApiAsync, _lastGood, reason, partial =>
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (generation != _generation) return;
                    _takeover = TakeoverState.None;
                    // An exception escaping a dispatcher callback ends the process; the final Apply reports it instead.
                    try { Apply(partial); }
                    catch (Exception error) { Debug.WriteLine($"GitHub partial update failed: {error.Message}"); }
                }), cancellationToken: cancellationToken);
            _generation++;
            _lastGood = result;
            _verified = true;
            _failure = GitHubFailure.None;
            _takeover = TakeoverState.None;
            _lastSuccess = Stopwatch.GetTimestamp();
            Apply(result);
            if (reason == GitHubRefreshReason.Manual) Announce(GitHubStrings.Loaded);
            _ = SaveAsync(result);
        }
        catch (OperationCanceledException)
        {
            _generation++;
            if (!_lifetime.IsCancellationRequested) Apply(_lastGood);
        }
        catch (Exception refreshError)
        {
            _generation++;
            var failure = refreshError is GitHubException error ? error.Failure : GitHubFailure.InvalidResponse;
            var takeover = failure == GitHubFailure.MissingCli ? TakeoverState.MissingCli : TakeoverState.None;
            if (failure == GitHubFailure.Request)
            {
                try
                {
                    if (await GitHubCli.ProbeAuthAsync(cancellationToken) == GitHubAuthState.SignedOut) takeover = TakeoverState.SignedOut;
                }
                catch (Exception exception) when (exception is GitHubException or OperationCanceledException)
                {
                    Debug.WriteLine($"GitHub dashboard sign-in probe failed: {exception.GetType().Name}");
                }
            }
            if (takeover != TakeoverState.None || failure == GitHubFailure.AccountChanged)
            {
                _lastGood = GitHubSnapshot.Empty;
                _verified = false;
                _ = DeleteCacheAsync();
            }
            _takeover = takeover;
            _failure = takeover == TakeoverState.None ? failure : GitHubFailure.None;
            Apply(_lastGood);
            Announce(takeover == TakeoverState.SignedOut ? GitHubStrings.SignedOutTitle : GitHubPresentation.ErrorText(failure));
        }
        finally
        {
            _refreshing = false;
            UpdateBusy();
            RenderStatus();
            RenderSectionStatus();
            if (List(_snapshot, _section).Value is null) RenderList(0, false);
        }
    }

    // Writes and deletes run one after another, so a delete can never be overtaken by an earlier refresh's write.
    private Task SaveAsync(GitHubSnapshot snapshot) => _cacheWork = ChainCacheAsync(_cacheWork, snapshot);

    private Task DeleteCacheAsync() => _cacheWork = ChainCacheAsync(_cacheWork, null);

    private async Task ChainCacheAsync(Task previous, GitHubSnapshot? snapshot)
    {
        try
        {
            await previous;
        }
        catch (Exception)
        {
        }
        if (snapshot is null)
        {
            await Task.Run(() => GitHubCache.Delete(_cachePath));
            return;
        }
        try
        {
            await Task.Run(() => GitHubCache.Write(_cachePath, snapshot));
            _cacheWarning = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or GitHubException)
        {
            Debug.WriteLine($"GitHub dashboard cache could not be written: {exception.GetType().Name}");
            _cacheWarning = true;
        }
        RenderStatus();
    }

    private void Apply(GitHubSnapshot next)
    {
        next = GitHubData.ExpireSections(next, DateTimeOffset.UtcNow);
        var previous = _snapshot;
        _snapshot = next;
        var account = previous.Id != next.Id || previous.Login != next.Login;
        if (account || previous.Name != next.Name || previous.AvatarUrl != next.AvatarUrl)
        {
            RenderHeader();
            IdentityChanged?.Invoke(next.Login, next.AvatarUrl);
        }
        if (account || Changed(previous.Calendar, next.Calendar)) RenderCalendar();
        if (account || Changed(previous.Usage, next.Usage)) RenderUsage();
        RenderCounts();
        if (account || ListChanged(List(previous, _section), List(next, _section))) RenderList(0, true);
        RenderSectionStatus();
        RenderStatus();
        RenderTakeover();
        UpdateBusy();
    }

    private static bool ListChanged(GitHubSection<IReadOnlyList<GitHubItem>> before, GitHubSection<IReadOnlyList<GitHubItem>> after) =>
        before.Failure != after.Failure || (before.Value is null) != (after.Value is null) ||
        (before.Value is { } old && after.Value is { } current && !GitHubPresentation.SameItems(old, current));

    private static bool Changed<T>(GitHubSection<T> before, GitHubSection<T> after) where T : class =>
        before.Updated != after.Updated || before.Failure != after.Failure || (before.Value is null) != (after.Value is null);

    private static GitHubSection<IReadOnlyList<GitHubItem>> List(GitHubSnapshot snapshot, int section) => section switch
    {
        0 => snapshot.Activity,
        1 => snapshot.Pulls,
        2 => snapshot.Reviews,
        _ => snapshot.Repos
    };

    private void RenderAll()
    {
        RenderHeader();
        RenderCalendar();
        RenderUsage();
        RenderCounts();
        RenderList(0, false);
        RenderSectionStatus();
        RenderStatus();
        RenderTakeover();
    }

    private void RenderHeader()
    {
        var (login, name) = (_snapshot.Login, _snapshot.Name);
        AccountText.Text = login.Length == 0 ? GitHubStrings.Dashboard : name.Length == 0 ? "@" + login : name;
        _avatar.Set(login, _snapshot.AvatarUrl);
        var calendar = _snapshot.Calendar;
        SummaryText.Text = (login.Length != 0 && name.Length != 0 ? "@" + login + " \u00B7 " : "") + (calendar.Value is { } value
            ? GitHubPresentation.Summary(value.Total, CultureInfo.CurrentCulture) +
              (calendar.Failure == GitHubFailure.None ? "" : " \u00B7 " + GitHubStrings.Stale)
            : GitHubStrings.NotLoaded);
    }

    private void RenderCalendar()
    {
        var calendar = _snapshot.Calendar.Value;
        Heatmap.SetDays(calendar?.Days);
        RenderHeader();
        if (calendar is not null && _open && !_revealed)
        {
            _revealed = true;
            Heatmap.Reveal(TimeSpan.FromMilliseconds(40));
        }
    }

    private void RenderReadout()
    {
        var text = Heatmap.SelectedDay is { } day ? GitHubPresentation.DayReadout(day, Heatmap.LatestYear, CultureInfo.CurrentCulture)
            : _snapshot.Calendar.Failure != GitHubFailure.None ? GitHubPresentation.ErrorText(_snapshot.Calendar.Failure)
            : "";
        CrossFade(ReadoutA, ReadoutB, ref _readoutFlip, text);
        var name = (text.Length == 0 ? GitHubStrings.CalendarName : text) + ". " + GitHubStrings.CalendarKeys;
        var old = AutomationProperties.GetName(Heatmap);
        AutomationProperties.SetName(Heatmap, name);
        if (FrameworkElementAutomationPeer.FromElement(Heatmap) is { } peer)
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, old, name);
    }

    private void RenderLegend()
    {
        Microsoft.UI.Xaml.Shapes.Rectangle[] cells = [Legend0, Legend1, Legend2, Legend3, Legend4];
        for (var level = 0; level < cells.Length; level++) cells[level].Fill = new SolidColorBrush(Heatmap.Palette[level]);
    }

    private void RenderUsage()
    {
        var section = _snapshot.Usage;
        UsageRows.Children.Clear();
        PlanText.Text = section.Value is { } usage ? GitHubPresentation.PlanText(usage.Plan) : "";
        if (section.Value is { } value)
        {
            foreach (var quota in GitHubPresentation.VisibleQuotas(value)) UsageRows.Children.Add(QuotaRow(quota));
        }
        if (section.Failure != GitHubFailure.None || (section.Value is null && !IsLoading))
        {
            UsageRows.Children.Add(new TextBlock
            {
                Text = section.Failure == GitHubFailure.None ? GitHubStrings.UsageNotLoaded
                    : GitHubPresentation.SectionStatus(section, DateTimeOffset.UtcNow, TimeZoneInfo.Local, CultureInfo.CurrentCulture),
                Style = (Style)Resources["GitHubTertiaryText"],
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 3
            });
        }
        Reveal(UsageSkeleton, UsageRows, section.Value is null && section.Failure == GitHubFailure.None && IsLoading);
        Reveal(PlanSkeleton, PlanText, section.Value is null && section.Failure == GitHubFailure.None && IsLoading);
    }

    private FrameworkElement QuotaRow(GitHubQuota quota)
    {
        var row = new StackPanel { Spacing = 6 };
        var line = new Grid { ColumnSpacing = 8, Height = 16 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = GitHubPresentation.QuotaName(quota);
        line.Children.Add(new TextBlock { Text = name, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center });
        var reset = GitHubPresentation.ResetText(quota.Reset, TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        var value = new TextBlock
        {
            Text = GitHubPresentation.QuotaValue(quota, CultureInfo.CurrentCulture) + (reset.Length == 0 ? "" : " \u00B7 " + reset),
            Style = (Style)Resources["GitHubSecondaryText"],
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(value, 1);
        line.Children.Add(value);
        row.Children.Add(line);
        if (quota.Used is { } used)
        {
            var track = new Grid
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
            };
            row.Children.Add(track);
            AutomationProperties.SetName(row, name + ", " + value.Text);
            var fill = (SolidColorBrush)Application.Current.Resources[used >= 95 ? "SystemFillColorCriticalBrush"
                : used >= 80 ? "SystemFillColorCautionBrush" : "AccentFillColorDefaultBrush"];
            var fraction = Math.Clamp(used / 100, 0, 1);
            GitHubMotion.Bar(track, fill.Color, _bars.GetValueOrDefault(quota.Kind), fraction);
            _bars[quota.Kind] = fraction;
        }
        return row;
    }

    private void RenderCounts()
    {
        for (var index = 0; index < _tabs.Length; index++)
        {
            var items = List(_snapshot, index).Value;
            _tabCounts[index].Text = items is null ? "" : items.Count.ToString(CultureInfo.CurrentCulture);
            AutomationProperties.SetName(_tabs[index], items is null ? SectionNames[index]
                : $"{SectionNames[index]}, {GitHubPresentation.Items(items.Count)}");
        }
        FitTabs();
    }

    private void RenderList(int direction, bool crossFade)
    {
        var section = List(_snapshot, _section);
        var wasLoading = ListSkeleton.Visibility == Visibility.Visible;
        var hadFocus = XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused && Contains(ListPanel, focused);
        _times.Clear();
        ListPanel.Children.Clear();
        if (hadFocus) _tabs[_section].Focus(FocusState.Programmatic);
        if (section.Value is { } items)
        {
            if (items.Count == 0) ListPanel.Children.Add(EmptyState(EmptyGlyphs[_section], EmptyTitles[_section], GitHubStrings.Empty));
            foreach (var item in items) ListPanel.Children.Add(Card(item));
            if (items.Count > 0)
                ListPanel.Children.Add(new TextBlock
                {
                    Text = GitHubStrings.AccountScope,
                    Style = (Style)Resources["GitHubTertiaryText"],
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8, 10, 8, 4)
                });
        }
        else if (!IsLoading || section.Failure != GitHubFailure.None)
        {
            ListPanel.Children.Add(EmptyState("\uE783", GitHubStrings.ListNotLoaded, GitHubStrings.ListNotLoadedDetail));
        }
        var skeleton = section.Value is null && section.Failure == GitHubFailure.None && IsLoading;
        if (direction != 0)
        {
            if (ListScroll.VerticalOffset > 0) ListScroll.ChangeView(null, 0, null, true);
            ListSkeleton.Visibility = skeleton ? Visibility.Visible : Visibility.Collapsed;
            GitHubMotion.Show(ListSkeleton, skeleton ? 1 : 0);
            UpdateShimmer();
            GitHubMotion.Enter(ListPanel, TimeSpan.Zero, rise: 0, slide: 20 * direction);
            if (skeleton) GitHubMotion.Enter(ListSkeleton, TimeSpan.Zero, rise: 0, slide: 20 * direction);
            return;
        }
        Reveal(ListSkeleton, ListPanel, skeleton);
        if (crossFade && wasLoading && !skeleton) GitHubMotion.Enter(ListPanel, TimeSpan.Zero, rise: 4);
    }

    private void RenderSectionStatus()
    {
        var section = List(_snapshot, _section);
        var text = !_verified && section.Value is not null && section.Failure == GitHubFailure.None && section.Updated < _startedAt
            ? GitHubStrings.Saved + " " + GitHubPresentation.Relative(section.Updated, DateTimeOffset.UtcNow, TimeZoneInfo.Local, CultureInfo.CurrentCulture) +
              (_refreshing ? " \u00B7 " + GitHubStrings.Verifying : "")
            : GitHubPresentation.SectionStatus(section, DateTimeOffset.UtcNow, TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        CrossFade(SectionStatusA, SectionStatusB, ref _sectionFlip, text);
    }

    private void RenderTimes()
    {
        foreach (var (text, at) in _times)
            text.Text = GitHubPresentation.Relative(at, DateTimeOffset.UtcNow, TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        RenderSectionStatus();
    }

    private void RenderStatus()
    {
        (string Text, string Style, string Glyph, bool Retry) state =
            _takeover != TakeoverState.None ? ("", "", "", false)
            : _failure != GitHubFailure.None ? ((_snapshot.Login.Length != 0 ? GitHubStrings.SavedUnverified + " " : "") +
                GitHubPresentation.ErrorText(_failure), "Error", "\uEA39", true)
            : !_verified && !_refreshing && _snapshot.Login.Length != 0 ? (GitHubStrings.SavedUnverified, "Info", "\uE946", false)
            : _cacheWarning ? (GitHubStrings.CacheWarning, "Warning", "\uE7BA", false)
            : ("", "", "", false);
        var (text, style, glyph, retry) = state;
        if (text == _status) return;
        var wasVisible = _status.Length != 0;
        _status = text;
        if (text.Length == 0)
        {
            GitHubMotion.FadeTo(StatusBar, 0, GitHubMotion.Short, () =>
            {
                if (_status.Length == 0) StatusBar.Visibility = Visibility.Collapsed;
            });
            return;
        }
        StatusBar.Style = (Style)Resources["GitHubStatus" + style];
        StatusIcon.Style = (Style)Resources["GitHubStatusIcon" + style];
        StatusIcon.Glyph = glyph;
        StatusText.Text = text;
        StatusRetry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Visible;
        if (!wasVisible) GitHubMotion.Show(StatusBar, 0);
        GitHubMotion.FadeTo(StatusBar, 1, GitHubMotion.Standard);
        if (wasVisible)
        {
            GitHubMotion.Show(StatusText, 0.3f);
            GitHubMotion.FadeTo(StatusText, 1, GitHubMotion.Standard);
        }
    }

    private void RenderTakeover()
    {
        var on = _takeover != TakeoverState.None;
        var changed = (Takeover.Visibility == Visibility.Visible) != on;
        Takeover.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Overflow.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        AccountBlock.Visibility = Overflow.Visibility;
        if (on)
        {
            var signedOut = _takeover == TakeoverState.SignedOut;
            TakeoverIcon.Glyph = signedOut ? "\uE77B" : "\uE7BA";
            TakeoverTitle.Text = signedOut ? GitHubStrings.SignedOutTitle : GitHubStrings.MissingCliTitle;
            TakeoverBody.Text = signedOut ? GitHubStrings.SignedOutBody : GitHubStrings.MissingCliBody;
            CommandText.Text = signedOut ? GitHubStrings.SignInCommand : GitHubStrings.InstallCommand;
            AutomationProperties.SetName(CommandText, CommandText.Text);
            AutomationProperties.SetLiveSetting(TakeoverTitle, AutomationLiveSetting.Polite);
        }
        if (changed)
        {
            UpdateHeight();
            if (!_open) return;
            GitHubMotion.Enter(on ? Takeover : Overflow, TimeSpan.Zero, rise: 6);
            if (on) TakeoverRetry.Focus(FocusState.Programmatic);
            else _tabs[_section].Focus(FocusState.Programmatic);
        }
    }

    private void UpdateBusy()
    {
        if (_refreshing) _spinner.Start();
        else _spinner.Stop();
        GitHubMotion.Show(RefreshIcon, _refreshing && !GitHubMotion.Enabled ? 0.45f : 1);
        AutomationProperties.SetName(RefreshButton, _refreshing ? GitHubStrings.Refreshing : GitHubStrings.Refresh);
        Heatmap.SetLoading(_open && IsLoading && _snapshot.Calendar.Value is null && _snapshot.Calendar.Failure == GitHubFailure.None);
        if (_snapshot.Usage.Value is null) RenderUsage();
        TakeoverRetry.Content = _refreshing ? GitHubStrings.Checking : GitHubStrings.Retry;
        StatusRetry.Content = TakeoverRetry.Content;
        var section = List(_snapshot, _section);
        Reveal(ListSkeleton, ListPanel, section.Value is null && section.Failure == GitHubFailure.None && IsLoading);
        UpdateShimmer();
    }

    private void UpdateShimmer()
    {
        var visible = UsageSkeleton.Visibility == Visibility.Visible || ListSkeleton.Visibility == Visibility.Visible;
        if (_open && visible) _shimmer.Start();
        else _shimmer.Stop();
    }

    private void Reveal(FrameworkElement skeleton, FrameworkElement content, bool loading)
    {
        var showing = skeleton.Visibility == Visibility.Visible;
        if (loading)
        {
            if (!showing)
            {
                skeleton.Visibility = Visibility.Visible;
                GitHubMotion.Show(skeleton);
                GitHubMotion.Show(content, 0);
            }
            return;
        }
        if (!showing)
        {
            GitHubMotion.Show(content);
            return;
        }
        GitHubMotion.FadeTo(skeleton, 0, GitHubMotion.Standard, () =>
        {
            if (Visual(skeleton).Opacity == 0) skeleton.Visibility = Visibility.Collapsed;
            UpdateShimmer();
        });
        if (_open) GitHubMotion.FadeTo(content, 1, GitHubMotion.Standard);
        else
        {
            skeleton.Visibility = Visibility.Collapsed;
            GitHubMotion.Show(content);
        }
    }

    private static Microsoft.UI.Composition.Visual Visual(UIElement element) => GitHubMotion.Of(element);

    private static bool Contains(DependencyObject ancestor, DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static void CrossFade(TextBlock first, TextBlock second, ref bool flip, string text)
    {
        var (incoming, outgoing) = flip ? (first, second) : (second, first);
        if (outgoing.Text == text) return;
        incoming.Text = text;
        flip = !flip;
        AutomationProperties.SetAccessibilityView(incoming, AccessibilityView.Content);
        AutomationProperties.SetAccessibilityView(outgoing, AccessibilityView.Raw);
        GitHubMotion.FadeTo(incoming, 1, GitHubMotion.Short);
        GitHubMotion.FadeTo(outgoing, 0, GitHubMotion.Short);
    }

    private void PlayEntrance()
    {
        FrameworkElement[] parts = _takeover != TakeoverState.None
            ? [Takeover]
            : [StatusBar, CalendarBlock, UsageBlock, TabsRow, SectionStatusHost, ListArea];
        if (AccountBlock.Visibility == Visibility.Visible) GitHubMotion.Enter(AccountBlock, TimeSpan.Zero, rise: 0, slide: 12);
        var delay = 25;
        foreach (var part in parts)
        {
            if (part.Visibility != Visibility.Visible) continue;
            GitHubMotion.Enter(part, TimeSpan.FromMilliseconds(delay), rise: 6);
            delay += 25;
        }
        if (_snapshot.Calendar.Value is not null && _takeover == TakeoverState.None)
        {
            _revealed = true;
            Heatmap.Reveal(TimeSpan.FromMilliseconds(50));
        }
    }

    private void UpdateHeight()
    {
        if (_takeover != TakeoverState.None)
        {
            Height = double.NaN;
            MaxHeight = _maxHeight;
            return;
        }
        MaxHeight = double.PositiveInfinity;
        Height = _maxHeight;
        var width = double.IsNaN(Width) ? 418 : Width;
        if (Fixed.ActualHeight <= 0) Fixed.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
        var fixedHeight = Fixed.ActualHeight > 0 ? Fixed.ActualHeight : Fixed.DesiredSize.Height;
        Header.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
        var body = Math.Max(0, _maxHeight - Header.DesiredSize.Height);
        var compact = body < fixedHeight + MinimumListHeight;
        Root.Height = compact ? double.NaN : body;
        if (compact == _compact) return;
        _compact = compact;
        Overflow.VerticalScrollMode = compact ? ScrollMode.Enabled : ScrollMode.Disabled;
        Overflow.VerticalScrollBarVisibility = compact ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        if (!compact) Overflow.ChangeView(null, 0, null, true);
    }

    private void Fixed_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) >= 0.5) UpdateHeight();
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        var index = Array.IndexOf(_tabs, sender);
        UpdateTabStops();
        if (index < 0 || index == _section) return;
        var direction = Math.Sign(index - _section);
        _section = index;
        MovePill(true);
        RenderList(direction, false);
        RenderSectionStatus();
    }

    private void TabStrip_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var next = e.Key switch
        {
            VirtualKey.Left => (_section + _tabs.Length - 1) % _tabs.Length,
            VirtualKey.Right => (_section + 1) % _tabs.Length,
            VirtualKey.Home => 0,
            VirtualKey.End => _tabs.Length - 1,
            _ => -1
        };
        if (next < 0) return;
        e.Handled = true;
        _tabs[next].IsChecked = true;
        _tabs[next].Focus(FocusState.Keyboard);
    }

    private void UpdateTabStops()
    {
        foreach (var tab in _tabs) tab.IsTabStop = tab.IsChecked == true;
    }

    private void TabStrip_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        FitTabs();
        MovePill(false);
    }

    private void FitTabs()
    {
        var column = (TabStrip.ActualWidth - TabStrip.BorderThickness.Left - TabStrip.BorderThickness.Right) / _tabs.Length - 10;
        if (column <= 0) return;
        double Width(TextBlock text)
        {
            text.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            return text.DesiredSize.Width;
        }
        foreach (var label in _tabLabels) label.FontSize = 14;
        var counts = _tabCounts.Any(count => count.Text.Length != 0) &&
            _tabLabels.Zip(_tabCounts).All(pair => Width(pair.First) + 4 + Width(pair.Second) <= column);
        foreach (var count in _tabCounts) count.Visibility = counts ? Visibility.Visible : Visibility.Collapsed;
        if (_tabLabels.Any(label => Width(label) > column))
            foreach (var label in _tabLabels) label.FontSize = 12;
    }

    private void MovePill(bool animate)
    {
        var column = (TabStrip.ActualWidth - TabStrip.BorderThickness.Left - TabStrip.BorderThickness.Right) / _tabs.Length;
        if (column <= 0) return;
        TabPill.Width = Math.Max(0, column - TabPill.Margin.Left - TabPill.Margin.Right);
        var to = new Vector3((float)(column * _section), 0, 0);
        if (animate) GitHubMotion.Slide(TabPill, to, GitHubMotion.Standard);
        else GitHubMotion.Place(TabPill, to);
    }

    private void View_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.F5) return;
        e.Handled = true;
        Refresh(GitHubRefreshReason.Manual);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh(GitHubRefreshReason.Manual);

    private void HeaderAvatar_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void Retry_Click(object sender, RoutedEventArgs e) => Refresh(GitHubRefreshReason.Manual);

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(CommandText.Text);
        try
        {
            Clipboard.SetContent(package);
        }
        catch (Exception exception) when (exception is COMException)
        {
            Debug.WriteLine($"GitHub dashboard could not copy the command: {exception.GetType().Name}");
            return;
        }
        GitHubMotion.FadeTo(CopyIcon, 0, GitHubMotion.Short);
        GitHubMotion.FadeTo(CopiedIcon, 1, GitHubMotion.Short);
        Announce(GitHubStrings.Copied);
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1.6);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            GitHubMotion.FadeTo(CopyIcon, 1, GitHubMotion.Standard);
            GitHubMotion.FadeTo(CopiedIcon, 0, GitHubMotion.Standard);
        };
        timer.Start();
    }

    private static void OpenUrl(string url)
    {
        if (!GitHubData.IsGitHubUrl(url)) return;
        try
        {
            _ = Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            Debug.WriteLine($"GitHub dashboard link could not be opened: {exception.GetType().Name}");
        }
    }

    private void Announce(string text)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(this) ?? FrameworkElementAutomationPeer.CreatePeerForElement(this);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent,
            text, "GitHubDashboard");
    }

    private void ApplyShimmerColor()
    {
        var key = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? "HighContrast"
            : ActualTheme == ElementTheme.Light ? "Light" : "Default";
        if (Resources.ThemeDictionaries.TryGetValue(key, out var dictionary) &&
            ((ResourceDictionary)dictionary).TryGetValue("GitHubShimmerColor", out var color))
            _shimmer.SetColor((Color)color);
    }

    private void BuildListSkeleton()
    {
        double[] titles = [236, 188, 262, 204, 172];
        foreach (var width in titles)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new Border { Style = (Style)Resources["GitHubSkeleton"], Width = 24, Height = 24, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Top });
            var lines = new StackPanel { Spacing = 9, Margin = new Thickness(0, 3, 0, 0) };
            lines.Children.Add(new Border { Style = (Style)Resources["GitHubSkeleton"], Width = width, Height = 12 });
            lines.Children.Add(new Border { Style = (Style)Resources["GitHubSkeleton"], Width = width * 0.62, Height = 10 });
            Grid.SetColumn(lines, 1);
            row.Children.Add(lines);
            ListSkeleton.Children.Add(row);
        }
    }

    private static IEnumerable<Border> Blocks(Panel panel) =>
        panel.Children.OfType<Grid>().SelectMany(row => row.Children.OfType<Border>()
            .Concat(row.Children.OfType<StackPanel>().SelectMany(lines => lines.Children.OfType<Border>())));
}
