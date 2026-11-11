using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static AgentTerm_App.GitHub.GitHubJson;

namespace AgentTerm_App.GitHub;

internal static class GitHubData
{
    public static readonly TimeSpan SectionLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan ListFreshness = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SlowFreshness = TimeSpan.FromMinutes(15);
    internal static readonly string[] ContributionLevels = ["NONE", "FIRST_QUARTILE", "SECOND_QUARTILE", "THIRD_QUARTILE", "FOURTH_QUARTILE"];

    private const string PullFields =
        "number title url updatedAt state isDraft headRefName baseRefName reviewDecision " +
        "repository { nameWithOwner } author { login avatarUrl(size:96) } comments { totalCount } " +
        "labels(first:10) { totalCount nodes { name } } " +
        "commits(last:1) { nodes { commit { statusCheckRollup { contexts(first:100) { " +
        "totalCount nodes { __typename ... on CheckRun { name status conclusion } " +
        "... on StatusContext { context state } } } } } } } ";

    private const string CalendarQuery =
        "query { viewer { login contributionsCollection { contributionCalendar { totalContributions weeks { contributionDays { date weekday contributionCount contributionLevel } } } } } }";

    private const string ReposEndpoint =
        "user/repos?sort=pushed&direction=desc&per_page=30&affiliation=owner,collaborator,organization_member";

    private static readonly string[] QuotaKinds = ["premium_interactions", "chat", "completions"];

    public static bool IsGitHubUrl(string url) =>
        url.StartsWith("https://github.com/", StringComparison.Ordinal) && IsUrlText(url);

    public static bool IsGitHubAvatarUrl(string url) =>
        Encoding.UTF8.GetByteCount(url) <= 2048 &&
        (url.StartsWith("https://avatars.githubusercontent.com/", StringComparison.Ordinal) ||
         url.StartsWith("https://avatars.githubusercontent.com:443/", StringComparison.Ordinal)) &&
        IsUrlText(url);

    private static bool IsUrlText(string url) => !url.Any(c => c <= ' ' || c == '\\' || c == '\x7f') && HasValidEscapes(url);

    // The Windows Runtime Uri rejects a '%' without two hex digits, while System.Uri accepts it.
    private static bool HasValidEscapes(string url)
    {
        for (var index = url.IndexOf('%'); index >= 0; index = url.IndexOf('%', index + 1))
            if (index + 2 >= url.Length || !char.IsAsciiHexDigit(url[index + 1]) || !char.IsAsciiHexDigit(url[index + 2])) return false;
        return true;
    }

    internal static bool IsLogin(string text) =>
        text.Length is > 0 and <= 100 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static GitHubItem ParsePullRequest(JsonElement value)
    {
        var title = Text(value, "title");
        var repository = Text(Get(value, "repository"), "nameWithOwner");
        var url = Url(value, "url");
        var number = Count(value, "number");
        Require(number > 0 && url == $"https://github.com/{repository}/pull/{number}");
        var updatedAt = Text(value, "updatedAt");
        var state = Text(value, "state");
        Require(state is "OPEN" or "CLOSED" or "MERGED");
        Require(IsBoolean(Get(value, "isDraft")));
        if (state == "OPEN" && Flag(value, "isDraft", false)) state = "DRAFT";
        var authorValue = Get(value, "author");
        var author = IsNull(authorValue) ? "" : Text(authorValue, "login");
        var avatarUrl = "";
        if (!IsNull(authorValue))
        {
            avatarUrl = Text(authorValue, "avatarUrl", true);
            Require(avatarUrl.Length == 0 || IsGitHubAvatarUrl(avatarUrl));
        }
        var head = Text(value, "headRefName");
        var @base = Text(value, "baseRefName");
        var review = Text(value, "reviewDecision", true);
        var comments = Count(Get(value, "comments"), "totalCount");
        var labelsValue = Get(value, "labels");
        var labelCount = Count(labelsValue, "totalCount");
        var labelNodes = Get(labelsValue, "nodes");
        RequireArray(labelNodes, 10);
        Require(labelNodes.GetArrayLength() == Math.Min(labelCount, 10u));
        var labels = labelNodes.EnumerateArray().Select(label => Text(label, "name")).ToArray();
        var commits = Get(Get(value, "commits"), "nodes");
        RequireArray(commits, 1);
        uint checkCount = 0;
        var checks = new List<GitHubCheck>();
        if (commits.GetArrayLength() > 0 && !IsNull(Get(Get(commits[0], "commit"), "statusCheckRollup")))
        {
            var contexts = Get(Get(Get(commits[0], "commit"), "statusCheckRollup"), "contexts");
            checkCount = Count(contexts, "totalCount");
            var nodes = Get(contexts, "nodes");
            RequireArray(nodes, 100);
            Require(nodes.GetArrayLength() == Math.Min(checkCount, 100u));
            foreach (var check in nodes.EnumerateArray())
            {
                var type = Text(check, "__typename");
                if (type == "CheckRun")
                {
                    var checkState = Text(check, "status");
                    if (checkState == "COMPLETED") checkState = Text(check, "conclusion", true);
                    checks.Add(new GitHubCheck(Text(check, "name"), checkState.Length == 0 ? "UNKNOWN" : checkState));
                }
                else
                {
                    Require(type == "StatusContext");
                    checks.Add(new GitHubCheck(Text(check, "context"), Text(check, "state")));
                }
            }
        }
        return new GitHubItem
        {
            Title = title,
            Repository = repository,
            Url = url,
            Number = number,
            UpdatedAt = updatedAt,
            State = state,
            Author = author,
            AvatarUrl = avatarUrl,
            Head = head,
            Base = @base,
            Review = review,
            Comments = comments,
            LabelCount = labelCount,
            Labels = labels,
            CheckCount = checkCount,
            Checks = checks.ToArray()
        };
    }

