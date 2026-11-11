using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AgentTerm_App.GitHub;

internal enum GitHubTone
{
    Neutral,
    Success,
    Danger,
    Caution,
    Done
}

internal enum GitHubCheckOutcome
{
    None,
    Passed,
    Pending,
    Failed
}

internal sealed record GitHubCheckSummary(int Passed, uint Total, GitHubCheckOutcome Outcome);

internal readonly record struct GitHubHeatmapGeometry(double Pitch, double Cell, double Width, double Height);

internal readonly record struct GitHubMonthLabel(int Column, string Text);

internal enum GitHubDayMove
{
    PreviousWeek,
    NextWeek,
    PreviousDay,
    NextDay,
    First,
    Last
}

internal static class GitHubPresentation
{
    public const int CalendarRows = 7;

    public static string ErrorText(GitHubFailure failure) => failure switch
    {
        GitHubFailure.MissingCli => GitHubStrings.MissingCli,
        GitHubFailure.Timeout => GitHubStrings.Timeout,
        GitHubFailure.AccountChanged => GitHubStrings.AccountChanged,
        GitHubFailure.InvalidResponse => GitHubStrings.InvalidResponse,
        _ => GitHubStrings.RequestFailed
    };

    public static string StateLabel(string value) => value switch
    {
        "OPEN" => GitHubStrings.Open,
        "CLOSED" => GitHubStrings.Closed,
        "MERGED" => GitHubStrings.Merged,
        "DRAFT" => GitHubStrings.Draft,
        "APPROVED" => GitHubStrings.Approved,
        "CHANGES_REQUESTED" => GitHubStrings.ChangesRequested,
        "REVIEW_REQUIRED" => GitHubStrings.ReviewRequired,
        _ => value
    };

    public static GitHubTone StateTone(string value) => value switch
    {
        "OPEN" or "APPROVED" => GitHubTone.Success,
        "CLOSED" or "CHANGES_REQUESTED" => GitHubTone.Danger,
        "MERGED" => GitHubTone.Done,
        "REVIEW_REQUIRED" => GitHubTone.Caution,
        _ => GitHubTone.Neutral
    };

    public static string ActivityLabel(string kind) => kind switch
    {
        "PushEvent" => GitHubStrings.Pushed,
        "DeleteEvent" => GitHubStrings.DeletedRef,
        "CreateEvent" => GitHubStrings.CreatedRef,
        "PullRequestEvent" => GitHubStrings.PullActivity,
        "PullRequestReviewEvent" => GitHubStrings.ReviewActivity,
        "PullRequestReviewCommentEvent" or "IssueCommentEvent" => GitHubStrings.CommentActivity,
        "IssuesEvent" => GitHubStrings.IssueActivity,
        "ReleaseEvent" => GitHubStrings.ReleaseActivity,
        _ => kind
    };

    public static string QuotaName(GitHubQuota quota) => quota.Kind switch
    {
        "premium_interactions" => quota.Credits ? GitHubStrings.AiCredits : GitHubStrings.PremiumRequests,
        "chat" => GitHubStrings.Chat,
        _ => GitHubStrings.Completions
    };

    public static IEnumerable<GitHubQuota> VisibleQuotas(GitHubUsage usage) =>
        usage.Quotas.Where(quota => !quota.Unlimited || quota.Kind == "premium_interactions");

    public static string QuotaValue(GitHubQuota quota, CultureInfo culture) => quota.Used is { } used
        ? used.ToString("0.0", culture) + "% used"
        : quota.Unlimited ? GitHubStrings.Unlimited : GitHubStrings.NotIncluded;

    public static string ResetText(string reset, TimeZoneInfo zone, CultureInfo culture)
    {
        if (reset.Length == 0) return "";
        if (TryParseTime(reset, out var at))
            return "Resets " + TimeZoneInfo.ConvertTime(at, zone).ToString("MMM d", culture);
        return "Resets " + (reset.Length > 10 ? reset[..10] : reset);
    }

    public static string PlanText(string plan)
    {
        var words = plan.Replace('_', ' ').Trim();
        return words.Length == 0 ? "" : char.ToUpper(words[0], CultureInfo.InvariantCulture) + words[1..];
    }

    public static bool HidesAuthor(GitHubItem item, bool ownPullRequests, string login) =>
        ownPullRequests && login.Length != 0 && string.Equals(item.Author, login, StringComparison.OrdinalIgnoreCase);

