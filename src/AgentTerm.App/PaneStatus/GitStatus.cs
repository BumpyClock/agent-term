using System.Globalization;

namespace AgentTerm_App.PaneStatus;

internal enum GitQueryState { Ready, NoRepository, Unavailable, Failed, TimedOut }
internal enum GitBranchKind { Named, Unborn, Detached }
internal sealed record GitLineChanges(long Added, long Deleted, bool Binary);
internal sealed record GitSnapshot(string Branch, GitBranchKind Kind, int Ahead, int Behind,
    bool Staged, bool Unstaged, bool Untracked, bool Conflicts, GitLineChanges? Lines = null)
{
    internal string BranchLabel => Kind switch
    {
        GitBranchKind.Detached => "detached " + Branch[..Math.Min(8, Branch.Length)],
        GitBranchKind.Unborn => Branch + " (new)",
        _ => Branch,
    };
    internal string Summary => string.Join(", ", new[]
    {
        "Git " + BranchLabel,
        Ahead > 0 ? $"{Ahead} commits ahead" : null,
        Behind > 0 ? $"{Behind} commits behind" : null,
        Lines is { } lines ? $"{lines.Added} lines added, {lines.Deleted} lines deleted (tracked, net against {(Kind == GitBranchKind.Unborn ? "the empty tree" : "HEAD")})" : null,
        Lines?.Binary == true ? "binary changes" : null,
        Staged ? "staged changes" : null,
        Unstaged ? "unstaged changes" : null,
        Untracked ? "untracked files" : null,
        Conflicts ? "unresolved conflicts; line totals unavailable" : null,
        !Staged && !Unstaged && !Untracked && !Conflicts ? "clean" : null,
    }.Where(part => part is not null));
}
internal sealed record GitResult(GitQueryState State, GitSnapshot? Snapshot = null);

internal static class GitStatusParser
{
    internal static GitSnapshot ParseStatus(string output)
    {
        if (!output.EndsWith('\0')) throw new FormatException("Incomplete Git status.");
        string? oid = null, branch = null;
        var ahead = 0;
        var behind = 0;
        bool staged = false, unstaged = false, untracked = false, conflicts = false;
        var records = output.Split('\0');
        for (var index = 0; index < records.Length - 1; index++)
        {
            var record = records[index];
            if (record.StartsWith("# branch.oid ")) oid = record[13..];
            else if (record.StartsWith("# branch.head ")) branch = record[14..];
            else if (record.StartsWith("# branch.ab "))
            {
                var counts = record[12..].Split(' ');
                if (counts.Length != 2 || !counts[0].StartsWith('+') || !counts[1].StartsWith('-')) throw new FormatException("Invalid tracking counts.");
                ahead = int.Parse(counts[0][1..], NumberStyles.None, CultureInfo.InvariantCulture);
                behind = int.Parse(counts[1][1..], NumberStyles.None, CultureInfo.InvariantCulture);
            }
            else if (record.StartsWith("# ")) { }
            else if (record.StartsWith("1 ") || record.StartsWith("2 "))
            {
                if (record.Length < 5 || record[4] != ' ') throw new FormatException("Invalid change record.");
                staged |= record[2] != '.';
                unstaged |= record[3] != '.';
                if (record[0] == '2' && (++index >= records.Length - 1 || records[index].Length == 0)) throw new FormatException("Missing rename source.");
            }
            else if (record.StartsWith("u ")) conflicts = true;
            else if (record.StartsWith("? ")) untracked = true;
            else if (!record.StartsWith("! ")) throw new FormatException("Invalid Git record.");
        }
        if (string.IsNullOrEmpty(branch) || string.IsNullOrEmpty(oid) || branch.Any(char.IsControl)) throw new FormatException("Missing Git branch.");
        var kind = oid == "(initial)" ? GitBranchKind.Unborn : branch == "(detached)" ? GitBranchKind.Detached : GitBranchKind.Named;
        if (oid != "(initial)" && (oid.Length is not (40 or 64) || !oid.All(char.IsAsciiHexDigit))) throw new FormatException("Invalid Git object id.");
        return new(kind == GitBranchKind.Detached ? oid : branch, kind, ahead, behind, staged, unstaged, untracked, conflicts);
    }

    internal static GitLineChanges ParseLines(string output)
    {
        long added = 0, deleted = 0;
        var binary = false;
        if (output.Length > 0 && !output.EndsWith('\0')) throw new FormatException("Incomplete Git diff.");
        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Split('\t', 3);
            if (fields.Length != 3 || fields[2].Length == 0) throw new FormatException("Invalid Git diff.");
            if (fields[0] == "-" && fields[1] == "-") binary = true;
            else checked
            {
                added += long.Parse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture);
                deleted += long.Parse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture);
            }
        }
        return new(added, deleted, binary);
    }
}