    // GitHub dates follow the viewer's time zone, so a day may run up to one day ahead of UTC.
    public static GitHubCalendar ParseCalendar(JsonElement value, DateTimeOffset now)
    {
        var total = Count(value, "totalContributions");
        var weeks = Get(value, "weeks");
        RequireArray(weeks, 54);
        Require(weeks.GetArrayLength() > 0);
        var latest = DateOnly.FromDateTime(now.UtcDateTime).AddDays(1);
        var days = new List<GitHubDay>();
        ulong sum = 0;
        uint weekIndex = 0;
        DateOnly? previous = null;
        foreach (var week in weeks.EnumerateArray())
        {
            var contributionDays = Get(week, "contributionDays");
            RequireArray(contributionDays, 7);
            Require(contributionDays.GetArrayLength() > 0);
            Require(weekIndex == 0 || Count(contributionDays[0], "weekday") == 0);
            foreach (var valueDay in contributionDays.EnumerateArray())
            {
                var date = Text(valueDay, "date");
                var weekday = Count(valueDay, "weekday");
                var count = Count(valueDay, "contributionCount");
                var level = System.Array.IndexOf(ContributionLevels, Text(valueDay, "contributionLevel"));
                Require(level >= 0);
                Require(weekday < 7 && date.Length == 10 && date[4] == '-' && date[7] == '-' && (count == 0) == (level == 0));
                Require(date.Where((c, index) => index is not (4 or 7)).All(char.IsAsciiDigit));
                Require(DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var current));
                Require((uint)current.DayOfWeek == weekday && (previous is null || current == previous.Value.AddDays(1)) && current <= latest);
                previous = current;
                sum += count;
                days.Add(new GitHubDay(date, weekday, count, (uint)level, weekIndex));
            }
            Require(weekIndex + 1 == weeks.GetArrayLength() || days[^1].Weekday == 6);
            weekIndex++;
        }
        Require(sum == total);
        return new GitHubCalendar(total, days.ToArray());
    }

    public static GitHubUsage ParseUsage(JsonElement value, string login)
    {
        Account(Text(value, "login"), login);
        var plan = Text(value, "copilot_plan");
        var quotas = new List<GitHubQuota>();
        foreach (var kind in QuotaKinds)
        {
            var entry = Get(Get(value, "quota_snapshots"), kind);
            if (IsNull(entry)) continue;
            Require(IsBoolean(Get(entry, "unlimited")));
            var unlimited = Flag(entry, "unlimited", false);
            var included = Flag(entry, "has_quota", true);
            var fallbackCredits = Flag(value, "token_based_billing", false);
            var credits = Flag(entry, "token_based_billing", fallbackCredits);
            double? used = null;
            if (included && !unlimited)
            {
                Require(IsNumber(Get(entry, "percent_remaining"), out var remaining));
                Require(double.IsFinite(remaining) && remaining >= 0 && remaining <= 100);
                used = 100.0 - remaining;
            }
            var reset = Text(value, "quota_reset_date_utc", true);
            if (reset.Length == 0) reset = Text(value, "quota_reset_date", true);
            var resetAt = Get(entry, "quota_reset_at");
            if (!IsNull(resetAt))
            {
                Require(IsInt64(resetAt, out var seconds) && seconds >= 0 && seconds <= 253402300799L);
                if (seconds > 0)
                    reset = DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            }
            quotas.Add(new GitHubQuota { Kind = kind, Unlimited = unlimited, Included = included, Credits = credits, Used = used, Reset = reset });
        }
        Require(quotas.Count > 0);
        return new GitHubUsage(plan, quotas.ToArray());
    }

    public static GitHubSnapshot ParseIdentity(JsonElement user)
    {
        Require(user.ValueKind == JsonValueKind.Object);
        Require(IsUInt64(Get(user, "id"), out var id));
        var login = Text(user, "login");
        Require(IsLogin(login));
        var name = Text(user, "name", true);
        var avatarUrl = Text(user, "avatar_url", true);
        Require(avatarUrl.Length == 0 || IsGitHubAvatarUrl(avatarUrl));
        return new GitHubSnapshot { Id = id.ToString(CultureInfo.InvariantCulture), Login = login, Name = name, AvatarUrl = avatarUrl };
    }

    public static bool IsSectionExpired(DateTimeOffset updated, DateTimeOffset now) =>
        updated > now || now - updated >= SectionLifetime;

    public static GitHubSnapshot ExpireSections(GitHubSnapshot snapshot, DateTimeOffset now) => snapshot with
    {
        Activity = Expire(snapshot.Activity, now),
        Pulls = Expire(snapshot.Pulls, now),
        Reviews = Expire(snapshot.Reviews, now),
        Repos = Expire(snapshot.Repos, now),
        Calendar = Expire(snapshot.Calendar, now),
        Usage = Expire(snapshot.Usage, now)
    };

    private static GitHubSection<T> Expire<T>(GitHubSection<T> section, DateTimeOffset now) where T : class =>
        section.Value is not null && IsSectionExpired(section.Updated, now) ? section with { Value = null } : section;

    /// <summary>
    /// Loads the six dashboard sections concurrently between two identity checks.
    /// <paramref name="update"/> receives each partial snapshot. It may run on any thread, usually a thread-pool
    /// thread, but never concurrently with itself. It runs under a lock, so it must return quickly and must not
    /// wait synchronously for the UI thread. Sections that completed before an exception may already have been
    /// delivered, so discard partial data when this method throws.
    /// </summary>
    /// <exception cref="GitHubException">The identity checks failed, or a section saw a different account (AccountChanged).</exception>
    /// <exception cref="OperationCanceledException">The request delegate observed cancellation.</exception>
    public static async Task<GitHubSnapshot> RefreshAsync(GitHubRequest request, GitHubSnapshot previous,
        GitHubRefreshReason reason = GitHubRefreshReason.Manual, Action<GitHubSnapshot>? update = null,
        TimeProvider? clock = null, CancellationToken cancellationToken = default)
    {
        clock ??= TimeProvider.System;
        var result = ParseIdentity(await request("user", null, cancellationToken).ConfigureAwait(false));
        var old = ExpireSections(previous.Id == result.Id && previous.Login == result.Login ? previous : GitHubSnapshot.Empty,
            clock.GetUtcNow());
        result = result with
        {
            Activity = old.Activity,
            Pulls = old.Pulls,
            Reviews = old.Reviews,
            Repos = old.Repos,
            Calendar = old.Calendar,
            Usage = old.Usage
        };
        update?.Invoke(result);
        var gate = new Lock();
        var login = result.Login;

        Task Fetch<T>(GitHubSection<T> section, TimeSpan freshness, Func<Task<T>> load,
            Func<GitHubSnapshot, GitHubSection<T>, GitHubSnapshot> store) where T : class => Task.Run(async () =>
        {
            var age = clock.GetUtcNow() - section.Updated;
            var fresh = reason == GitHubRefreshReason.Automatic && section.Value is not null && section.Failure == GitHubFailure.None &&
                age >= TimeSpan.Zero && age < freshness;
            var loaded = fresh ? section : await Load(section, load, clock).ConfigureAwait(false);
            lock (gate)
            {
                result = store(result, loaded);
                update?.Invoke(result);
            }
        });

        Task[] workers =
        [
            Fetch(old.Activity, ListFreshness, () => ActivityAsync(request, login, cancellationToken), (s, v) => s with { Activity = v }),
            Fetch(old.Pulls, ListFreshness, () => PullsAsync(request, login, false, cancellationToken), (s, v) => s with { Pulls = v }),
            Fetch(old.Reviews, ListFreshness, () => PullsAsync(request, login, true, cancellationToken), (s, v) => s with { Reviews = v }),
            Fetch(old.Repos, SlowFreshness, () => ReposAsync(request, cancellationToken), (s, v) => s with { Repos = v }),
            Fetch(old.Calendar, SlowFreshness, async () =>
            {
                var response = await request("graphql", CalendarQuery, cancellationToken).ConfigureAwait(false);
                return ParseCalendar(Get(Get(Get(Graph(response, login), "viewer"), "contributionsCollection"), "contributionCalendar"),
                    clock.GetUtcNow());
            }, (s, v) => s with { Calendar = v }),
            Fetch(old.Usage, ListFreshness, async () =>
                ParseUsage(await request("copilot_internal/user", null, cancellationToken).ConfigureAwait(false), login),
                (s, v) => s with { Usage = v })
        ];
        await Task.WhenAll(workers).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        foreach (var worker in workers) await worker.ConfigureAwait(false);

        var finalUser = await request("user", null, cancellationToken).ConfigureAwait(false);
        Require(finalUser.ValueKind == JsonValueKind.Object);
        Require(IsUInt64(Get(finalUser, "id"), out var finalId));
        Account(finalId.ToString(CultureInfo.InvariantCulture), result.Id);
        Account(Text(finalUser, "login"), result.Login);
        return result;
    }

    private static async Task<GitHubSection<T>> Load<T>(GitHubSection<T> previous, Func<Task<T>> fetch, TimeProvider clock)
        where T : class
    {
        try
        {
            var value = await fetch().ConfigureAwait(false);
            return new GitHubSection<T>(value, clock.GetUtcNow(), GitHubFailure.None);
        }
        catch (GitHubException error) when (error.Failure != GitHubFailure.AccountChanged)
        {
            return previous with
            {
                Value = IsSectionExpired(previous.Updated, clock.GetUtcNow()) ? null : previous.Value,
                Failure = error.Failure
            };
        }
    }

    private static void Account(string actual, string expected)
    {
        if (actual != expected) throw new GitHubException(GitHubFailure.AccountChanged);
    }

    private static JsonElement Graph(JsonElement value, string login, bool allowErrors = false)
    {
        var viewer = Get(Get(value, "data"), "viewer");
        if (!IsNull(Get(viewer, "login"))) Account(Text(viewer, "login"), login);
        var errors = Get(value, "errors");
        Require(IsNull(errors) || errors.ValueKind == JsonValueKind.Array && (allowErrors || errors.GetArrayLength() == 0));
        Account(Text(viewer, "login"), login);
        return Get(value, "data");
    }

    private static string Url(JsonElement value, string key)
    {
        var url = Text(value, key);
        Require(IsGitHubUrl(url));
        return url;
    }

    private static async Task<IReadOnlyList<GitHubItem>> PullsAsync(GitHubRequest request, string login, bool reviews,
        CancellationToken cancellationToken)
    {
        var filter = reviews ? "is:pr is:open review-requested:" : "is:pr author:";
        var query = "query { viewer { login } search(type:ISSUE,first:30,query:\"" + filter + login +
            " sort:updated-desc\") { nodes { ... on PullRequest { " + PullFields + " } } } }";
        var response = await request("graphql", query, cancellationToken).ConfigureAwait(false);
        var nodes = Get(Get(Graph(response, login), "search"), "nodes");
        RequireArray(nodes, 30);
        return nodes.EnumerateArray().Select(ParsePullRequest).ToArray();
    }

    private static async Task<IReadOnlyList<GitHubItem>> ReposAsync(GitHubRequest request, CancellationToken cancellationToken)
    {
        var repos = await request(ReposEndpoint, null, cancellationToken).ConfigureAwait(false);
        RequireArray(repos, 30);
        return repos.EnumerateArray().Select(repo => new GitHubItem
        {
            Title = Text(repo, "full_name"),
            Url = Url(repo, "html_url"),
            Description = Text(repo, "description", true),
            UpdatedAt = Text(repo, "pushed_at", true)
        }).ToArray();
    }

    private static async Task<IReadOnlyList<GitHubItem>> ActivityAsync(GitHubRequest request, string login,
        CancellationToken cancellationToken)
    {
        var events = await request("users/" + login + "/events?per_page=30", null, cancellationToken).ConfigureAwait(false);
        RequireArray(events, 30);
        var items = new List<GitHubItem>();
        var pulls = new Dictionary<(string Repository, uint Number), int>();
        foreach (var activity in events.EnumerateArray())
        {
            var repository = Text(Get(activity, "repo"), "name");
            var slash = repository.IndexOf('/');
            Require(slash >= 0 && IsLogin(repository[..slash]) && IsLogin(repository[(slash + 1)..]));
            var item = new GitHubItem
            {
                Repository = repository,
                Url = "https://github.com/" + repository,
                Title = repository,
                UpdatedAt = Text(activity, "created_at")
            };
            var type = Text(activity, "type");
            var payload = Get(activity, "payload");
            item = item with { ActivityKind = type };
            if (type is "PullRequestEvent" or "PullRequestReviewEvent" or "PullRequestReviewCommentEvent")
            {
                var pullRequest = Get(payload, "pull_request");
                var number = IsUInt32(Get(pullRequest, "number"), out _) ? Count(pullRequest, "number") : Count(payload, "number");
                Require(number > 0);
                if (pulls.TryGetValue((repository, number), out var existing))
                {
                    items[existing] = items[existing] with { Events = items[existing].Events + 1 };
                    continue;
                }
                item = item with
                {
                    Number = number,
                    Events = 1,
                    Url = item.Url + "/pull/" + number.ToString(CultureInfo.InvariantCulture),
                    Description = Text(payload, "action", true)
                };
                pulls.Add((repository, number), items.Count);
            }
            else if (type is "PushEvent" or "CreateEvent" or "DeleteEvent")
            {
                item = item with { Description = Text(payload, "ref", true) };
            }
            else if (type is "IssuesEvent" or "IssueCommentEvent")
            {
                var issue = Get(payload, "issue");
                item = item with { Title = Text(issue, "title"), Url = Url(issue, "html_url"), Description = Text(payload, "action", true) };
            }
            items.Add(item);
        }
        if (pulls.Count > 0) await HydrateAsync(request, login, items, pulls, cancellationToken).ConfigureAwait(false);
        return items.ToArray();
    }

    private static async Task HydrateAsync(GitHubRequest request, string login, List<GitHubItem> items,
        Dictionary<(string Repository, uint Number), int> pulls, CancellationToken cancellationToken)
    {
        var query = new StringBuilder("query { viewer { login } ");
        foreach (var ((repository, number), index) in pulls)
        {
            var slash = repository.IndexOf('/');
            query.Append(CultureInfo.InvariantCulture,
                $"r{index}:repository(owner:\"{repository[..slash]}\",name:\"{repository[(slash + 1)..]}\") {{ pullRequest(number:{number}) {{ {PullFields} }} }} ");
        }
        var response = await request("graphql", query.Append('}').ToString(), cancellationToken).ConfigureAwait(false);
        var data = Graph(response, login, allowErrors: true);
        foreach (var index in pulls.Values)
        {
            var pull = Get(Get(data, "r" + index.ToString(CultureInfo.InvariantCulture)), "pullRequest");
            if (IsNull(pull)) continue;
            var original = items[index];
            items[index] = ParsePullRequest(pull) with
            {
                Events = original.Events,
                UpdatedAt = original.UpdatedAt,
                Description = original.Description,
                ActivityKind = original.ActivityKind
            };
        }
    }
}