    public static string MetaLine(GitHubItem item)
    {
        if (item.Repository.Length == 0 || (item.Repository == item.Title && item.Number == 0 && item.Author.Length == 0)) return "";
        var text = item.Repository;
        if (item.Number != 0) text += " #" + item.Number.ToString(CultureInfo.InvariantCulture);
        if (item.Author.Length != 0) text += " \u00B7 @" + item.Author;
        return text;
    }

    public static string DetailLine(GitHubItem item)
    {
        var parts = new List<string>();
        if (item.ActivityKind.Length != 0) parts.Add(ActivityLabel(item.ActivityKind));
        if (item.Description.Length != 0) parts.Add(item.Description);
        if (item.Events != 0)
            parts.Add(item.Events.ToString(CultureInfo.InvariantCulture) + (item.Events == 1 ? " event" : " events") +
                " in the fetched activity window");
        return string.Join(" \u00B7 ", parts);
    }

    public static string BranchLine(GitHubItem item) => item.Head.Length == 0 ? "" : item.Head + " \u2192 " + item.Base;

    public static string LabelsLine(GitHubItem item)
    {
        var text = string.Join(", ", item.Labels);
        if (item.LabelCount > item.Labels.Count)
            text += (text.Length == 0 ? "" : " \u00B7 ") + GitHubStrings.LabelsLimited + " " + item.LabelCount.ToString(CultureInfo.InvariantCulture);
        return text;
    }

    // Records compare their label and check lists by reference, so compare those element by element.
    public static bool SameItems(IReadOnlyList<GitHubItem> first, IReadOnlyList<GitHubItem> second) =>
        first.Count == second.Count && first.Zip(second).All(pair =>
            pair.First with { Labels = NoLabels, Checks = NoChecks } == pair.Second with { Labels = NoLabels, Checks = NoChecks } &&
            pair.First.Labels.SequenceEqual(pair.Second.Labels) && pair.First.Checks.SequenceEqual(pair.Second.Checks));

    private static readonly IReadOnlyList<string> NoLabels = new string[0];
    private static readonly IReadOnlyList<GitHubCheck> NoChecks = new GitHubCheck[0];

    public static string Items(int count) => count == 1 ? "1 item" : count.ToString(CultureInfo.InvariantCulture) + " items";

    public static string Comments(uint count) => count == 1 ? "1 comment" : count.ToString(CultureInfo.InvariantCulture) + " comments";

    public static GitHubCheckOutcome CheckOutcome(string state) => state switch
    {
        "SUCCESS" => GitHubCheckOutcome.Passed,
        "FAILURE" or "ERROR" or "TIMED_OUT" or "CANCELLED" or "ACTION_REQUIRED" or "STARTUP_FAILURE" => GitHubCheckOutcome.Failed,
        "SKIPPED" or "NEUTRAL" or "STALE" => GitHubCheckOutcome.None,
        _ => GitHubCheckOutcome.Pending
    };

    public static GitHubCheckSummary Checks(GitHubItem item)
    {
        var outcomes = item.Checks.Select(check => CheckOutcome(check.State)).ToArray();
        var outcome = outcomes.Contains(GitHubCheckOutcome.Failed) ? GitHubCheckOutcome.Failed
            : outcomes.Contains(GitHubCheckOutcome.Pending) || item.CheckCount > item.Checks.Count ? GitHubCheckOutcome.Pending
            : outcomes.Contains(GitHubCheckOutcome.Passed) ? GitHubCheckOutcome.Passed
            : GitHubCheckOutcome.None;
        return new GitHubCheckSummary(outcomes.Count(value => value == GitHubCheckOutcome.Passed), item.CheckCount, outcome);
    }

    public static bool TryParseTime(string text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out value);

    public static string Relative(DateTimeOffset value, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        var age = now - value;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return ((int)age.TotalMinutes).ToString(culture) + "m ago";
        if (age < TimeSpan.FromDays(1)) return ((int)age.TotalHours).ToString(culture) + "h ago";
        if (age < TimeSpan.FromDays(7)) return ((int)age.TotalDays).ToString(culture) + "d ago";
        var local = TimeZoneInfo.ConvertTime(value, zone);
        return local.ToString(local.Year == TimeZoneInfo.ConvertTime(now, zone).Year ? "MMM d" : "MMM d, yyyy", culture);
    }

    public static string Absolute(DateTimeOffset value, TimeZoneInfo zone, CultureInfo culture) =>
        TimeZoneInfo.ConvertTime(value, zone).ToString("f", culture);

