namespace AgentTerm_App.PaneStatus;

internal sealed record GitRepository(string WorkTree, string GitDirectory, string CommonDirectory)
{
    internal static GitRepository? Locate(string directory)
    {
        try
        {
            for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            {
                var dotGit = Path.Combine(current.FullName, ".git");
                string? gitDirectory = null;
                if (Directory.Exists(dotGit)) gitDirectory = dotGit;
                else if (File.Exists(dotGit) && File.ReadLines(dotGit).FirstOrDefault() is { } line && line.StartsWith("gitdir:", StringComparison.Ordinal))
                    gitDirectory = Path.GetFullPath(line[7..].Trim(), current.FullName);
                if (gitDirectory is null) continue;
                var commonFile = Path.Combine(gitDirectory, "commondir");
                var common = File.Exists(commonFile) ? Path.GetFullPath(File.ReadAllText(commonFile).Trim(), gitDirectory) : gitDirectory;
                return new(Trim(current.FullName), Trim(gitDirectory), Trim(common));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { }
        return null;
    }

    internal IDisposable? Watch(Action changed)
    {
        var watchers = new List<FileSystemWatcher>();
        try
        {
            watchers.Add(Watcher(WorkTree, true, changed));
            if (!Contains(WorkTree, CommonDirectory)) watchers.Add(Watcher(CommonDirectory, true, changed));
            if (!Contains(WorkTree, GitDirectory) && !Contains(CommonDirectory, GitDirectory)) watchers.Add(Watcher(GitDirectory, false, changed));
            return new Watchers(watchers);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            foreach (var watcher in watchers) watcher.Dispose();
            return null;
        }
    }

    internal static IDisposable? WatchCreation(AgentTerm.Terminal.ShellDirectory directory, Action changed)
    {
        if (directory.WslDistribution is not null) return null;
        try
        {
            var watcher = new FileSystemWatcher(directory.Path, ".git") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
            watcher.Created += (_, _) => changed();
            watcher.Renamed += (_, _) => changed();
            watcher.Error += (_, _) => changed();
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException) { return null; }
    }

    internal bool Affects(string path)
    {
        var git = Relative(GitDirectory, path);
        var common = Relative(CommonDirectory, path);
        if (git is null && common is null) return true;
        return git is "HEAD" or "index" || common is "packed-refs" or "refs" || common?.StartsWith("refs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true;
    }

    private FileSystemWatcher Watcher(string path, bool recursive, Action changed)
    {
        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        FileSystemEventHandler handler = (_, args) => { if (Affects(args.FullPath)) changed(); };
        watcher.Changed += handler;
        watcher.Created += handler;
        watcher.Deleted += handler;
        watcher.Renamed += (_, args) => { if (Affects(args.FullPath) || Affects(args.OldFullPath)) changed(); };
        watcher.Error += (_, _) => changed();
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private static string? Relative(string root, string path)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return "";
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path[prefix.Length..] : null;
    }

    private static bool Contains(string root, string path) => Relative(root, path) is not null;

    private static string Trim(string path) => Path.TrimEndingDirectorySeparator(path);

    private sealed class Watchers(List<FileSystemWatcher> watchers) : IDisposable
    {
        public void Dispose() { foreach (var watcher in watchers) watcher.Dispose(); }
    }
}
