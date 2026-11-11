namespace AgentTerm.Terminal;

/// <summary>A filesystem directory reported by the shell, in its launch environment.</summary>
public sealed record ShellDirectory(string Path, string? WslDistribution = null)
{
    public static ShellDirectory? FromReport(string report, ShellProfile profile)
    {
        // Windows prompt tools also report literal, sometimes quoted, drive paths through OSC 7.
        // Do not URI-decode those paths: a directory named %20 contains a literal percent sign.
        var raw = report.Length >= 2 && report[0] == '"' && report[^1] == '"' ? report[1..^1] : report;
        if (!profile.Id.StartsWith("wsl:", StringComparison.Ordinal) && raw.Length >= 3 &&
            char.IsAsciiLetter(raw[0]) && raw[1] == ':' && raw[2] is '\\' or '/')
            return !raw.Any(char.IsControl) ? new(raw.Replace('/', '\\')) : null;
        if (!Uri.TryCreate(report, UriKind.Absolute, out var uri) || !uri.IsFile ||
            (!string.IsNullOrEmpty(uri.Host) && uri.Host != "localhost" &&
             !uri.Host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return null;
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (path.Length == 0 || path.Any(char.IsControl)) return null;
        if (profile.Id.StartsWith("wsl:", StringComparison.Ordinal))
            return path.StartsWith('/') ? new(path, profile.Id[4..]) : null;
        if (path.Length >= 4 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':')
            path = path[1..];
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\')
            return new(path.Replace('/', '\\'));
        return !OperatingSystem.IsWindows() && System.IO.Path.IsPathFullyQualified(path) ? new(path) : null;
    }

    public string DisplayPath
    {
        get
        {
            if (WslDistribution is not null) return Path;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
            if (home.Length > 0 && Path.Equals(home, StringComparison.OrdinalIgnoreCase)) return "~";
            return home.Length > 0 && Path.StartsWith(home + "\\", StringComparison.OrdinalIgnoreCase)
                ? "~" + Path[home.Length..] : Path;
        }
    }
}