    public static string SectionStatus<T>(GitHubSection<T> section, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
        where T : class
    {
        if (section.Failure != GitHubFailure.None)
            return (section.Value is null ? "" : GitHubStrings.Stale + " ") + ErrorText(section.Failure);
        return section.Value is null ? "" : GitHubStrings.Updated + " " + Relative(section.Updated, now, zone, culture);
    }

    public static string Contributions(uint count, CultureInfo culture) => count switch
    {
        0 => "No contributions",
        1 => "1 contribution",
        _ => count.ToString("N0", culture) + " contributions"
    };

    public static string Summary(uint total, CultureInfo culture) => Contributions(total, culture) + " in the last 12 months";

    public static string DayReadout(GitHubDay day, int latestYear, CultureInfo culture)
    {
        var date = DateOnly.ParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return date.ToString(date.Year == latestYear ? "ddd, MMM d" : "ddd, MMM d, yyyy", culture) + ": " + Contributions(day.Count, culture);
    }

    // Picks the largest whole-pixel pitch whose grid fits, so every cell and gap renders crisply. Measuring again at
    // the returned width yields the same geometry, which lets the layout shrink to the grid.
    public static GitHubHeatmapGeometry Heatmap(double width, double scale, int columns)
    {
        if (scale <= 0 || !double.IsFinite(scale)) scale = 1;
        columns = Math.Max(1, columns);
        var available = Math.Max(0, width) * scale + 1e-6;
        var pitch = Math.Floor(available / columns) + 1;
        while (pitch > 3 && columns * pitch - Gap(pitch) > available) pitch--;
        pitch = Math.Max(3, pitch);
        var gap = Gap(pitch);
        return new GitHubHeatmapGeometry(pitch / scale, (pitch - gap) / scale,
            (columns * pitch - gap) / scale, (CalendarRows * pitch - gap) / scale);

        static double Gap(double pitch) => Math.Max(1, Math.Round(pitch * 0.22));
    }

    public static int Columns(IReadOnlyList<GitHubDay> days) => days.Count == 0 ? 0 : (int)days[^1].Week + 1;

    public static IReadOnlyList<GitHubMonthLabel> MonthLabels(IReadOnlyList<GitHubDay> days, int labelColumns, CultureInfo culture)
    {
        var columns = Columns(days);
        var starts = new List<(int Column, int Month)>();
        var previousColumn = -1;
        var previousMonth = -1;
        foreach (var day in days)
        {
            if ((int)day.Week == previousColumn) continue;
            previousColumn = (int)day.Week;
            var month = int.Parse(day.Date.AsSpan(5, 2), CultureInfo.InvariantCulture);
            if (month != previousMonth) starts.Add((previousColumn, month));
            previousMonth = month;
        }
        var labels = new List<GitHubMonthLabel>();
        for (var index = 0; index < starts.Count; index++)
        {
            var (column, month) = starts[index];
            var next = index + 1 < starts.Count ? starts[index + 1].Column : int.MaxValue;
            if (column + labelColumns > columns || (labels.Count > 0 && column - labels[^1].Column < labelColumns)) continue;
            if (index == 0 && column == 0 && next - column < labelColumns) continue;
            labels.Add(new GitHubMonthLabel(column, culture.DateTimeFormat.GetAbbreviatedMonthName(month)));
        }
        return labels;
    }

    public static int DayAt(IReadOnlyList<GitHubDay> days, int column, int row)
    {
        if (days.Count == 0 || row is < 0 or >= CalendarRows || column < 0) return -1;
        var index = column * CalendarRows + row - (int)days[0].Weekday;
        return index >= 0 && index < days.Count && days[index].Week == column && days[index].Weekday == row ? index : -1;
    }

    public static int MoveDay(int index, GitHubDayMove move, int count)
    {
        if (count <= 0) return -1;
        index = Math.Clamp(index, 0, count - 1);
        return move switch
        {
            GitHubDayMove.PreviousWeek => index >= CalendarRows ? index - CalendarRows : index,
            GitHubDayMove.NextWeek => index + CalendarRows < count ? index + CalendarRows : index,
            GitHubDayMove.PreviousDay => Math.Max(0, index - 1),
            GitHubDayMove.NextDay => Math.Min(count - 1, index + 1),
            GitHubDayMove.First => 0,
            _ => count - 1
        };
    }
}
