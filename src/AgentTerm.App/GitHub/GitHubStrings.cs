namespace AgentTerm_App.GitHub;

internal static class GitHubStrings
{
    public const string Dashboard = "GitHub dashboard";
    public const string ButtonToolTip = "GitHub activity, pull requests, and Copilot usage";
    public const string CloseDashboard = "Close GitHub dashboard";
    public const string Activity = "Activity";
    public const string MyPrs = "My PRs";
    public const string Reviews = "Reviews";
    public const string Repos = "Repos";
    public const string Sections = "Dashboard sections";
    public const string Refresh = "Refresh";
    public const string RefreshToolTip = "Refresh (F5)";
    public const string Refreshing = "Refreshing";
    public const string SavedUnverified = "Account verification pending - data may be saved or still refreshing.";
    public const string CacheWarning = "Live data loaded, but the local cache could not be saved.";
    public const string NotLoaded = "Contributions not loaded";
    public const string UsageNotLoaded = "Copilot usage not loaded";
    public const string ListNotLoaded = "Not loaded";
    public const string ListNotLoadedDetail = "Select Refresh to try again.";
    public const string AvatarUnavailable = "The GitHub avatar could not be loaded. Showing initials instead.";
    public const string AccountScope =
        "Account-wide snapshot, not the active terminal's repository. Lists show at most 30 entries; activity may be delayed.";
    public const string Loading = "Refreshing GitHub dashboard...";
    public const string Loaded = "GitHub dashboard updated.";
    public const string MissingCli =
        "GitHub CLI was not found on PATH. Install gh, sign in with gh auth login --hostname github.com, then restart AgentTerm.";
    public const string Timeout = "The GitHub request timed out. Check your connection and refresh.";
    public const string AccountChanged =
        "Your GitHub account changed during refresh. Previous data has been removed. Refresh to load the current account.";
    public const string InvalidResponse = "GitHub returned an unexpected response. Refresh to try again.";
    public const string RequestFailed =
        "Could not load GitHub data. Check your connection, gh auth status --hostname github.com, and repository permissions. Copilot usage may be unavailable for this account.";
    public const string Stale = "Stale data.";
    public const string Updated = "Updated";
    public const string Saved = "Saved";
    public const string Verifying = "verifying account";
    public const string Retry = "Retry";
    public const string Checking = "Checking...";
    public const string CalendarName = "Contribution calendar";
    public const string CalendarControlType = "calendar";
    public const string CalendarKeys = "Use arrow keys to inspect days, Home for the first day, and End for the latest day.";
    public const string Less = "Less";
    public const string More = "More";
    public const string Copilot = "Copilot";
    public const string AiCredits = "AI credits";
    public const string PremiumRequests = "Premium requests";
    public const string Chat = "Chat";
    public const string Completions = "Completions";
    public const string Unlimited = "Unlimited";
    public const string NotIncluded = "Not included";
    public const string Empty = "No items in this snapshot.";
    public const string EmptyActivity = "No recent activity";
    public const string EmptyPulls = "No pull requests";
    public const string EmptyReviews = "No reviews requested";
    public const string EmptyRepos = "No repositories";
    public const string Checks = "Checks passed:";
    public const string NoChecks = "No checks reported. This does not imply success.";
    public const string ChecksLimited = "Showing the first 100 checks. Total:";
    public const string LabelsLimited = "Showing the first 10 labels. Total:";
    public const string ViewOnGitHub = "View on GitHub";
    public const string Open = "Open";
    public const string Closed = "Closed";
    public const string Merged = "Merged";
    public const string Draft = "Draft";
    public const string Approved = "Approved";
    public const string ChangesRequested = "Changes requested";
    public const string ReviewRequired = "Review required";
    public const string Pushed = "Pushed commits";
    public const string DeletedRef = "Deleted a branch or tag";
    public const string CreatedRef = "Created a branch or tag";
    public const string PullActivity = "Pull request activity";
    public const string ReviewActivity = "Pull request review";
    public const string CommentActivity = "Comment activity";
    public const string IssueActivity = "Issue activity";
    public const string ReleaseActivity = "Release activity";
    public const string SignedOutTitle = "Sign in to GitHub CLI";
    public const string SignedOutBody = "The dashboard uses your GitHub CLI account. Run this in a terminal tab, then retry.";
    public const string SignInCommand = "gh auth login --hostname github.com";
    public const string MissingCliTitle = "GitHub CLI not found";
    public const string MissingCliBody = "Install the GitHub CLI, sign in, then restart AgentTerm so it can find gh on PATH.";
    public const string InstallCommand = "winget install --id GitHub.cli";
    public const string Copy = "Copy command";
    public const string Copied = "Copied";
}
