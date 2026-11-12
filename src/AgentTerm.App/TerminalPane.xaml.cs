using System;
using AgentTerm.Terminal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using AgentTerm_App.PaneStatus;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AgentTerm_App;

internal sealed partial class TerminalPane : UserControl, IDisposable
{
    private readonly MainPage _page;
    private readonly PaneAcrylicBackdrop _backdrop = new();
    internal event Action<bool>? SplitRequested;
    internal event Action? CloseRequested;

    internal TerminalPane(MainPage page)
    {
        _page = page;
        InitializeComponent();
        _backdrop.AttachmentChanged += UpdateSolidFallback;
        TerminalContent.Content = page;
        PaneShell.Text = page.Profile.Name;
        ToolTipService.SetToolTip(PaneShell, page.Profile.Name);
        _page.PaneStatus.Changed += UpdateHeader;
        UpdateHeader();
        ActualThemeChanged += Pane_ActualThemeChanged;
        TerminalPreferences.Current.Changed += ApplyMaterial;
        _page.TerminalBackgroundChanged += ApplyMaterial;
        PaneBackdrop.SystemBackdrop = _backdrop;
        ApplyMaterial();
    }

    internal void SetFocused(bool focused, bool multiplePanes)
    {
        ActiveBorder.Visibility = focused && multiplePanes ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Pane_Loaded(object sender, RoutedEventArgs e) => ApplyMaterial();

    private void Pane_ActualThemeChanged(FrameworkElement sender, object args) => ApplyMaterial();

    public void Dispose()
    {
        TerminalPreferences.Current.Changed -= ApplyMaterial;
        _page.TerminalBackgroundChanged -= ApplyMaterial;
        _page.PaneStatus.Changed -= UpdateHeader;
        ActualThemeChanged -= Pane_ActualThemeChanged;
        Loaded -= Pane_Loaded;
        _backdrop.Dispose();
        _backdrop.AttachmentChanged -= UpdateSolidFallback;
    }

    private void UpdateHeader()
    {
        var status = _page.PaneStatus;
        PaneDirectory.Text = status.Directory?.DisplayPath ?? "";
        ToolTipService.SetToolTip(PaneDirectory, status.Directory?.Path);
        var git = status.Result.Snapshot;
        GitBranch.Text = git?.BranchLabel ?? "";
        GitTracking.Text = git is null ? "" : string.Join(" ", new[]
        {
            git.Ahead > 0 ? $"↑{git.Ahead}" : null,
            git.Behind > 0 ? $"↓{git.Behind}" : null,
        }.Where(value => value is not null));
        GitAdded.Text = git?.Lines is { Added: > 0 } added ? $"+{added.Added}" : "";
        GitDeleted.Text = git?.Lines is { Deleted: > 0 } deleted ? $"−{deleted.Deleted}" : "";
        GitChanges.Text = git is null ? "" : string.Join(" ", new[]
        {
            git.Staged ? "●" : null,
            git.Unstaged ? "±" : null,
            git.Untracked ? "?" : null,
            git.Conflicts ? "!" : null,
        }.Where(value => value is not null));
        foreach (var text in new[] { GitTracking, GitAdded, GitDeleted, GitChanges })
            text.Visibility = text.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var summary = git?.Summary ?? status.Result.State switch
        {
            GitQueryState.Unavailable => "Git executable unavailable",
            GitQueryState.Failed => "Git status unavailable",
            GitQueryState.TimedOut => "Git status query timed out",
            _ => "",
        };
        var description = string.Join(". ", new[] { _page.Profile.Name, status.Directory?.Path, summary }.Where(value => !string.IsNullOrEmpty(value)));
        ToolTipService.SetToolTip(HeaderContext, description);
        AutomationProperties.SetName(HeaderContext, description);
        ToolTipService.SetToolTip(GitSummary, summary);
        AutomationProperties.SetName(GitSummary, summary);
        UpdateHeaderLayout();
    }

    private void HeaderContext_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateHeaderLayout();

    private void UpdateHeaderLayout()
    {
        GitSummary.Visibility = _page.PaneStatus.Result.Snapshot is not null && HeaderContext.ActualWidth >= 300
            ? Visibility.Visible : Visibility.Collapsed;
        var wide = HeaderContext.ActualWidth >= 530;
        GitDetails.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        GitBranch.MaxWidth = wide ? 260 : 130;
        GitSummary.MaxWidth = wide ? 460 : 160;
    }

    private void ApplyMaterial()
    {
        var appearance = TerminalPreferences.Current.ResolveAppearance(_page.Profile.Id);
        // Theme the pane controls too, but leave window chrome on the Windows appearance.
        // Automatic must inherit again rather than resolve against a previous fixed theme.
        RequestedTheme = appearance.ThemeId.Length == 0 ? ElementTheme.Default
            : TerminalThemes.Resolve(appearance.ThemeId, false).IsLight ? ElementTheme.Light : ElementTheme.Dark;
        var background = _page.TerminalBackground;
        var color = Color.FromArgb(255, background.R, background.G, background.B);
        SolidSurface.Background = new SolidColorBrush(color);
        _backdrop.UpdateMaterial(color, appearance.UseAcrylic ? appearance.BackgroundOpacity : 1);
        UpdateSolidFallback();
    }

    private void UpdateSolidFallback() => SolidSurface.Visibility =
        _backdrop.IsAttached ? Visibility.Collapsed : Visibility.Visible;

    private void SplitRight_Click(object sender, RoutedEventArgs e) => SplitRequested?.Invoke(true);
    private void SplitDown_Click(object sender, RoutedEventArgs e) => SplitRequested?.Invoke(false);
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
