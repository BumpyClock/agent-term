using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgentTerm_App.GitHub;

internal enum GitHubFailure
{
    None,
    MissingCli,
    Request,
    Timeout,
    InvalidResponse,
    AccountChanged
}

// Messages never include response or stderr text, which can contain secrets.
internal sealed class GitHubException(GitHubFailure failure) : Exception($"GitHub dashboard failure: {failure}.")
{
    public GitHubFailure Failure { get; } = failure;
}

internal delegate Task<JsonElement> GitHubRequest(string endpoint, string? query, CancellationToken cancellationToken);

internal enum GitHubRefreshReason
{
    Manual,
    Automatic
}

internal sealed record GitHubCheck(string Name, string State);

internal sealed record GitHubItem
{
    public string Title { get; init; } = "";
    public string Repository { get; init; } = "";
    public string Url { get; init; } = "";
    public string UpdatedAt { get; init; } = "";
    public string Description { get; init; } = "";
    public string ActivityKind { get; init; } = "";
    public string State { get; init; } = "";
    public string Author { get; init; } = "";
    public string AvatarUrl { get; init; } = "";
    public string Head { get; init; } = "";
    public string Base { get; init; } = "";
    public string Review { get; init; } = "";
    public IReadOnlyList<string> Labels { get; init; } = [];
    public IReadOnlyList<GitHubCheck> Checks { get; init; } = [];
    public uint Number { get; init; }
    public uint Comments { get; init; }
    public uint LabelCount { get; init; }
    public uint CheckCount { get; init; }
    public uint Events { get; init; }
}

internal sealed record GitHubDay(string Date, uint Weekday, uint Count, uint Level, uint Week);

internal sealed record GitHubCalendar(uint Total, IReadOnlyList<GitHubDay> Days);

internal sealed record GitHubQuota
{
    public string Kind { get; init; } = "";
    public bool Credits { get; init; }
    public bool Unlimited { get; init; }
    public bool Included { get; init; } = true;
    public double? Used { get; init; }
    public string Reset { get; init; } = "";
}

internal sealed record GitHubUsage(string Plan, IReadOnlyList<GitHubQuota> Quotas);

internal sealed record GitHubSection<T>(T? Value, DateTimeOffset Updated, GitHubFailure Failure) where T : class
{
    public static GitHubSection<T> Empty { get; } = new(null, DateTimeOffset.UnixEpoch, GitHubFailure.None);
}

internal sealed record GitHubSnapshot
{
    public static GitHubSnapshot Empty { get; } = new();

    public string Id { get; init; } = "";
    public string Login { get; init; } = "";
    public string Name { get; init; } = "";
    public string AvatarUrl { get; init; } = "";
    public GitHubSection<IReadOnlyList<GitHubItem>> Activity { get; init; } = GitHubSection<IReadOnlyList<GitHubItem>>.Empty;
    public GitHubSection<IReadOnlyList<GitHubItem>> Pulls { get; init; } = GitHubSection<IReadOnlyList<GitHubItem>>.Empty;
    public GitHubSection<IReadOnlyList<GitHubItem>> Reviews { get; init; } = GitHubSection<IReadOnlyList<GitHubItem>>.Empty;
    public GitHubSection<IReadOnlyList<GitHubItem>> Repos { get; init; } = GitHubSection<IReadOnlyList<GitHubItem>>.Empty;
    public GitHubSection<GitHubCalendar> Calendar { get; init; } = GitHubSection<GitHubCalendar>.Empty;
    public GitHubSection<GitHubUsage> Usage { get; init; } = GitHubSection<GitHubUsage>.Empty;
}
