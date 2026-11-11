using System.Text.Json;
using System.Text.Json.Nodes;
using AgentTerm_App.GitHub;

internal static class GitHubChecks
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public static void Run()
    {
        PullRequestStatesRemainIndependent();
        RejectMismatchedPullUrl();
        PullRequestAvatarsAreOptional();
        AccountAvatarIsValidatedAndCached();
        AvatarUrlsRequireGitHubHttpsHost();
        CachePreservesAvatarsAndReadsOlderSnapshots();
        PullQueriesRequestAvatars();
        MissingChecksAreNotPassing();
        CalendarValidatesTotalsAndDates();
        QuotaUsesRemainingPercentage();
        SectionFailureRetainsOriginalSuccessTime();
        ChangedAccountNeverRetainsPrivateData();
        FinalIdentityChangeRejectsSnapshot();
        CancellationDoesNotPublishPartialSuccess();
        SuccessfulEmptyResponseClearsOldItems();
        ExpiredDataIsNotRetained();
        ExpireSectionsRemovesOldBoundaryAndFutureData();
        RefreshDoesNotPublishExpiredSections();
        MalformedSectionDoesNotHideOtherSections();
        UnlimitedQuotaHasNoPercentage();
        CacheRoundTripPreservesAllSections();
        CacheRejectsCorruptionAndUnsafeUrls();
        CacheExpiresSectionsAndRejectsFutureTimestamps();
        CachePersistsAndReplacesOnDisk();
        AutomaticRefreshReusesFreshDataAndManualBypassesCache();
        AutomaticRefreshRetriesFailedSections();
        CachedAccountChangeForcesFreshSections();
        IndependentSectionsRunConcurrently();
        PartialUpdatesArriveBeforeSlowSectionCompletes();

        CalendarAcceptsTomorrowInUtcButNotLater();
        LoginRuleMatchesBetweenApiAndCache();
        HydrationErrorsLeaveOnlyMissingPullsUnhydrated();
        HydrationErrorsStillCheckTheAccount();
        CacheRejectsDuplicateKeys();
        CliSearchesOnlyAbsolutePathEntries();
        CachePathIsUnderLocalAppData();

        AuthProbeTreatsOnlyMissingOrRejectedAccountsAsSignedOut();
        RelativeTimesUseLocalDatesAfterAWeek();
        DayReadoutCountsContributions();
        HeatmapGeometryLandsOnWholePixelsAndFits();
        MonthLabelsNeverOverlapOrOverflow();
        HeatmapNavigationKeepsWeekdaysAndClamps();
        CardLinesFollowTheSourceRules();
        OwnPullRequestsHideOnlyTheAccountAuthor();
        CheckSummaryPrefersFailureThenPending();
        LabelsAndQuotasMatchTheSource();
        SameItemsComparesListsByContent();
        UrlPredicatesRejectMalformedPercentEscapes();
        HostileEventRepositoryNeverReachesQueries();
        IssueEventLinksMustBeGitHubUrls();
        CacheDeleteRemovesFileAndIgnoresMissing();
    }

    private static void UrlPredicatesRejectMalformedPercentEscapes()
    {
        foreach (var suffix in new[] { "a%", "a%z", "a%zz", "a%4", "a%4z", "%", "a%2" })
        {
            Check(!GitHubData.IsGitHubUrl("https://github.com/" + suffix), $"URL must be rejected: {suffix}");
            Check(!GitHubData.IsGitHubAvatarUrl("https://avatars.githubusercontent.com/" + suffix), $"Avatar URL must be rejected: {suffix}");
        }
        foreach (var suffix in new[] { "a%20", "a%2F", "a%2f%41b", "a%20b?x=%7E" })
        {
            Check(GitHubData.IsGitHubUrl("https://github.com/" + suffix), $"URL must be accepted: {suffix}");
            Check(GitHubData.IsGitHubAvatarUrl("https://avatars.githubusercontent.com/" + suffix), $"Avatar URL must be accepted: {suffix}");
        }
        var clock = new ManualClock(Start);
        var snapshot = Refresh(Fake(Api), GitHubSnapshot.Empty, clock);
        GitHubCache.Deserialize(GitHubCache.Serialize(snapshot with { AvatarUrl = "https://avatars.githubusercontent.com/u/1?v=%2F" }), clock);
        ExpectInvalid(() => GitHubCache.Deserialize(
            GitHubCache.Serialize(snapshot with { AvatarUrl = "https://avatars.githubusercontent.com/u/1?v=%zz" }), clock),
            "A cached account avatar with a malformed escape must be rejected.");
    }

    private static void HostileEventRepositoryNeverReachesQueries()
    {
        const string hostile = "a/b\") { x } #";
        var queries = new List<string>();
        var result = Refresh(Fake((endpoint, query) =>
        {
            if (query is not null) lock (queries) queries.Add(query);
            if (endpoint.StartsWith("users/example/events", StringComparison.Ordinal))
                return new JsonArray(new JsonObject
                {
                    ["type"] = "PullRequestEvent",
                    ["repo"] = new JsonObject { ["name"] = hostile },
                    ["created_at"] = "2026-02-01T00:00:00Z",
                    ["payload"] = new JsonObject { ["action"] = "opened", ["number"] = 42 }
                });
            return Api(endpoint, query);
        }), GitHubSnapshot.Empty, new ManualClock(Start));
        Check(result.Activity.Failure == GitHubFailure.InvalidResponse && result.Activity.Value is null,
            "A hostile event repository must fail the activity section as invalid.");
        Check(queries.Count > 0 && queries.All(query => !query.Contains("{ x }", StringComparison.Ordinal) &&
            !query.Contains("a\\\"", StringComparison.Ordinal) && !query.Contains("name:\"b", StringComparison.Ordinal)),
            "A hostile event repository must never reach GraphQL query text.");
    }

    private static void IssueEventLinksMustBeGitHubUrls()
    {
        GitHubSnapshot Activity(string url) => Refresh(Fake((endpoint, query) =>
            endpoint.StartsWith("users/example/events", StringComparison.Ordinal)
                ? new JsonArray(new JsonObject
                {
                    ["type"] = "IssuesEvent",
                    ["repo"] = new JsonObject { ["name"] = "example/repo" },
                    ["created_at"] = "2026-02-01T00:00:00Z",
                    ["payload"] = new JsonObject
                    {
                        ["action"] = "opened",
                        ["issue"] = new JsonObject { ["title"] = "Bug", ["html_url"] = url }
                    }
                })
                : Api(endpoint, query)), GitHubSnapshot.Empty, new ManualClock(Start));
        var good = Activity("https://github.com/example/repo/issues/1");
        Check(good.Activity.Failure == GitHubFailure.None && good.Activity.Value![0].Url == "https://github.com/example/repo/issues/1",
            "A GitHub issue link must be accepted.");
        Check(Activity("https://evil.example/example/repo/issues/1").Activity.Failure == GitHubFailure.InvalidResponse,
            "A non-GitHub issue link must be rejected.");
    }

    private static void CacheDeleteRemovesFileAndIgnoresMissing()
    {
        var folder = Path.Combine(Path.GetTempPath(), "agent-term-github-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "github-dashboard.json");
        try
        {
            GitHubCache.Delete(path);
            Directory.CreateDirectory(folder);
            GitHubCache.Delete(path);
            File.WriteAllText(path, "{}");
            GitHubCache.Delete(path);
            Check(!File.Exists(path), "Delete must remove an existing cache file.");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static readonly System.Globalization.CultureInfo English = System.Globalization.CultureInfo.GetCultureInfo("en-US");

    private static void AuthProbeTreatsOnlyMissingOrRejectedAccountsAsSignedOut()
    {
        GitHubAuthState Classify(string json) => GitHubCli.ClassifyAuth(GitHubJson.Parse(json));
        Check(Classify("""{"hosts":{}}""") == GitHubAuthState.SignedOut, "No stored account is signed out.");
        ExpectInvalid(() => Classify("""{}"""), "Missing hosts are malformed, not signed out.");
        ExpectInvalid(() => Classify("""{"hosts":null}"""), "Null hosts are malformed, not signed out.");
        Check(Classify("""{"hosts":{"github.com":[{"active":false,"state":"success"}]}}""") == GitHubAuthState.SignedOut,
            "Only an inactive account is signed out.");
        Check(Classify("""{"hosts":{"github.com":[{"active":false,"state":"error","error":"x"},{"active":true,"state":"success"}]}}""") ==
            GitHubAuthState.SignedIn, "The active account decides, whatever other accounts report.");
        Check(Classify("""{"hosts":{"github.com":[{"active":true,"state":"error","error":"non-200 OK status code: 401 Unauthorized body: \"{}\""}]}}""") ==
            GitHubAuthState.SignedOut, "A rejected token is signed out.");
        Check(Classify("""{"hosts":{"github.com":[{"active":true,"state":"error","error":"Get \"https://api.github.com/\": dial tcp: connection refused"}]}}""") ==
            GitHubAuthState.Unknown, "A network error must not look like signed out.");
        Check(Classify("""{"hosts":{"github.com":[{"active":true,"state":"timeout"}]}}""") == GitHubAuthState.Unknown,
            "A timed-out check must not look like signed out.");
        ExpectInvalid(() => Classify("""{"hosts":{"github.com":{"active":true}}}"""), "Accounts must be an array.");
        ExpectInvalid(() => Classify("""{"hosts":{"github.com":[{"active":true,"state":"success"},{"active":true,"state":"success"}]}}"""),
            "Two active accounts are malformed.");
        ExpectFailure(GitHubFailure.MissingCli, () =>
        {
            var original = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", "");
                GitHubCli.ProbeAuthAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", original);
            }
        }, "The probe must report a missing gh rather than an unknown state.");
    }

    private static void RelativeTimesUseLocalDatesAfterAWeek()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(-8), "Test", "Test");
        var now = new DateTimeOffset(2026, 11, 10, 20, 0, 0, TimeSpan.Zero);
        string At(TimeSpan age) => GitHubPresentation.Relative(now - age, now, zone, English);
        Check(At(TimeSpan.FromSeconds(30)) == "just now" && At(TimeSpan.FromSeconds(-90)) == "just now", "Recent and future times read as just now.");
        Check(At(TimeSpan.FromMinutes(5)) == "5m ago" && At(TimeSpan.FromHours(2.5)) == "2h ago" && At(TimeSpan.FromDays(3.9)) == "3d ago",
            "Times within a week are relative.");
        Check(At(TimeSpan.FromDays(10)) == "Oct 31", "Older times show the local date.");
        Check(GitHubPresentation.Relative(new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero), now, zone, English) == "Dec 31, 2025",
            "Times in another year include the year.");
        Check(GitHubPresentation.TryParseTime("2026-11-10T19:58:00Z", out var parsed) && parsed == now.AddMinutes(-2),
            "GitHub ISO times parse as UTC.");
        Check(!GitHubPresentation.TryParseTime("", out _), "An empty time does not parse.");
        var absolute = GitHubPresentation.Absolute(now, zone, English);
        Check(absolute.Contains("November 10, 2026", StringComparison.Ordinal) && absolute.Contains("12:00", StringComparison.Ordinal),
            "The tooltip shows the local date and time.");
    }

    private static void DayReadoutCountsContributions()
    {
        Check(GitHubPresentation.DayReadout(new GitHubDay("2026-11-03", 2, 7, 2, 0), 2026, English) == "Tue, Nov 3: 7 contributions",
            "The day readout names the day and its count.");
        Check(GitHubPresentation.DayReadout(new GitHubDay("2026-11-03", 2, 1, 1, 0), 2026, English) == "Tue, Nov 3: 1 contribution" &&
            GitHubPresentation.DayReadout(new GitHubDay("2026-11-03", 2, 0, 0, 0), 2026, English) == "Tue, Nov 3: No contributions",
            "Counts of one and zero read naturally.");
        Check(GitHubPresentation.DayReadout(new GitHubDay("2025-11-09", 0, 1, 1, 0), 2026, English) == "Sun, Nov 9, 2025: 1 contribution",
            "Days from the previous year name their year, so the first and last Nov 9 differ.");
        Check(GitHubPresentation.Summary(9210, English) == "9,210 contributions in the last 12 months", "The summary groups thousands.");
    }

    private static void HeatmapGeometryLandsOnWholePixelsAndFits()
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
        foreach (var width in new[] { 386.0, 366.0, 300.0 })
        {
            var geometry = GitHubPresentation.Heatmap(width, scale, 53);
            var pitch = geometry.Pitch * scale;
            var cell = geometry.Cell * scale;
            Check(Math.Abs(pitch - Math.Round(pitch)) < 1e-9 && Math.Abs(cell - Math.Round(cell)) < 1e-9,
                $"Cells and gaps must be whole device pixels at {scale}.");
            Check(geometry.Width <= width + 1e-9 && geometry.Width + geometry.Pitch > width - 53 / scale,
                $"The calendar must fit {width} DIPs at {scale}, losing less than a pixel per column.");
            Check(geometry.Cell < geometry.Pitch && Math.Abs(geometry.Height - (7 * geometry.Pitch - (geometry.Pitch - geometry.Cell))) < 1e-9,
                "Seven rows must share the column pitch.");
            Check(GitHubPresentation.Heatmap(geometry.Width, scale, 53) == geometry, "Measuring at the grid's own width must keep the grid.");
        }
        var flyout = GitHubPresentation.Heatmap(386, 1.25, 53);
        Check(Math.Round(flyout.Pitch * 1.25) == 9 && Math.Round(flyout.Cell * 1.25) == 7 && Math.Abs(flyout.Width - 380) < 1e-9,
            "The 420-DIP flyout gets 7-pixel cells on a 9-pixel pitch at 125%.");
    }

    private static IReadOnlyList<GitHubDay> Year(DateOnly last)
    {
        var first = last.AddDays(-364 - (int)last.AddDays(-364).DayOfWeek);
        var days = new List<GitHubDay>();
        for (var date = first; date <= last; date = date.AddDays(1))
            days.Add(new GitHubDay(date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), (uint)date.DayOfWeek,
                0, 0, (uint)(days.Count / 7)));
        return days;
    }

    private static void MonthLabelsNeverOverlapOrOverflow()
    {
        foreach (var last in new[] { new DateOnly(2026, 11, 10), new DateOnly(2026, 3, 1), new DateOnly(2026, 8, 31) })
        {
            var days = Year(last);
            var columns = GitHubPresentation.Columns(days);
            var labels = GitHubPresentation.MonthLabels(days, 4, English);
            Check(labels.Count is >= 11 and <= 13, "Most months get a label.");
            for (var index = 0; index < labels.Count; index++)
            {
                Check(labels[index].Column + 4 <= columns, "A label must fit inside the calendar.");
                if (index > 0) Check(labels[index].Column - labels[index - 1].Column >= 4, "Labels must not overlap.");
            }
            foreach (var label in labels)
            {
                var day = days.First(day => day.Week == label.Column);
                var month = int.Parse(day.Date.AsSpan(5, 2), System.Globalization.CultureInfo.InvariantCulture);
                Check(label.Text == English.DateTimeFormat.GetAbbreviatedMonthName(month), "A label names the month its week starts in.");
            }
        }
    }

    private static void HeatmapNavigationKeepsWeekdaysAndClamps()
    {
        var days = Year(new DateOnly(2026, 11, 10));
        var first = (int)days[0].Weekday;
        Check(first == 0, "The test year starts on Sunday.");
        var partial = days.Skip(3).Select((day, index) => day with { Week = (uint)((index + 3) / 7) }).ToList();
        Check(GitHubPresentation.DayAt(partial, 0, 2) == -1 && GitHubPresentation.DayAt(partial, 0, 3) == 0 &&
            GitHubPresentation.DayAt(partial, 1, 0) == 4, "Hit testing accounts for a partial first week.");
        Check(GitHubPresentation.DayAt(days, 53, 0) == -1 && GitHubPresentation.DayAt(days, 0, 7) == -1, "Points outside the grid hit nothing.");
        var last = days.Count - 1;
        Check(GitHubPresentation.MoveDay(last, GitHubDayMove.NextWeek, days.Count) == last &&
            GitHubPresentation.MoveDay(3, GitHubDayMove.PreviousWeek, days.Count) == 3, "Week moves stop at the edges instead of changing weekday.");
        Check(GitHubPresentation.MoveDay(10, GitHubDayMove.PreviousWeek, days.Count) == 3 &&
            GitHubPresentation.MoveDay(10, GitHubDayMove.NextWeek, days.Count) == 17, "Left and right move a week.");
        Check(GitHubPresentation.MoveDay(6, GitHubDayMove.NextDay, days.Count) == 7 &&
            GitHubPresentation.MoveDay(0, GitHubDayMove.PreviousDay, days.Count) == 0 &&
            GitHubPresentation.MoveDay(last, GitHubDayMove.NextDay, days.Count) == last, "Up and down move a day and clamp.");
        Check(GitHubPresentation.MoveDay(40, GitHubDayMove.First, days.Count) == 0 &&
            GitHubPresentation.MoveDay(40, GitHubDayMove.Last, days.Count) == last, "Home and End reach the ends.");
    }

    private static void OwnPullRequestsHideOnlyTheAccountAuthor()
    {
        var pull = new GitHubItem { Title = "Fix", Repository = "o/r", Number = 7, Author = "Octo-Cat" };
        Check(GitHubPresentation.HidesAuthor(pull, true, "octo-cat") && GitHubPresentation.HidesAuthor(pull, true, "OCTO-CAT"),
            "My PRs hides the signed-in account's own author, whatever the login's case.");
        Check(!GitHubPresentation.HidesAuthor(pull, false, "octo-cat"), "Activity and Reviews keep the account's own author.");
        Check(!GitHubPresentation.HidesAuthor(pull with { Author = "someone" }, true, "octo-cat"), "My PRs keeps another author.");
        Check(!GitHubPresentation.HidesAuthor(pull, true, "") && !GitHubPresentation.HidesAuthor(pull with { Author = "" }, true, ""),
            "Without a confirmed account, nothing is hidden.");
        Check(!GitHubPresentation.HidesAuthor(pull, true, "octo-ca"), "Only an exact login match hides the author.");
    }

    private static void CardLinesFollowTheSourceRules()
    {
        var repo = new GitHubItem { Title = "o/r", Repository = "o/r", Url = "https://github.com/o/r" };
        Check(GitHubPresentation.MetaLine(repo) == "", "A repository line that repeats the title is omitted.");
        Check(GitHubPresentation.MetaLine(repo with { Number = 7 }) == "o/r #7" &&
            GitHubPresentation.MetaLine(repo with { Number = 7, Author = "a" }) == "o/r #7 \u00B7 @a", "Number and author join the repository.");
        Check(GitHubPresentation.MetaLine(repo with { Repository = "" }) == "", "Items without a repository have no repository line.");
        var activity = repo with { ActivityKind = "PullRequestEvent", Description = "opened", Events = 3 };
        Check(GitHubPresentation.DetailLine(activity) == "Pull request activity \u00B7 opened \u00B7 3 events in the fetched activity window" &&
            GitHubPresentation.DetailLine(activity with { Events = 1, Description = "" }) == "Pull request activity \u00B7 1 event in the fetched activity window",
            "Activity details join the label, description, and event count.");
        Check(GitHubPresentation.DetailLine(repo with { Description = "A tool" }) == "A tool", "A repository shows its description.");
        var pull = GitHubData.ParsePullRequest(Element(Pull()));
        Check(GitHubPresentation.BranchLine(pull) == "feature \u2192 main" && GitHubPresentation.BranchLine(repo) == "", "Branches show for pulls only.");
        Check(GitHubPresentation.LabelsLine(pull with { Labels = ["bug", "ui"], LabelCount = 12 }) ==
            "bug, ui \u00B7 Showing the first 10 labels. Total: 12", "Truncated labels say how many exist.");
        Check(GitHubPresentation.Comments(1) == "1 comment" && GitHubPresentation.Comments(0) == "0 comments", "Comment counts read naturally.");
    }

    private static void CheckSummaryPrefersFailureThenPending()
    {
        GitHubCheckSummary Summary(uint total, params string[] states) => GitHubPresentation.Checks(new GitHubItem
        {
            CheckCount = total,
            Checks = states.Select(state => new GitHubCheck("c", state)).ToArray()
        });
        Check(Summary(3, "SUCCESS", "SKIPPED", "SUCCESS") == new GitHubCheckSummary(2, 3, GitHubCheckOutcome.Passed), "Skipped checks do not fail.");
        Check(Summary(3, "SUCCESS", "PENDING", "FAILURE").Outcome == GitHubCheckOutcome.Failed, "A failure outranks pending checks.");
        Check(Summary(2, "SUCCESS", "IN_PROGRESS").Outcome == GitHubCheckOutcome.Pending, "Running checks are pending.");
        Check(Summary(101, Enumerable.Repeat("SUCCESS", 100).ToArray()) == new GitHubCheckSummary(100, 101, GitHubCheckOutcome.Pending),
            "Unlisted checks keep the summary pending.");
        Check(Summary(0).Outcome == GitHubCheckOutcome.None && Summary(1, "NEUTRAL").Outcome == GitHubCheckOutcome.None,
            "No checks, or only neutral ones, are not a pass.");
    }

    private static void SameItemsComparesListsByContent()
    {
        var pull = GitHubData.ParsePullRequest(Element(Pull()));
        var copy = GitHubData.ParsePullRequest(Element(Pull()));
        Check(!ReferenceEquals(pull.Checks, copy.Checks) && GitHubPresentation.SameItems([pull], [copy]),
            "Separately parsed identical items are the same.");
        Check(!GitHubPresentation.SameItems([pull], [copy with { Labels = ["x"], LabelCount = 1 }]) &&
            !GitHubPresentation.SameItems([pull], [copy with { Checks = [new GitHubCheck("build", "FAILURE")] }]) &&
            !GitHubPresentation.SameItems([pull], [copy with { Title = "Other" }]) && !GitHubPresentation.SameItems([pull], []),
            "Any changed field, label, or check is a change.");
    }

    private static void LabelsAndQuotasMatchTheSource()
    {
        Check(GitHubPresentation.StateLabel("CHANGES_REQUESTED") == "Changes requested" && GitHubPresentation.StateLabel("ODD") == "ODD",
            "Unknown states pass through.");
        Check(GitHubPresentation.StateTone("MERGED") == GitHubTone.Done && GitHubPresentation.StateTone("DRAFT") == GitHubTone.Neutral &&
            GitHubPresentation.StateTone("REVIEW_REQUIRED") == GitHubTone.Caution && GitHubPresentation.StateTone("CLOSED") == GitHubTone.Danger,
            "Pills take GitHub's state colors.");
        Check(GitHubPresentation.ActivityLabel("PullRequestReviewCommentEvent") == "Comment activity" &&
            GitHubPresentation.ActivityLabel("WatchEvent") == "WatchEvent", "Activity labels match the source.");
        Check(GitHubPresentation.ErrorText(GitHubFailure.None) == GitHubStrings.RequestFailed, "Unknown failures read as request failures.");
        var usage = new GitHubUsage("individual_pro",
        [
            new GitHubQuota { Kind = "premium_interactions", Unlimited = true },
            new GitHubQuota { Kind = "chat", Unlimited = true },
            new GitHubQuota { Kind = "completions", Included = false }
        ]);
        Check(GitHubPresentation.VisibleQuotas(usage).Select(quota => quota.Kind).SequenceEqual(["premium_interactions", "completions"]),
            "Unlimited chat is hidden; unlimited premium requests and excluded quotas show.");
        Check(GitHubPresentation.QuotaName(new GitHubQuota { Kind = "premium_interactions", Credits = true }) == "AI credits" &&
            GitHubPresentation.QuotaValue(usage.Quotas[0], English) == "Unlimited" &&
            GitHubPresentation.QuotaValue(usage.Quotas[2], English) == "Not included" &&
            GitHubPresentation.QuotaValue(new GitHubQuota { Used = 4.05 }, English) == "4.1% used", "Quota text matches the source.");
        var utc = TimeZoneInfo.Utc;
        Check(GitHubPresentation.ResetText("2026-12-01T00:00:00Z", utc, English) == "Resets Dec 1" &&
            GitHubPresentation.ResetText("", utc, English) == "", "Reset dates are short and local.");
        Check(GitHubPresentation.PlanText("individual_pro") == "Individual pro" && GitHubPresentation.PlanText("") == "", "Plans read as words.");
    }

    private static void PullRequestStatesRemainIndependent()
    {
        var pull = GitHubData.ParsePullRequest(Element(Pull()));
        Check(pull.State == "MERGED" && pull.Review == "APPROVED" && pull.Checks[0].State == "SUCCESS" &&
            pull.Checks[1].State == "SKIPPED" && pull.Checks[2].State == "PENDING",
            "Pull state, review decision, and check states must parse independently.");
    }

    private static void RejectMismatchedPullUrl()
    {
        var pull = Pull();
        pull["url"] = "https://github.com/example/other/pull/42";
        ExpectInvalid(() => GitHubData.ParsePullRequest(Element(pull)), "A pull URL must match its repository and number.");
        Check(!GitHubData.IsGitHubUrl("https://github.com.evil.example/repo") && !GitHubData.IsGitHubUrl("https://github.com\\@evil.example/") &&
            !GitHubData.IsGitHubUrl("file:///C:/Windows"), "Only https://github.com/ URLs are GitHub URLs.");
    }

    private static void PullRequestAvatarsAreOptional()
    {
        var value = Pull();
        Check(GitHubData.ParsePullRequest(Element(value)).AvatarUrl == "https://avatars.githubusercontent.com/u/1?s=96&v=4",
            "Pull author avatars must be read.");
        value["author"]!.AsObject().Remove("avatarUrl");
        Check(GitHubData.ParsePullRequest(Element(value)).AvatarUrl == "", "A missing author avatar must be empty.");
        value["author"] = null;
        var parsed = GitHubData.ParsePullRequest(Element(value));
        Check(parsed.AvatarUrl == "" && parsed.Author == "", "A deleted author must leave author and avatar empty.");
    }

    private static void AccountAvatarIsValidatedAndCached()
    {
        var clock = new ManualClock(Start);
        var account = GitHubData.ParseIdentity(Element(User()));
        Check(account.AvatarUrl == "https://avatars.githubusercontent.com/u/1?v=4", "The account avatar must be read.");
        var encoded = Node(GitHubCache.Serialize(account));
        Check(GitHubCache.Deserialize(encoded.ToJsonString(), clock).AvatarUrl == account.AvatarUrl, "The account avatar must be cached.");
        encoded.AsObject().Remove("avatarUrl");
        Check(GitHubCache.Deserialize(encoded.ToJsonString(), clock).AvatarUrl == "", "Older caches without an avatar must load.");
        encoded["avatarUrl"] = "file:///C:/avatar.png";
        ExpectInvalid(() => GitHubCache.Deserialize(encoded.ToJsonString(), clock), "A cached avatar must be a GitHub avatar URL.");
        var user = User();
        user["avatar_url"] = "https://example.com/avatar.png";
        ExpectInvalid(() => GitHubData.ParseIdentity(Element(user)), "An account avatar must be a GitHub avatar URL.");
        user.AsObject().Remove("avatar_url");
        Check(GitHubData.ParseIdentity(Element(user)).AvatarUrl == "", "A missing account avatar must be empty.");
    }

    private static void AvatarUrlsRequireGitHubHttpsHost()
    {
        Check(GitHubData.IsGitHubAvatarUrl("https://avatars.githubusercontent.com:443/u/1?v=4"), "The explicit HTTPS port must be accepted.");
        foreach (var url in new[]
        {
            "http://avatars.githubusercontent.com/u/1",
            "https://avatars.githubusercontent.com.evil.example/u/1",
            "https://avatars.githubusercontent.com@evil.example/u/1",
            "https://avatars.githubusercontent.com:8443/u/1",
            "https://avatars.githubusercontent.com\\@evil.example/u/1",
            "https://avatars.githubusercontent.com/u/\n1",
            "file:///C:/avatar.png"
        })
        {
            Check(!GitHubData.IsGitHubAvatarUrl(url), $"Avatar URL must be rejected: {url}");
            var value = Pull();
            value["author"]!["avatarUrl"] = url;
            ExpectInvalid(() => GitHubData.ParsePullRequest(Element(value)), $"Pull avatar must be rejected: {url}");
        }
    }

    private static void CachePreservesAvatarsAndReadsOlderSnapshots()
    {
        var clock = new ManualClock(Start);
        var snapshot = AddPull(Refresh(Fake(Api), GitHubSnapshot.Empty, clock));
        var encoded = Node(GitHubCache.Serialize(snapshot));
        Check(GitHubCache.Deserialize(encoded.ToJsonString(), clock).Pulls.Value![0].AvatarUrl == snapshot.Pulls.Value![0].AvatarUrl,
            "Cached pull avatars must round trip.");
        encoded["pulls"]!["value"]![0]!.AsObject().Remove("avatarUrl");
        Check(GitHubCache.Deserialize(encoded.ToJsonString(), clock).Pulls.Value![0].AvatarUrl == "", "Older cached pulls must load.");
        encoded["pulls"]!["value"]![0]!["avatarUrl"] = "https://example.com/avatar";
        ExpectInvalid(() => GitHubCache.Deserialize(encoded.ToJsonString(), clock), "Cached pull avatars must be GitHub avatar URLs.");
    }

    private static void PullQueriesRequestAvatars()
    {
        var pullQueries = 0;
        var result = Refresh(Fake((endpoint, query) =>
        {
            var response = Api(endpoint, query);
            if (query?.Contains("... on PullRequest") == true)
            {
                if (query.Contains("avatarUrl(size:96)")) Interlocked.Increment(ref pullQueries);
                response["data"]!["search"]!["nodes"]!.AsArray().Add(Pull());
            }
            return response;
        }), GitHubSnapshot.Empty, new ManualClock(Start));
        Check(pullQueries == 2 && result.Pulls.Value![0].AvatarUrl != "" && result.Reviews.Value![0].AvatarUrl != "",
            "Both pull queries must request and return author avatars.");
    }

    private static void MissingChecksAreNotPassing()
    {
        var pull = Pull();
        pull["commits"]!["nodes"]![0]!["commit"]!["statusCheckRollup"] = null;
        var parsed = GitHubData.ParsePullRequest(Element(pull));
        Check(parsed.CheckCount == 0 && parsed.Checks.Count == 0, "A pull without a check rollup must have no checks.");
    }

    private static void CalendarValidatesTotalsAndDates()
    {
        var calendar = CalendarValue();
        Check(GitHubData.ParseCalendar(Element(calendar), Start).Total == 2, "A valid calendar must parse.");
        calendar["totalContributions"] = 3;
        ExpectInvalid(() => GitHubData.ParseCalendar(Element(calendar), Start), "The calendar total must match its days.");
        calendar = CalendarValue();
        calendar["weeks"]![0]!["contributionDays"]![1]!["date"] = "2026-01-04";
        ExpectInvalid(() => GitHubData.ParseCalendar(Element(calendar), Start), "Calendar days must be consecutive and match weekdays.");
    }

    private static void QuotaUsesRemainingPercentage()
    {
        var value = Api("copilot_internal/user", null);
        var usage = GitHubData.ParseUsage(Element(value), "example");
        Check(usage.Quotas[0].Credits && usage.Quotas[0].Used == 49.6, "Quota usage must be 100 minus the remaining percentage.");
        ExpectFailure(GitHubFailure.AccountChanged, () => GitHubData.ParseUsage(Element(value), "different"),
            "Usage for another login must be rejected.");
        value["quota_snapshots"]!["premium_interactions"]!["percent_remaining"] = 101;
        ExpectInvalid(() => GitHubData.ParseUsage(Element(value), "example"), "Remaining percentage above 100 must be rejected.");
    }

    private static void SectionFailureRetainsOriginalSuccessTime()
    {
        var clock = new ManualClock(Start);
        var previous = Refresh(Fake(Api), GitHubSnapshot.Empty, clock);
        clock.Now = Start.AddMinutes(1);
        var result = Refresh(Fake((endpoint, query) => endpoint == "copilot_internal/user"
            ? throw new GitHubException(GitHubFailure.Request)
            : Api(endpoint, query)), previous, clock);
        Check(result.Usage.Value is not null && result.Usage.Updated == previous.Usage.Updated && result.Usage.Failure == GitHubFailure.Request &&
            result.Pulls.Failure == GitHubFailure.None && result.Pulls.Updated == clock.Now,
            "A failed section must keep its data and original success time while others refresh.");
    }

    private static void ChangedAccountNeverRetainsPrivateData()
    {
        var clock = new ManualClock(Start);
        var previous = Refresh(Fake(Api), GitHubSnapshot.Empty, clock) with { Id = "2" };
        var result = Refresh(Fake((endpoint, query) => endpoint == "copilot_internal/user"
            ? throw new GitHubException(GitHubFailure.Request)
            : Api(endpoint, query)), previous, clock);
        Check(result.Usage.Value is null, "Another account's data must never be retained.");
    }

    private static void FinalIdentityChangeRejectsSnapshot()
    {
        var userCalls = 0;
        ExpectFailure(GitHubFailure.AccountChanged, () => Refresh(Fake((endpoint, query) =>
        {
            var value = Api(endpoint, query);
            if (endpoint == "user" && Interlocked.Increment(ref userCalls) == 2) value["id"] = 2;
            return value;
        }), GitHubSnapshot.Empty, new ManualClock(Start)), "A changed account after loading must reject the snapshot.");
    }

    private static void CancellationDoesNotPublishPartialSuccess()
    {
        var clock = new ManualClock(Start);
        Check(Throws<OperationCanceledException>(() => Refresh(Fake((_, _) => throw new OperationCanceledException()),
            GitHubSnapshot.Empty, clock)), "Cancellation of the identity request must propagate.");
        Check(Throws<OperationCanceledException>(() => Refresh(Fake((endpoint, query) => endpoint == "user"
            ? Api(endpoint, query)
            : throw new OperationCanceledException()), GitHubSnapshot.Empty, clock)),
            "Section cancellation must propagate instead of becoming a section failure.");

        using var cancellation = new CancellationTokenSource();
        GitHubRequest request = async (endpoint, query, token) =>
        {
            if (endpoint == "copilot_internal/user") await Task.Delay(Timeout.Infinite, token);
            return Element(Api(endpoint, query));
        };
        var userCalls = 0;
        GitHubRequest counted = (endpoint, query, token) =>
        {
            if (endpoint == "user") Interlocked.Increment(ref userCalls);
            return request(endpoint, query, token);
        };
        Check(Throws<OperationCanceledException>(() => GitHubData.RefreshAsync(counted, GitHubSnapshot.Empty, GitHubRefreshReason.Manual,
                partial =>
                {
                    if (partial.Pulls.Value is not null) cancellation.Cancel();
                }, clock, cancellation.Token).GetAwaiter().GetResult()) && userCalls == 1,
            "Cancelling the token mid-refresh must throw before the final identity check.");
    }

    private static void SuccessfulEmptyResponseClearsOldItems()
    {
        var clock = new ManualClock(Start);
        var previous = AddPull(Refresh(Fake(Api), GitHubSnapshot.Empty, clock));
        Check(Refresh(Fake(Api), previous, clock).Pulls.Value!.Count == 0, "A successful empty response must replace old items.");
    }

    private static void ExpiredDataIsNotRetained()
    {
        var clock = new ManualClock(Start);
        var previous = Refresh(Fake(Api), GitHubSnapshot.Empty, clock);
        previous = previous with { Usage = previous.Usage with { Updated = previous.Usage.Updated - TimeSpan.FromDays(7) } };
        var result = Refresh(Fake((endpoint, query) => endpoint == "copilot_internal/user"
            ? throw new GitHubException(GitHubFailure.Timeout)
            : Api(endpoint, query)), previous, clock);
        Check(result.Usage.Value is null && result.Usage.Failure == GitHubFailure.Timeout, "A failure must not retain expired data.");
    }

    private static void ExpireSectionsRemovesOldBoundaryAndFutureData()
    {
        var now = Start;
        var snapshot = Refresh(Fake(Api), GitHubSnapshot.Empty, new ManualClock(now));
        var week = TimeSpan.FromDays(7);
        var second = TimeSpan.FromSeconds(1);
        snapshot = snapshot with
        {
            Activity = snapshot.Activity with { Updated = now - week - second, Failure = GitHubFailure.Request },
            Pulls = snapshot.Pulls with { Updated = now - week },
            Reviews = snapshot.Reviews with { Updated = now - week + second },
            Repos = snapshot.Repos with { Updated = now + second }
        };
        var expired = GitHubData.ExpireSections(snapshot, now);
        Check(expired.Activity.Value is null && expired.Activity.Failure == GitHubFailure.Request && expired.Pulls.Value is null &&
            expired.Reviews.Value is not null && expired.Repos.Value is null && expired.Calendar.Value is not null &&
            expired.Usage.Value is not null, "Sections at or past seven days, or in the future, must expire.");
    }

    private static void RefreshDoesNotPublishExpiredSections()
    {
        var clock = new ManualClock(Start);
        var previous = AddPull(Refresh(Fake(Api), GitHubSnapshot.Empty, clock));
        previous = previous with { Pulls = previous.Pulls with { Updated = previous.Pulls.Updated - TimeSpan.FromDays(7) } };
        var publishedExpiredPulls = false;
        Refresh(Fake(Api), previous, clock, GitHubRefreshReason.Automatic, partial =>
        {
            if (partial.Pulls.Value is { Count: > 0 }) publishedExpiredPulls = true;
        });
        Check(!publishedExpiredPulls, "Partial updates must never publish expired sections.");
    }

    private static void MalformedSectionDoesNotHideOtherSections()
    {
        var result = Refresh(Fake((endpoint, query) => endpoint == "copilot_internal/user" ? new JsonArray() : Api(endpoint, query)),
            GitHubSnapshot.Empty, new ManualClock(Start));
        Check(result.Usage.Value is null && result.Usage.Failure == GitHubFailure.InvalidResponse && result.Calendar.Value is not null &&
            result.Pulls.Value is not null, "One malformed section must not hide the others.");
    }

    private static void UnlimitedQuotaHasNoPercentage()
    {
        var value = Api("copilot_internal/user", null);
        value["quota_snapshots"]!["premium_interactions"]!["unlimited"] = true;
        value["quota_snapshots"]!["premium_interactions"]!["percent_remaining"] = null;
        var usage = GitHubData.ParseUsage(Element(value), "example");
        Check(usage.Quotas[0].Unlimited && usage.Quotas[0].Used is null, "An unlimited quota must have no usage percentage.");
    }

    private static void CacheRoundTripPreservesAllSections()
    {
        var clock = new ManualClock(Start);
        var snapshot = Refresh(Fake(Api), GitHubSnapshot.Empty, clock);
        var pull = GitHubData.ParsePullRequest(Element(Pull())) with
        {
            Labels = ["bug", "windows"],
            LabelCount = 2,
            Description = "Description",
            ActivityKind = "PullRequestEvent",
            Events = 3
        };
        var pulls = snapshot.Pulls with { Value = [.. snapshot.Pulls.Value!, pull] };
        snapshot = snapshot with
        {
            Pulls = pulls,
            Activity = pulls,
            Reviews = pulls,
            Repos = pulls,
            Usage = snapshot.Usage with { Failure = GitHubFailure.Timeout }
        };
        var encoded = GitHubCache.Serialize(snapshot);
        var cached = GitHubCache.Deserialize(encoded, clock);
        Check(encoded == GitHubCache.Serialize(cached) && cached.Usage.Failure == GitHubFailure.Request &&
            cached.Pulls.Value![0].Labels[0] == "bug", "The cache must round trip every section.");
    }

    private static void CacheRejectsCorruptionAndUnsafeUrls()
    {
        var clock = new ManualClock(Start);
        var snapshot = AddPull(Refresh(Fake(Api), GitHubSnapshot.Empty, clock));
        void Reject(Action<JsonNode> corrupt, string message)
        {
            var encoded = Node(GitHubCache.Serialize(snapshot));
            corrupt(encoded);
            ExpectInvalid(() => GitHubCache.Deserialize(encoded.ToJsonString(), clock), message);
        }
        Reject(encoded => encoded["version"] = 2, "Unknown cache versions must be rejected.");
        Reject(encoded => encoded["pulls"]!["value"]![0]!["url"] = "https://github.com.evil.example/private", "Unsafe cached URLs must be rejected.");
        Reject(encoded => encoded["id"] = "../other", "Non-numeric cached IDs must be rejected.");
        Reject(encoded => encoded["usage"]!["value"]!["quotas"]![0]!["used"] = 101, "Cached usage above 100 must be rejected.");
    }

    private static void CacheExpiresSectionsAndRejectsFutureTimestamps()
    {
        var clock = new ManualClock(Start);
        var snapshot = Refresh(Fake(Api), GitHubSnapshot.Empty, clock);
        var old = snapshot with { Pulls = snapshot.Pulls with { Updated = snapshot.Pulls.Updated - TimeSpan.FromDays(8) } };
        var cached = GitHubCache.Deserialize(GitHubCache.Serialize(old), clock);
        Check(cached.Pulls.Value is null && cached.Calendar.Value is not null, "Expired cached sections must be dropped individually.");
        var future = snapshot with { Pulls = snapshot.Pulls with { Updated = clock.Now.AddHours(1) } };
        ExpectInvalid(() => GitHubCache.Deserialize(GitHubCache.Serialize(future), clock), "Future cache timestamps must be rejected.");
    }

    private static void CachePersistsAndReplacesOnDisk()
    {
        var clock = new ManualClock(Start);
        var folder = Path.Combine(Path.GetTempPath(), "agent-term-github-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "nested", "github-dashboard.json");
        try
        {
            Check(GitHubCache.Read(path, clock).Login == "", "A missing cache must read as an empty snapshot.");
            var snapshot = Refresh(Fake(Api), GitHubSnapshot.Empty, clock);
            GitHubCache.Write(path, snapshot);
            Check(GitHubCache.Serialize(snapshot) == GitHubCache.Serialize(GitHubCache.Read(path, clock)), "The cache must persist on disk.");
            GitHubCache.Write(path, AddPull(snapshot));
            Check(GitHubCache.Read(path, clock).Pulls.Value!.Count == 1, "Writing must replace the cached file.");
            Check(Directory.GetFiles(Path.GetDirectoryName(path)!).Length == 1, "Writing must not leave staging files behind.");
            File.WriteAllText(path, "");
            ExpectInvalid(() => GitHubCache.Read(path, clock), "An empty cache file must be rejected.");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static void AutomaticRefreshReusesFreshDataAndManualBypassesCache()
    {
        var clock = new ManualClock(Start);
        var previous = GitHubCache.Deserialize(GitHubCache.Serialize(Refresh(Fake(Api), GitHubSnapshot.Empty, clock)), clock);
        var sectionCalls = 0;
        var request = Fake((endpoint, query) =>
        {
            if (endpoint != "user") Interlocked.Increment(ref sectionCalls);
            return Api(endpoint, query);
        });
        var cached = Refresh(request, previous, clock, GitHubRefreshReason.Automatic);
        Check(sectionCalls == 0 && cached.Pulls.Updated == previous.Pulls.Updated, "Automatic refresh must reuse fresh sections.");
        Refresh(request, previous, clock, GitHubRefreshReason.Manual);
        Check(sectionCalls == 6, "Manual refresh must reload every section.");
        clock.Now = Start.AddMinutes(5);
        sectionCalls = 0;
        Refresh(request, previous, clock, GitHubRefreshReason.Automatic);
        Check(sectionCalls == 4, "After five minutes, only the two 15-minute sections may stay cached.");
        clock.Now = Start.AddMinutes(15);
        sectionCalls = 0;
        Refresh(request, previous, clock, GitHubRefreshReason.Automatic);
        Check(sectionCalls == 6, "After fifteen minutes, every section must reload.");
    }

    private static void AutomaticRefreshRetriesFailedSections()
    {
        var clock = new ManualClock(Start);
        var previous = Refresh(Fake(Api), GitHubSnapshot.Empty, clock);
        previous = previous with { Usage = previous.Usage with { Failure = GitHubFailure.Request } };
        var sectionCalls = 0;
        var result = Refresh(Fake((endpoint, query) =>
        {
            if (endpoint != "user") Interlocked.Increment(ref sectionCalls);
            return Api(endpoint, query);
        }), previous, clock, GitHubRefreshReason.Automatic);
        Check(sectionCalls == 1 && result.Usage.Failure == GitHubFailure.None, "Automatic refresh must retry failed sections.");
    }

    private static void CachedAccountChangeForcesFreshSections()
    {
        var clock = new ManualClock(Start);
        var previous = GitHubCache.Deserialize(GitHubCache.Serialize(Refresh(Fake(Api), GitHubSnapshot.Empty, clock)), clock) with { Id = "2" };
        var sectionCalls = 0;
        Refresh(Fake((endpoint, query) =>
        {
            if (endpoint != "user") Interlocked.Increment(ref sectionCalls);
            return Api(endpoint, query);
        }), previous, clock, GitHubRefreshReason.Automatic);
        Check(sectionCalls == 6, "A cached snapshot for another account must not satisfy an automatic refresh.");
    }

    private static void IndependentSectionsRunConcurrently()
    {
        var entered = 0;
        var timedOut = false;
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Refresh(async (endpoint, query, _) =>
        {
            if (endpoint != "user")
            {
                if (Interlocked.Increment(ref entered) == 6) all.TrySetResult();
                try
                {
                    await all.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    timedOut = true;
                }
            }
            return Element(Api(endpoint, query));
        }, GitHubSnapshot.Empty, new ManualClock(Start));
        Check(entered == 6 && !timedOut, "All six sections must be in flight at once.");
    }

    private static void PartialUpdatesArriveBeforeSlowSectionCompletes()
    {
        var timedOut = false;
        var listPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Refresh(async (endpoint, query, _) =>
        {
            if (endpoint == "copilot_internal/user")
            {
                try
                {
                    await listPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    timedOut = true;
                }
            }
            return Element(Api(endpoint, query));
        }, GitHubSnapshot.Empty, new ManualClock(Start), GitHubRefreshReason.Manual, partial =>
        {
            if (partial.Pulls.Value is not null && partial.Usage.Value is null) listPublished.TrySetResult();
        });
        Check(listPublished.Task.IsCompleted && !timedOut, "Partial updates must arrive before a slow section completes.");
    }

    private static void CalendarAcceptsTomorrowInUtcButNotLater()
    {
        var clock = new ManualClock(Start);
        static JsonNode Days(string lastDate, int lastWeekday) => Node($$"""
            {"totalContributions":0,"weeks":[{"contributionDays":[
                {"date":"2026-03-01","weekday":0,"contributionCount":0,"contributionLevel":"NONE"},
                {"date":"{{lastDate}}","weekday":{{lastWeekday}},"contributionCount":0,"contributionLevel":"NONE"}]}]}
            """);
        Check(GitHubData.ParseCalendar(Element(Days("2026-03-02", 1)), Start).Days.Count == 2,
            "A calendar day one day after today in UTC must be accepted.");
        var later = Days("2026-03-02", 1);
        later["weeks"]![0]!["contributionDays"]!.AsArray().Add(Node("""{"date":"2026-03-03","weekday":2,"contributionCount":0,"contributionLevel":"NONE"}"""));
        ExpectInvalid(() => GitHubData.ParseCalendar(Element(later), Start), "A calendar day two days after today in UTC must be rejected.");

        GitHubSnapshot WithCalendar(params GitHubDay[] days) => GitHubData.ParseIdentity(Element(User())) with
        {
            Calendar = new GitHubSection<GitHubCalendar>(new GitHubCalendar(0, days), Start, GitHubFailure.None)
        };
        var tomorrow = WithCalendar(new GitHubDay("2026-03-01", 0, 0, 0, 0), new GitHubDay("2026-03-02", 1, 0, 0, 0));
        Check(GitHubCache.Deserialize(GitHubCache.Serialize(tomorrow), clock).Calendar.Value!.Days.Count == 2,
            "The cache must accept a calendar day one day after today in UTC.");
        var tooLate = WithCalendar(new GitHubDay("2026-03-01", 0, 0, 0, 0), new GitHubDay("2026-03-02", 1, 0, 0, 0),
            new GitHubDay("2026-03-03", 2, 0, 0, 0));
        ExpectInvalid(() => GitHubCache.Deserialize(GitHubCache.Serialize(tooLate), clock),
            "The cache must reject a calendar day two days after today in UTC.");
    }

    private static void LoginRuleMatchesBetweenApiAndCache()
    {
        var clock = new ManualClock(Start);
        var user = User();
        user["login"] = "user_name.x-1";
        var account = GitHubData.ParseIdentity(Element(user));
        Check(GitHubCache.Deserialize(GitHubCache.Serialize(account), clock).Login == "user_name.x-1",
            "The cache must accept every login the API parser accepts.");
        foreach (var login in new[] { "", "bad/login", "bad login", new string('a', 101) })
        {
            user["login"] = login;
            ExpectInvalid(() => GitHubData.ParseIdentity(Element(user)), $"The API parser must reject login <{login}>.");
            ExpectInvalid(() => GitHubCache.Deserialize(GitHubCache.Serialize(account with { Login = login }), clock),
                $"The cache must reject login <{login}>.");
        }
    }

    private static JsonNode HydrationEvents() => Node("""
        [
            {"type":"PullRequestEvent","repo":{"name":"example/repo"},"created_at":"2026-02-01T00:00:00Z","payload":{"action":"opened","number":42}},
            {"type":"PullRequestReviewEvent","repo":{"name":"example/repo"},"created_at":"2026-02-02T00:00:00Z","payload":{"action":"created","pull_request":{"number":42}}},
            {"type":"PullRequestEvent","repo":{"name":"example/gone"},"created_at":"2026-02-03T00:00:00Z","payload":{"action":"closed","number":7}}
        ]
        """);

    private static JsonNode HydrationResponse(string viewer) => new JsonObject
    {
        ["data"] = new JsonObject
        {
            ["viewer"] = new JsonObject { ["login"] = viewer },
            ["r0"] = new JsonObject { ["pullRequest"] = Pull() },
            ["r1"] = null
        },
        ["errors"] = new JsonArray(new JsonObject { ["type"] = "NOT_FOUND", ["message"] = "Could not resolve to a Repository." })
    };

    private static void HydrationErrorsLeaveOnlyMissingPullsUnhydrated()
    {
        var hydrationQueries = 0;
        var result = Refresh(Fake((endpoint, query) =>
        {
            if (endpoint.StartsWith("users/example/events", StringComparison.Ordinal)) return HydrationEvents();
            if (query?.Contains("pullRequest(number:") == true)
            {
                Interlocked.Increment(ref hydrationQueries);
                return HydrationResponse("example");
            }
            var response = Api(endpoint, query);
            if (endpoint == "graphql") response["errors"] = new JsonArray(new JsonObject { ["message"] = "partial" });
            return response;
        }), GitHubSnapshot.Empty, new ManualClock(Start));
        var activity = result.Activity.Value;
        Check(result.Activity.Failure == GitHubFailure.None && hydrationQueries == 1 && activity is { Count: 2 },
            "Hydration errors must not fail the activity section.");
        Check(activity![0].Title == "Test pull" && activity[0].State == "MERGED" && activity[0].Events == 2 &&
            activity[0].ActivityKind == "PullRequestEvent" && activity[0].Description == "opened" &&
            activity[0].UpdatedAt == "2026-02-01T00:00:00Z", "Pulls returned beside hydration errors must be hydrated.");
        Check(activity[1].Title == "example/gone" && activity[1].State == "" && activity[1].Url == "https://github.com/example/gone/pull/7" &&
            activity[1].Events == 1, "Pulls missing from a hydration response must stay unhydrated.");
        Check(result.Pulls.Failure == GitHubFailure.InvalidResponse && result.Reviews.Failure == GitHubFailure.InvalidResponse &&
            result.Calendar.Failure == GitHubFailure.InvalidResponse, "Search and calendar queries must still reject GraphQL errors.");
    }

    private static void HydrationErrorsStillCheckTheAccount()
    {
        ExpectFailure(GitHubFailure.AccountChanged, () => Refresh(Fake((endpoint, query) =>
        {
            if (endpoint.StartsWith("users/example/events", StringComparison.Ordinal)) return HydrationEvents();
            return query?.Contains("pullRequest(number:") == true ? HydrationResponse("someone-else") : Api(endpoint, query);
        }), GitHubSnapshot.Empty, new ManualClock(Start)), "A hydration response for another viewer must reject the refresh.");
    }

    private static void CacheRejectsDuplicateKeys()
    {
        var clock = new ManualClock(Start);
        var encoded = GitHubCache.Serialize(Refresh(Fake(Api), GitHubSnapshot.Empty, clock));
        GitHubCache.Deserialize(encoded, clock);
        ExpectInvalid(() => GitHubCache.Deserialize(encoded.Replace("\"version\":1", "\"version\":1,\"version\":1"), clock),
            "Duplicate top-level cache keys must be rejected.");
        ExpectInvalid(() => GitHubCache.Deserialize(encoded.Replace("\"failed\":false", "\"failed\":false,\"failed\":true"), clock),
            "Duplicate nested cache keys must be rejected.");
    }

    private static void CliSearchesOnlyAbsolutePathEntries()
    {
        var folder = Path.Combine(Path.GetTempPath(), "agent-term-gh-" + Guid.NewGuid().ToString("N"));
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalDirectory = Environment.CurrentDirectory;
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, OperatingSystem.IsWindows() ? "gh.exe" : "gh"), []);
        try
        {
            Environment.CurrentDirectory = folder;
            Environment.SetEnvironmentVariable("PATH", "." + Path.PathSeparator + "relative");
            ExpectFailure(GitHubFailure.MissingCli, () => GitHubCli.ReadApiAsync("user", null, CancellationToken.None).GetAwaiter().GetResult(),
                "gh must not be found through relative PATH entries or the current directory.");
            Environment.SetEnvironmentVariable("PATH", "\"" + folder + "\"");
            ExpectFailure(GitHubFailure.Request, () => GitHubCli.ReadApiAsync("user", null, CancellationToken.None).GetAwaiter().GetResult(),
                "A quoted absolute PATH entry must be searched, and a gh that cannot start must be a request failure.");
            Check(Throws<OperationCanceledException>(() =>
                GitHubCli.ReadApiAsync("user", null, new CancellationToken(canceled: true)).GetAwaiter().GetResult()),
                "A cancelled token must stop the request before it starts.");
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void CachePathIsUnderLocalAppData()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentTerm", "github-dashboard.json");
        Check(GitHubCache.DefaultPath() == expected, "The cache must live beside AgentTerm settings in local app data.");
    }

    private static JsonNode Node(string json) => JsonNode.Parse(json)!;

    private static JsonElement Element(JsonNode node) => GitHubJson.Parse(node.ToJsonString());

    private static JsonNode Pull() => Node("""
        {
            "number":42,"title":"Test pull","url":"https://github.com/example/repo/pull/42",
            "repository":{"nameWithOwner":"example/repo"},"updatedAt":"2026-01-01T00:00:00Z",
            "state":"MERGED","isDraft":false,"headRefName":"feature","baseRefName":"main",
            "author":{"login":"example","avatarUrl":"https://avatars.githubusercontent.com/u/1?s=96&v=4"},"reviewDecision":"APPROVED","comments":{"totalCount":2},
            "labels":{"totalCount":0,"nodes":[]},"commits":{"nodes":[{"commit":{"statusCheckRollup":{
                "contexts":{"totalCount":3,"nodes":[
                    {"__typename":"CheckRun","name":"build","status":"COMPLETED","conclusion":"SUCCESS"},
                    {"__typename":"CheckRun","name":"lint","status":"COMPLETED","conclusion":"SKIPPED"},
                    {"__typename":"StatusContext","context":"external","state":"PENDING"}
                ]}
            }}}]}
        }
        """);

    private static JsonNode User() =>
        Node("""{"id":1,"login":"example","name":"Example","avatar_url":"https://avatars.githubusercontent.com/u/1?v=4"}""");

    private static JsonNode CalendarValue() => Node("""
        {"totalContributions":2,"weeks":[{"contributionDays":[
            {"date":"2026-01-01","weekday":4,"contributionCount":2,"contributionLevel":"FIRST_QUARTILE"},
            {"date":"2026-01-02","weekday":5,"contributionCount":0,"contributionLevel":"NONE"}
        ]}]}
        """);

    private static JsonNode Api(string endpoint, string? query)
    {
        if (endpoint == "user") return User();
        if (endpoint == "copilot_internal/user")
            return Node("""
                {"login":"example","copilot_plan":"individual","token_based_billing":true,
                    "quota_snapshots":{"premium_interactions":{"unlimited":false,"percent_remaining":50.4}}}
                """);
        if (endpoint == "graphql")
        {
            var data = new JsonObject { ["viewer"] = new JsonObject { ["login"] = "example" } };
            if (query!.Contains("contributionsCollection"))
                data["viewer"]!["contributionsCollection"] = new JsonObject { ["contributionCalendar"] = CalendarValue() };
            else
                data["search"] = new JsonObject { ["nodes"] = new JsonArray() };
            return new JsonObject { ["data"] = data };
        }
        return new JsonArray();
    }

    private static GitHubRequest Fake(Func<string, string?, JsonNode> api) =>
        (endpoint, query, _) => Task.FromResult(Element(api(endpoint, query)));

    private static GitHubSnapshot Refresh(GitHubRequest request, GitHubSnapshot previous, TimeProvider clock,
        GitHubRefreshReason reason = GitHubRefreshReason.Manual, Action<GitHubSnapshot>? update = null) =>
        GitHubData.RefreshAsync(request, previous, reason, update, clock).GetAwaiter().GetResult();

    private static GitHubSnapshot AddPull(GitHubSnapshot snapshot) => snapshot with
    {
        Pulls = snapshot.Pulls with { Value = [.. snapshot.Pulls.Value!, GitHubData.ParsePullRequest(Element(Pull()))] }
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return true;
        }
        return false;
    }

    private static void ExpectInvalid(Action action, string message) => ExpectFailure(GitHubFailure.InvalidResponse, action, message);

    private static void ExpectFailure(GitHubFailure expected, Action action, string message)
    {
        try
        {
            action();
        }
        catch (GitHubException exception) when (exception.Failure == expected)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
