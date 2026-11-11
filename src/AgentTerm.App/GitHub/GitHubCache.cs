using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static AgentTerm_App.GitHub.GitHubJson;

namespace AgentTerm_App.GitHub;

internal static class GitHubCache
{
    private static readonly string[] TextFields =
        ["title", "repository", "url", "updatedAt", "description", "activityKind", "state", "author", "head", "base", "review"];
    private static readonly string[] NumberFields = ["number", "comments", "labelCount", "checkCount", "events"];
    private static readonly string[] QuotaKinds = ["premium_interactions", "chat", "completions"];

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentTerm", "github-dashboard.json");

    public static string Serialize(GitHubSnapshot snapshot) => new JsonObject
    {
        ["version"] = 1,
        ["host"] = "github.com",
        ["id"] = snapshot.Id,
        ["login"] = snapshot.Login,
        ["name"] = snapshot.Name,
        ["avatarUrl"] = snapshot.AvatarUrl,
        ["activity"] = EncodeSection(snapshot.Activity, Encode),
        ["pulls"] = EncodeSection(snapshot.Pulls, Encode),
        ["reviews"] = EncodeSection(snapshot.Reviews, Encode),
        ["repos"] = EncodeSection(snapshot.Repos, Encode),
        ["calendar"] = EncodeSection(snapshot.Calendar, Encode),
        ["usage"] = EncodeSection(snapshot.Usage, Encode)
    }.ToJsonString();

    public static GitHubSnapshot Deserialize(string json, TimeProvider? clock = null) =>
        Decode(Parse(json), (clock ?? TimeProvider.System).GetUtcNow());

