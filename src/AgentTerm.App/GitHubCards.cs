using System;
using System.Globalization;
using AgentTerm_App.GitHub;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace AgentTerm_App;

internal sealed partial class GitHubDashboardView
{
    private FrameworkElement Card(GitHubItem item)
    {
        var hideAuthor = GitHubPresentation.HidesAuthor(item, _section == 1, _snapshot.Login);
        var card = new Grid
        {
            Padding = new Thickness(8, 8, 8, 9),
            ColumnSpacing = hideAuthor ? 0 : 10,
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
            BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(150) }
        };
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(hideAuthor ? 0 : 24) });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.PointerEntered += (_, _) => card.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        card.PointerExited += (_, _) => card.Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"];
        card.PointerCanceled += (_, _) => card.Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"];
        card.Tapped += (_, args) =>
        {
            for (var source = args.OriginalSource as DependencyObject; source is not null && !ReferenceEquals(source, card);
                 source = VisualTreeHelper.GetParent(source))
                if (source is ButtonBase) return;
            OpenUrl(item.Url);
        };

        if (hideAuthor) item = item with { Author = "", AvatarUrl = "" };
        else
        {
            FrameworkElement leading;
            if (item.Author.Length != 0)
            {
                var avatar = new GitHubAvatar(24, 48);
                avatar.Set(item.Author, item.AvatarUrl);
                leading = avatar;
            }
            else
            {
                leading = new Border
                {
                    Width = 24,
                    Height = 24,
                    CornerRadius = new CornerRadius(12),
                    Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                    Child = new FontIcon
                    {
                        Glyph = Glyph(item),
                        FontSize = 12,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                    }
                };
            }
            leading.VerticalAlignment = VerticalAlignment.Top;
            leading.Margin = new Thickness(0, 1, 0, 0);
            AutomationProperties.SetAccessibilityView(leading, AccessibilityView.Raw);
            card.Children.Add(leading);
        }

        var body = new StackPanel { Spacing = 3 };
        Grid.SetColumn(body, 1);
        card.Children.Add(body);

        var top = new Grid { ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new HyperlinkButton
        {
            Style = (Style)Resources["GitHubTitleLink"],
            Content = new TextBlock
            {
                Text = item.Title,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2
            }
        };
        AutomationProperties.SetName(title, item.Title);
        ToolTipService.SetToolTip(title, item.Title);
        title.Click += (_, _) => OpenUrl(item.Url);
        top.Children.Add(title);
        if (GitHubPresentation.TryParseTime(item.UpdatedAt, out var updated))
        {
            var time = new TextBlock
            {
                Style = (Style)Resources["GitHubTertiaryText"],
                Margin = new Thickness(0, 2, 0, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            ToolTipService.SetToolTip(time, GitHubPresentation.Absolute(updated, TimeZoneInfo.Local, CultureInfo.CurrentCulture));
            Grid.SetColumn(time, 1);
            top.Children.Add(time);
            _times.Add((time, updated));
            time.Text = GitHubPresentation.Relative(updated, DateTimeOffset.UtcNow, TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        }
        body.Children.Add(top);

        var meta = GitHubPresentation.MetaLine(item);
        if (item.State.Length != 0 || meta.Length != 0)
        {
            var line = new Grid { ColumnSpacing = 6 };
            var text = new TextBlock { Text = meta, Style = (Style)Resources["GitHubSecondaryText"], VerticalAlignment = VerticalAlignment.Center };
            if (item.State.Length != 0)
            {
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.Children.Add(Pill(item.State));
                Grid.SetColumn(text, 1);
            }
            line.Children.Add(text);
            body.Children.Add(line);
        }
        var detail = GitHubPresentation.DetailLine(item);
        if (detail.Length != 0)
            body.Children.Add(new TextBlock { Text = detail, Style = (Style)Resources["GitHubSecondaryText"], TextWrapping = TextWrapping.Wrap, MaxLines = 2 });
        if (item.Head.Length != 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = GitHubPresentation.BranchLine(item),
                Style = (Style)Resources["GitHubTertiaryText"],
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 11
            });
            var labels = GitHubPresentation.LabelsLine(item);
            if (labels.Length != 0) body.Children.Add(new TextBlock { Text = labels, Style = (Style)Resources["GitHubTertiaryText"] });
            var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 3, 0, 0) };
            footer.Children.Add(ChecksPill(item));
            if (item.Review.Length != 0) footer.Children.Add(Pill(item.Review));
            footer.Children.Add(new TextBlock
            {
                Text = GitHubPresentation.Comments(item.Comments),
                Style = (Style)Resources["GitHubSecondaryText"],
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0)
            });
            body.Children.Add(footer);
        }
        return card;
    }

    private static string Glyph(GitHubItem item) => item.ActivityKind switch
    {
        "" => "\uE8B7",
        "PushEvent" => "\uE898",
        "CreateEvent" or "DeleteEvent" => "\uE8EC",
        "IssueCommentEvent" or "PullRequestReviewCommentEvent" => "\uE90A",
        "ReleaseEvent" => "\uE7C1",
        _ => "\uE81C"
    };

    private FrameworkElement Pill(string state)
    {
        var tone = GitHubPresentation.StateTone(state).ToString();
        var text = GitHubPresentation.StateLabel(state);
        var pill = new Border
        {
            Style = (Style)Resources["GitHubPill" + tone],
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 0, 8, 1),
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                Style = (Style)Resources["GitHubPillText" + tone],
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        AutomationProperties.SetName(pill, text);
        return pill;
    }

    private Button ChecksPill(GitHubItem item)
    {
        var summary = GitHubPresentation.Checks(item);
        var (glyph, brush) = Outcome(summary.Outcome);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 10, Foreground = (Brush)Application.Current.Resources[brush] });
        content.Children.Add(new TextBlock { Text = $"{summary.Passed}/{summary.Total}", VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Style = (Style)Resources["GitHubPillButton"], Content = content };
        var label = $"{GitHubStrings.Checks} {summary.Passed}/{summary.Total}";
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        flyout.Opening += (_, _) => flyout.Content = ChecksList(item, summary);
        button.Flyout = flyout;
        return button;
    }

    private static (string Glyph, string Brush) Outcome(GitHubCheckOutcome outcome) => outcome switch
    {
        GitHubCheckOutcome.Passed => ("\uE73E", "SystemFillColorSuccessBrush"),
        GitHubCheckOutcome.Failed => ("\uE711", "SystemFillColorCriticalBrush"),
        GitHubCheckOutcome.Pending => ("\uEA3B", "SystemFillColorCautionBrush"),
        _ => ("\uE738", "TextFillColorTertiaryBrush")
    };

    private FrameworkElement ChecksList(GitHubItem item, GitHubCheckSummary summary)
    {
        var panel = new StackPanel { Spacing = 8, Width = 300 };
        panel.Children.Add(new TextBlock { Text = $"{GitHubStrings.Checks} {summary.Passed}/{summary.Total}", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        var rows = new StackPanel { Spacing = 6 };
        if (item.Checks.Count == 0)
            rows.Children.Add(new TextBlock { Text = GitHubStrings.NoChecks, Style = (Style)Resources["GitHubSecondaryText"], TextWrapping = TextWrapping.Wrap });
        foreach (var check in item.Checks)
        {
            var (glyph, brush) = Outcome(GitHubPresentation.CheckOutcome(check.State));
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 11, Foreground = (Brush)Application.Current.Resources[brush] });
            var name = new TextBlock { Text = check.Name, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTipService.SetToolTip(name, check.Name);
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var state = new TextBlock { Text = check.State.Replace('_', ' ').ToLowerInvariant(), Style = (Style)Resources["GitHubTertiaryText"] };
            Grid.SetColumn(state, 2);
            row.Children.Add(state);
            AutomationProperties.SetName(row, check.Name + ", " + state.Text);
            rows.Children.Add(row);
        }
        panel.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 280, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        if (item.CheckCount > item.Checks.Count)
            panel.Children.Add(new TextBlock { Text = GitHubStrings.ChecksLimited + " " + item.CheckCount.ToString(CultureInfo.CurrentCulture), Style = (Style)Resources["GitHubSecondaryText"] });
        var url = item.Url + "/checks";
        if (GitHubData.IsGitHubUrl(url))
        {
            var link = new HyperlinkButton { Content = GitHubStrings.ViewOnGitHub, Padding = new Thickness(0), FontSize = 12 };
            link.Click += (_, _) => OpenUrl(url);
            panel.Children.Add(link);
        }
        return panel;
    }

    private static FrameworkElement EmptyState(string glyph, string title, string detail)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(16, 28, 16, 16), HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 20,
            Margin = new Thickness(0, 0, 0, 6),
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
        });
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock
        {
            Text = detail,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        return panel;
    }
}