    public static GitHubSnapshot Read(string path, TimeProvider? clock = null)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return GitHubSnapshot.Empty;
        }
        using (stream)
        {
            Require(stream.Length > 0 && stream.Length <= MaximumBytes);
            var bytes = new byte[stream.Length];
            Require(stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false) == bytes.Length);
            return Decode(Parse(bytes), (clock ?? TimeProvider.System).GetUtcNow());
        }
    }

    public static void Write(string path, GitHubSnapshot snapshot)
    {
        var bytes = Encoding.UTF8.GetBytes(Serialize(snapshot));
        Require(bytes.Length <= MaximumBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var staging = path + Guid.NewGuid().ToString("B") + ".tmp";
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(staging);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static JsonNode? EncodeSection<T>(GitHubSection<T> section, Func<T, JsonNode> encode) where T : class =>
        section.Value is null ? null : new JsonObject
        {
            ["updated"] = section.Updated.ToUnixTimeMilliseconds(),
            ["value"] = encode(section.Value),
            ["failed"] = section.Failure != GitHubFailure.None
        };

    private static JsonNode Encode(IReadOnlyList<GitHubItem> items) => new JsonArray(items.Select(item =>
    {
        var value = new JsonObject();
        string[] texts = [item.Title, item.Repository, item.Url, item.UpdatedAt, item.Description, item.ActivityKind, item.State,
            item.Author, item.Head, item.Base, item.Review];
        uint[] numbers = [item.Number, item.Comments, item.LabelCount, item.CheckCount, item.Events];
        for (var index = 0; index < TextFields.Length; index++) value[TextFields[index]] = texts[index];
        for (var index = 0; index < NumberFields.Length; index++) value[NumberFields[index]] = numbers[index];
        value["avatarUrl"] = item.AvatarUrl;
        value["labels"] = new JsonArray(item.Labels.Select(label => (JsonNode?)label).ToArray());
        value["checks"] = new JsonArray(item.Checks.Select(check => (JsonNode?)new JsonObject
        {
            ["name"] = check.Name,
            ["state"] = check.State
        }).ToArray());
        return (JsonNode?)value;
    }).ToArray());

    private static JsonNode Encode(GitHubCalendar calendar)
    {
        var weeks = new JsonArray();
        foreach (var day in calendar.Days)
        {
            Require(day.Week < 54 && day.Level < GitHubData.ContributionLevels.Length);
            while (weeks.Count <= day.Week) weeks.Add(null);
            var week = weeks[(int)day.Week] ??= new JsonObject { ["contributionDays"] = new JsonArray() };
            week["contributionDays"]!.AsArray().Add(new JsonObject
            {
                ["date"] = day.Date,
                ["weekday"] = day.Weekday,
                ["contributionCount"] = day.Count,
                ["contributionLevel"] = GitHubData.ContributionLevels[day.Level]
            });
        }
        return new JsonObject { ["totalContributions"] = calendar.Total, ["weeks"] = weeks };
    }

    private static JsonNode Encode(GitHubUsage usage) => new JsonObject
    {
        ["plan"] = usage.Plan,
        ["quotas"] = new JsonArray(usage.Quotas.Select(quota => (JsonNode?)new JsonObject
        {
            ["kind"] = quota.Kind,
            ["credits"] = quota.Credits,
            ["unlimited"] = quota.Unlimited,
            ["included"] = quota.Included,
            ["used"] = quota.Used,
            ["reset"] = quota.Reset
        }).ToArray())
    };

    private static GitHubSnapshot Decode(JsonElement value, DateTimeOffset now)
    {
        Require(value.ValueKind == JsonValueKind.Object && IsInt64(Get(value, "version"), out var version) && version == 1);
        var host = Get(value, "host");
        Require(host.ValueKind == JsonValueKind.String && host.ValueEquals("github.com"));
        var id = ReadString(Get(value, "id"));
        var login = ReadString(Get(value, "login"));
        var name = ReadString(Get(value, "name"));
        var avatarUrl = "";
        // Older snapshots predate avatar support.
        if (!IsNull(Get(value, "avatarUrl")))
        {
            avatarUrl = ReadString(Get(value, "avatarUrl"));
            Require(avatarUrl.Length == 0 || GitHubData.IsGitHubAvatarUrl(avatarUrl));
        }
        Require(id.Length is > 0 and <= 20 && id.All(char.IsAsciiDigit) && GitHubData.IsLogin(login));
        return new GitHubSnapshot
        {
            Id = id,
            Login = login,
            Name = name,
            AvatarUrl = avatarUrl,
            Activity = DecodeSection(Get(value, "activity"), DecodeItems, now),
            Pulls = DecodeSection(Get(value, "pulls"), DecodeItems, now),
            Reviews = DecodeSection(Get(value, "reviews"), DecodeItems, now),
            Repos = DecodeSection(Get(value, "repos"), DecodeItems, now),
            Calendar = DecodeSection(Get(value, "calendar"), element => GitHubData.ParseCalendar(element, now), now),
            Usage = DecodeSection(Get(value, "usage"), DecodeUsage, now)
        };
    }

    private static GitHubSection<T> DecodeSection<T>(JsonElement value, Func<JsonElement, T> decode, DateTimeOffset now) where T : class
    {
        if (IsNull(value)) return GitHubSection<T>.Empty;
        Require(value.ValueKind == JsonValueKind.Object);
        Require(IsInt64(Get(value, "updated"), out var timestamp) &&
            IsBoolean(Get(value, "failed")));
        Require(timestamp >= 0 && timestamp <= now.ToUnixTimeMilliseconds());
        var updated = DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
        if (GitHubData.IsSectionExpired(updated, now)) return new GitHubSection<T>(null, updated, GitHubFailure.None);
        var data = decode(Get(value, "value"));
        return new GitHubSection<T>(data, updated, Get(value, "failed").GetBoolean() ? GitHubFailure.Request : GitHubFailure.None);
    }

    private static IReadOnlyList<GitHubItem> DecodeItems(JsonElement array)
    {
        RequireArray(array, 30);
        return array.EnumerateArray().Select(value =>
        {
            Require(value.ValueKind == JsonValueKind.Object);
            var texts = TextFields.Select(key => ReadString(Get(value, key))).ToArray();
            var numbers = NumberFields.Select(key =>
            {
                Require(IsUInt32(Get(value, key), out var number));
                return number;
            }).ToArray();
            Require(GitHubData.IsGitHubUrl(texts[2]));
            var avatarUrl = "";
            if (!IsNull(Get(value, "avatarUrl")))
            {
                avatarUrl = ReadString(Get(value, "avatarUrl"));
                Require(avatarUrl.Length == 0 || GitHubData.IsGitHubAvatarUrl(avatarUrl));
            }
            var labels = Get(value, "labels");
            RequireArray(labels, 10);
            var checks = Get(value, "checks");
            RequireArray(checks, 100);
            var item = new GitHubItem
            {
                Title = texts[0],
                Repository = texts[1],
                Url = texts[2],
                UpdatedAt = texts[3],
                Description = texts[4],
                ActivityKind = texts[5],
                State = texts[6],
                Author = texts[7],
                Head = texts[8],
                Base = texts[9],
                Review = texts[10],
                Number = numbers[0],
                Comments = numbers[1],
                LabelCount = numbers[2],
                CheckCount = numbers[3],
                Events = numbers[4],
                AvatarUrl = avatarUrl,
                Labels = labels.EnumerateArray().Select(ReadString).ToArray(),
                Checks = checks.EnumerateArray()
                    .Select(check => new GitHubCheck(ReadString(Get(check, "name")), ReadString(Get(check, "state")))).ToArray()
            };
            Require(item.Checks.Count <= item.CheckCount && item.Labels.Count <= item.LabelCount);
            return item;
        }).ToArray();
    }

    private static GitHubUsage DecodeUsage(JsonElement value)
    {
        var plan = ReadString(Get(value, "plan"));
        var quotas = Get(value, "quotas");
        Require(quotas.ValueKind == JsonValueKind.Array && quotas.GetArrayLength() is > 0 and <= 3);
        return new GitHubUsage(plan, quotas.EnumerateArray().Select(entry =>
        {
            var kind = ReadString(Get(entry, "kind"));
            Require(QuotaKinds.Contains(kind));
            Require(IsBoolean(Get(entry, "credits")) && IsBoolean(Get(entry, "unlimited")) && IsBoolean(Get(entry, "included")));
            var quota = new GitHubQuota
            {
                Kind = kind,
                Credits = Get(entry, "credits").GetBoolean(),
                Unlimited = Get(entry, "unlimited").GetBoolean(),
                Included = Get(entry, "included").GetBoolean()
            };
            if (!quota.Unlimited && quota.Included)
            {
                Require(IsNumber(Get(entry, "used"), out var used) && used >= 0 && used <= 100);
                quota = quota with { Used = used };
            }
            else
            {
                Require(IsNull(Get(entry, "used")));
            }
            return quota with { Reset = ReadString(Get(entry, "reset")) };
        }).ToArray());
    }
}
