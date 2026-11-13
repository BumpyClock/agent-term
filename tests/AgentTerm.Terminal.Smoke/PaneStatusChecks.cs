using System.Diagnostics;
using System.Text;
using AgentTerm.Terminal;
using AgentTerm_App.PaneStatus;

internal static class PaneStatusChecks
{
    internal static async Task RunAsync()
    {
        using (var terminal = new GhosttyTerminal())
        {
            var report = Encoding.UTF8.GetBytes("\x1b]7;file://localhost/C:/space%20%C3%A9\x1b\\");
            foreach (var value in report) terminal.Write(new[] { value });
            Check(terminal.ReadWorkingDirectory() == "file://localhost/C:/space%20%C3%A9", "Split OSC reports must survive chunk boundaries.");
            Check(terminal.ReadText() == "", "Directory reports must not appear on screen.");
            terminal.Write("\x1b]7;\a"u8);
            Check(terminal.ReadWorkingDirectory() == "", "An empty directory report must clear metadata.");
        }
        var windows = new ShellProfile("pwsh", "PowerShell", "pwsh.exe", "", "C:\\");
        Check(ShellDirectory.FromReport("file://localhost/C:/space%20%C3%A9", windows)?.Path == "C:\\space é", "URI decoding must preserve spaces and Unicode.");
        Check(ShellDirectory.FromReport("\"C:\\repo # %20\"", windows)?.Path == "C:\\repo # %20", "Quoted Windows reports from prompt tools must not URI-decode literal percent signs.");
        Check(ShellDirectory.FromReport("file://remote-host/C:/repo", windows) is null, "Remote OSC reports must not trigger local Git.");
        Check(ShellDirectory.FromReport("file://localhost/home/dev/repo", windows with { Id = "wsl:Ubuntu" }) == new ShellDirectory("/home/dev/repo", "Ubuntu"), "WSL reports must retain their distribution.");
        Check(ShellDirectory.FromReport("file://localhost/C:/bad%1Bpath", windows) is null, "Control-containing reports must be rejected.");
        var oid = new string('a', 40);
        var renamed = GitStatusParser.ParseStatus($"# branch.oid {oid}\0# branch.head feature/long\0# branch.ab +2 -3\02 R. N... 100644 100644 100644 {oid} {oid} R100 new\0? old-looking-name\0? untracked\0");
        Check(renamed.Staged && renamed.Untracked && renamed.Ahead == 2 && renamed.Behind == 3, "Renames must consume their source record and tracking counts must survive.");
        var detached = GitStatusParser.ParseStatus($"# branch.oid {oid}\0# branch.head (detached)\0");
        Check(detached.Kind == GitBranchKind.Detached && detached.BranchLabel == "detached aaaaaaaa", "Detached HEAD must show its short commit.");
        Check(GitStatusParser.ParseLines("2\t1\tname\0-\t-\tbinary\0") == new GitLineChanges(2, 1, true), "Binary entries must not contribute line totals.");
        await MonitorAsync();
        await SharingAsync();
        await BoundariesAsync();
        var watched = await IdleQueriesPerMinuteAsync(true);
        var unwatched = await IdleQueriesPerMinuteAsync(false);
        Check(watched == 2 && unwatched == 12, $"An idle watched pane must query twice per minute and an unwatched pane every 5 s. Actual: {watched}, {unwatched}.");
        await RepositoriesAsync();
        Console.WriteLine($"Pane status checks passed native OSC metadata, URI boundaries, Git fixtures, shared repository queries, unchanged-result suppression, and watched scheduling (idle Git queries per minute: {watched} watched, {unwatched} unwatched).");
    }

    private sealed class Probe
    {
        private readonly List<(ShellDirectory Directory, TaskCompletionSource<GitResult> Result)> _queries = [];
        private readonly Dictionary<string, Action> _watchers = [];
        internal DateTimeOffset Time;
        internal int Unwatched;
        internal GitStatusHub Hub { get; }

        internal Probe(bool watched = true, GitResult? immediate = null) => Hub = new((directory, _) =>
        {
            var query = new TaskCompletionSource<GitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (immediate is not null) query.SetResult(immediate);
            lock (_queries) _queries.Add((directory, query));
            return query.Task;
        }, directory => new GitRepository(directory.Path.Split('\\')[0], "", ""), (_, repository, changed) =>
        {
            if (!watched) return null;
            lock (_watchers) _watchers[repository!.WorkTree] = changed;
            return new Stop(() => Interlocked.Increment(ref Unwatched));
        }, () => Time);

        internal int Count { get { lock (_queries) return _queries.Count; } }
        internal void Complete(int index, GitResult result) { lock (_queries) _queries[index].Result.SetResult(result); }
        internal void Change(string repository, DateTimeOffset at) { Time = at; lock (_watchers) _watchers[repository](); }
    }

    private sealed class Stop(Action stop) : IDisposable { public void Dispose() => stop(); }

    private static GitResult Clean(string branch = "main") => new(GitQueryState.Ready, new(branch, GitBranchKind.Named, 0, 0, false, false, false, false));

    private static async Task Until(Func<bool> condition, string message)
    {
        for (var attempt = 0; attempt < 1000 && !condition(); attempt++) await Task.Delay(10);
        Check(condition(), message);
    }

    private static async Task Settle(PaneStatusMonitor monitor, ShellDirectory directory, DateTimeOffset now, Probe probe, int expected, string message)
    {
        await Until(() => { monitor.Refresh(directory, true, now); return probe.Count >= expected; }, message);
        await Task.Delay(50);
        monitor.Refresh(directory, true, now);
        Check(probe.Count == expected, message);
    }

    private static async Task MonitorAsync()
    {
        var probe = new Probe();
        var now = DateTimeOffset.UtcNow;
        var first = new ShellDirectory("first");
        var second = new ShellDirectory("second");
        using var monitor = new PaneStatusMonitor(probe.Hub);
        var changes = 0;
        monitor.Changed += () => changes++;
        await Settle(monitor, first, now, probe, 1, "A new directory must query once.");
        monitor.Refresh(second, true, now);
        Check(changes == 2 && monitor.Result.Snapshot is null, "Changing directory must clear Git immediately.");
        probe.Complete(0, Clean("obsolete"));
        await Task.Delay(50);
        Check(monitor.Result.Snapshot is null, "A canceled directory query must not publish stale Git.");
        await Settle(monitor, second, now, probe, 2, "The new directory must query after cancellation completes.");
        probe.Complete(1, Clean());
        await Until(() => monitor.Result == Clean(), "A completed query must publish its result.");
        Check(changes == 3, "A new result must rebuild the header once.");
        await Settle(monitor, second, now + TimeSpan.FromSeconds(20), probe, 2, "Watched repositories must not poll before the fallback interval.");
        probe.Change("second", now + TimeSpan.FromSeconds(21));
        await Settle(monitor, second, now + TimeSpan.FromSeconds(21.1), probe, 2, "Repository changes must wait for the debounce.");
        await Settle(monitor, second, now + TimeSpan.FromSeconds(21.3), probe, 3, "Repository changes must refresh after the debounce.");
        await Task.Delay(700);
        probe.Complete(2, Clean());
        await Settle(monitor, second, now + TimeSpan.FromSeconds(22), probe, 3, "A finished query must settle.");
        Check(changes == 3, "An equal Git result must not rebuild the header.");
        probe.Change("second", now + TimeSpan.FromSeconds(22.5));
        await Settle(monitor, second, now + TimeSpan.FromSeconds(28.5), probe, 3, "Slow queries must back off by ten times their duration.");
        await Settle(monitor, second, now + TimeSpan.FromSeconds(35), probe, 4, "Backed-off changes must still refresh.");
        probe.Complete(3, Clean("dirty"));
        await Until(() => monitor.Result == Clean("dirty"), "A changed result must publish.");
        Check(changes == 4, "A changed result must rebuild the header once.");
        monitor.Refresh(second, false, now + TimeSpan.FromMinutes(5));
        await Task.Delay(50);
        Check(probe.Count == 4, "Hidden panes must not poll.");
        probe.Hub.Paused = true;
        await Settle(monitor, second, now + TimeSpan.FromMinutes(5), probe, 4, "Minimized windows must not poll.");
        probe.Hub.Paused = false;
        await Settle(monitor, second, now + TimeSpan.FromMinutes(5), probe, 5, "Restored panes must refresh stale Git.");
        monitor.Dispose();
        probe.Complete(4, Clean("disposed"));
        await Task.Delay(50);
        Check(monitor.Result == Clean("dirty") && probe.Unwatched == 2, "Disposed panes must reject completions and stop watching.");
    }

    private static async Task SharingAsync()
    {
        var probe = new Probe();
        var now = DateTimeOffset.UtcNow;
        var left = new PaneStatusMonitor(probe.Hub);
        var right = new PaneStatusMonitor(probe.Hub);
        await Until(() =>
        {
            left.Refresh(new("repo\\left"), true, now);
            right.Refresh(new("repo\\right"), true, now);
            return probe.Count == 1 && probe.Hub.EntryCount == 1;
        }, "Panes in one repository must share one entry.");
        probe.Complete(0, Clean());
        await Until(() =>
        {
            left.Refresh(new("repo\\left"), true, now);
            right.Refresh(new("repo\\right"), true, now);
            return left.Result == Clean() && right.Result == Clean();
        }, "A shared query must publish to every pane in the repository.");
        Check(probe.Count == 1, "Panes in one repository must share one Git query.");
        left.Dispose();
        Check(probe.Unwatched == 0 && probe.Hub.EntryCount == 1, "A repository must stay watched while a pane uses it.");
        right.Dispose();
        Check(probe.Unwatched == 1 && probe.Hub.EntryCount == 0, "The last pane leaving a repository must stop watching it.");
    }

    private static async Task BoundariesAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var parent = new GitRepository("parent", "parent/git", "parent/git");
        var nested = new GitRepository("parent/child", "child/git", "child/git");
        GitRepository? childRepository = parent;
        var queries = new List<(ShellDirectory Directory, TaskCompletionSource<GitResult> Completion)>();
        var watchers = new List<(GitRepository? Repository, Action Changed, Stop Lifetime)>();
        var released = 0;
        var located = 0;
        var hub = new GitStatusHub((directory, _) =>
        {
            var completion = new TaskCompletionSource<GitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (queries) queries.Add((directory, completion));
            return completion.Task;
        }, directory =>
        {
            Interlocked.Increment(ref located);
            return directory.Path == "parent" ? parent : childRepository;
        }, (_, repository, changed) =>
        {
            var lifetime = new Stop(() => Interlocked.Increment(ref released));
            lock (watchers) watchers.Add((repository, changed, lifetime));
            return lifetime;
        }, () => now);
        int Count() { lock (queries) return queries.Count; }
        void Complete(int index, GitResult result) { lock (queries) queries[index].Completion.SetResult(result); }
        using var left = new PaneStatusMonitor(hub);
        using var right = new PaneStatusMonitor(hub);
        void Refresh()
        {
            left.Refresh(new("parent"), true, now);
            right.Refresh(new("parent/child"), true, now);
        }
        await Until(() => { Refresh(); return Count() == 1; }, "Parent and child must initially share one query.");
        Check(queries[0].Directory.Path == "parent", "A shared query must target the canonical root.");
        Complete(0, Clean("parent"));
        await Until(() => { Refresh(); return left.Result == Clean("parent") && right.Result == Clean("parent"); }, "Both panes must adopt the parent branch.");
        var scans = located;
        for (var tick = 0; tick < 20; tick++) Refresh();
        Check(scans == located, "Cheap UI polls must not scan repository membership.");
        childRepository = nested;
        now += TimeSpan.FromSeconds(6);
        await Until(() => { Refresh(); return Count() == 2; }, "Nested git init must rebind without a cwd report.");
        Check(queries[1].Directory.Path == "parent/child" && hub.EntryCount == 2, "A nested repository must own its query and entry.");
        Complete(1, Clean("nested"));
        await Until(() => { Refresh(); return right.Result == Clean("nested"); }, "The nested pane must show its own branch.");
        Check(left.Result == Clean("parent") && Count() == 2, "Nested status must never replace the parent branch.");
        now += TimeSpan.FromSeconds(31);
        await Until(() => { Refresh(); return Count() == 4; }, "Each repository must have one due query.");
        childRepository = parent;
        now += TimeSpan.FromSeconds(6);
        await Until(() => { Refresh(); return hub.EntryCount == 1; }, "Removal must release the old entry even while its query is running.");
        for (var index = 2; index < 4; index++) Complete(index, queries[index].Directory.Path == "parent" ? Clean("current") : Clean("obsolete"));
        await Until(() => { Refresh(); return right.Result == Clean("current") && left.Result == Clean("current"); }, "An old in-flight query must not overwrite the rebound pane.");
        Check(released == 1, "Removing the nested repository must release its watcher.");
        left.Dispose(); right.Dispose();
        Check(hub.EntryCount == 0 && released == 2, "Disposal must release every repository reference.");

        childRepository = null;
        using var promoted = new PaneStatusMonitor(hub);
        await Until(() => { promoted.Refresh(new("parent/child"), true, now); return Count() == 5; }, "A nonrepository must query once.");
        Complete(4, new(GitQueryState.NoRepository));
        await Task.Delay(50);
        childRepository = nested;
        now += TimeSpan.FromSeconds(6);
        await Until(() => { promoted.Refresh(new("parent/child"), true, now); return Count() == 6; }, "Creation-only watchers must retain the five-second cadence.");
        Complete(5, Clean("promoted"));
        await Until(() => { promoted.Refresh(new("parent/child"), true, now); return promoted.Result == Clean("promoted"); }, "Git init must promote the pane.");
        Check(released == 3 && watchers[^1].Repository == nested, "Promotion must replace the creation watcher with a repository watcher.");
        now += TimeSpan.FromSeconds(6);
        watchers[^1].Changed();
        now += TimeSpan.FromMilliseconds(300);
        await Until(() => { promoted.Refresh(new("parent/child"), true, now); return Count() == 7; }, "Later edits must refresh through the promoted watcher.");
        Complete(6, Clean("edited"));
        await Until(() => { promoted.Refresh(new("parent/child"), true, now); return promoted.Result == Clean("edited"); }, "The watcher refresh must publish.");
        var replacement = nested with { GitDirectory = "replacement/git", CommonDirectory = "replacement/git" };
        childRepository = replacement;
        now += TimeSpan.FromSeconds(6);
        await Until(() => { promoted.Refresh(new("parent/child"), true, now); return Count() == 8; }, "Replacing repository metadata must rebind the entry.");
        Complete(7, Clean("replacement"));
        await Until(() => { promoted.Refresh(new("parent/child"), true, now); return promoted.Result == Clean("replacement"); }, "Replacement must publish its own status.");
        Check(released == 4 && hub.EntryCount == 1, "Replacement must release the old metadata watcher.");
        hub.Paused = true;
        scans = located;
        now += TimeSpan.FromMinutes(1);
        promoted.Refresh(new("parent/child"), true, now);
        await Task.Delay(50);
        Check(scans == located && Count() == 8, "Pause must suppress membership scans as well as queries.");
        promoted.Dispose();
        Check(released == 5 && hub.EntryCount == 0, "Promoted and replacement entries must be fully released.");
    }

    private static async Task<int> IdleQueriesPerMinuteAsync(bool watched)
    {
        var probe = new Probe(watched, Clean());
        var start = DateTimeOffset.UtcNow;
        using var monitor = new PaneStatusMonitor(probe.Hub);
        var directory = new ShellDirectory("idle");
        await Until(() => { monitor.Refresh(directory, true, start); return monitor.Result == Clean(); }, "An idle pane must query once.");
        for (var tick = 1; tick < 240; tick++)
        {
            monitor.Refresh(directory, true, start + TimeSpan.FromMilliseconds(250 * tick));
            await Task.Delay(1);
        }
        return probe.Count;
    }

    private static async Task RepositoriesAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentterm-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var provider = new GitStatusProvider();
        try
        {
            var availability = await provider.QueryAsync(new(root), default);
            if (availability.State == GitQueryState.Unavailable)
            {
                Console.WriteLine("Real Git fixtures skipped. Git 2.45 or newer is not installed.");
                return;
            }
            await Git(root, "init", "-b", "main");
            await Git(root, "config", "user.name", "AgentTerm Test");
            await Git(root, "config", "user.email", "test@example.invalid");
            await Git(root, "config", "core.autocrlf", "false");
            var file = Path.Combine(root, "tracked.txt");
            await File.WriteAllTextAsync(file, "one\ntwo\n");
            await Git(root, "add", "tracked.txt");
            var unborn = await provider.QueryAsync(new(root), default);
            Check(unborn.Snapshot is { Kind: GitBranchKind.Unborn, Staged: true, Lines.Added: 2, Lines.Deleted: 0 },  $"Unborn branches must compare staged content with the empty tree. Actual: {unborn}");
            await Git(root, "commit", "-qm", "initial");
            Check((await provider.QueryAsync(new(root), default)).Snapshot is { Branch: "main", Staged: false, Unstaged: false, Untracked: false, Lines.Added: 0 }, "A committed worktree must show a clean branch.");
            await WatchedRepositoryAsync(provider, root, file);
            await File.WriteAllTextAsync(file, "one\nchanged\nextra\n");
            await Git(root, "add", "tracked.txt");
            await File.WriteAllTextAsync(file, "one\ntwo\n");
            await File.WriteAllTextAsync(Path.Combine(root, "untracked.txt"), "not counted\n");
            var child = Directory.CreateDirectory(Path.Combine(root, "child")).FullName;
            var canceled = await provider.QueryAsync(new(child), default);
            Check(canceled.Snapshot is { Staged: true, Unstaged: true, Untracked: true, Lines.Added: 0, Lines.Deleted: 0 }, "Staged and unstaged changes must stay visible even when their net line totals cancel, including from a subdirectory.");
            await Git(root, "reset", "--hard", "HEAD");
            await File.WriteAllTextAsync(file, "one\nchanged\nextra\n");
            Check((await provider.QueryAsync(new(child), default)).Snapshot is { Lines.Added: 2, Lines.Deleted: 1 }, "Line totals must cover the whole worktree from a nested directory.");
            await Git(root, "checkout", "--", "tracked.txt");
            await File.WriteAllBytesAsync(file, [0, 1, 2, 3]);
            Check((await provider.QueryAsync(new(root), default)).Snapshot is { Unstaged: true, Lines.Binary: true, Lines.Added: 0, Lines.Deleted: 0 }, "Binary changes must show state without numeric totals.");
            await Git(root, "checkout", "--", "tracked.txt");
            await Git(root, "checkout", "-qb", "other");
            await File.WriteAllTextAsync(file, "other\n");
            await Git(root, "commit", "-qam", "other");
            await Git(root, "checkout", "-q", "main");
            await File.WriteAllTextAsync(file, "main\n");
            await Git(root, "commit", "-qam", "main");
            await Git(root, "branch", "--set-upstream-to", "other");
            Check((await provider.QueryAsync(new(root), default)).Snapshot is { Ahead: 1, Behind: 1 }, "Tracking must use local upstream refs without fetching.");
            await Git(root, "merge", "other", expectedExit: 1);
            Check((await provider.QueryAsync(new(root), default)).Snapshot is { Conflicts: true, Lines: null }, "Unresolved conflicts must suppress aggregate line totals.");
            await Git(root, "merge", "--abort");
            await Git(root, "checkout", "--detach", "-q");
            Check((await provider.QueryAsync(new(root), default)).Snapshot?.Kind == GitBranchKind.Detached, "Real detached HEAD must be identified.");
            var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            await NestedInitializationAsync(provider, root, outside);
            Check((await provider.QueryAsync(new(outside), default)).Snapshot?.Branch == "nested", "Nested repositories must select their own branch.");
            var notRepository = Directory.CreateDirectory(root + "-outside").FullName;
            try { Check((await provider.QueryAsync(new(notRepository), default)).State == GitQueryState.NoRepository, "Leaving Git must clear its status."); }
            finally { Directory.Delete(notRepository); }
        }
        finally
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task NestedInitializationAsync(GitStatusProvider provider, string root, string child)
    {
        var hub = new GitStatusHub(provider.QueryAsync);
        using var parentPane = new PaneStatusMonitor(hub);
        using var childPane = new PaneStatusMonitor(hub);
        var now = DateTimeOffset.UtcNow;
        void Refresh()
        {
            parentPane.Refresh(new(root), true, now);
            childPane.Refresh(new(child), true, now);
        }
        await Until(() => { Refresh(); return parentPane.Result.Snapshot is not null && childPane.Result == parentPane.Result; }, "Real parent and child panes must initially share status.");
        Check(hub.EntryCount == 1, "Real panes must initially share one repository entry.");
        await Git(child, "init", "-b", "nested");
        now += TimeSpan.FromSeconds(6);
        await Until(() => { Refresh(); return childPane.Result.Snapshot?.Branch == "nested"; }, "Real nested git init must rebind without changing either pane's directory.");
        Check(parentPane.Result.Snapshot?.Kind == GitBranchKind.Detached && hub.EntryCount == 2, "Real nested git init must preserve the parent's detached branch.");
        parentPane.Dispose(); childPane.Dispose();
        Check(hub.EntryCount == 0, "Real nested entries must release all watchers.");
    }

    private static async Task WatchedRepositoryAsync(GitStatusProvider provider, string root, string file)
    {
        var queries = 0;
        var hub = new GitStatusHub((directory, cancellation) => { Interlocked.Increment(ref queries); return provider.QueryAsync(directory, cancellation); });
        var child = Directory.CreateDirectory(Path.Combine(root, "watched")).FullName;
        using (var monitor = new PaneStatusMonitor(hub))
        {
            await Until(() => { monitor.Refresh(new(child), true, DateTimeOffset.UtcNow); return monitor.Result.State == GitQueryState.Ready; }, "A watched repository must report Git.");
            await Task.Delay(1000);
            monitor.Refresh(new(child), true, DateTimeOffset.UtcNow);
            Check(queries == 1, $"Git's own queries must not mark the repository stale. Queries: {queries}.");
            await File.AppendAllTextAsync(file, "edited\n");
            await Until(() => { monitor.Refresh(new(child), true, DateTimeOffset.UtcNow); return monitor.Result.Snapshot?.Unstaged == true; }, "Working tree edits must refresh Git without waiting for the fallback interval.");
            await Git(root, "checkout", "--", "tracked.txt");
            await Until(() => { monitor.Refresh(new(child), true, DateTimeOffset.UtcNow); return monitor.Result.Snapshot?.Unstaged == false; }, "Restoring files must refresh Git.");
        }
        Check(hub.EntryCount == 0, "Closing the last pane must stop watching the repository.");
        Directory.Delete(child);
        var worktree = root + "-worktree";
        await Git(root, "worktree", "add", "-q", "-b", "linked", worktree);
        try
        {
            var linked = GitRepository.Locate(Path.Combine(worktree));
            Check(linked is not null && linked.WorkTree == worktree && Path.GetFileName(linked.CommonDirectory) == ".git" &&
                linked.GitDirectory.Equals(Path.Combine(linked.CommonDirectory, "worktrees", Path.GetFileName(worktree)), StringComparison.OrdinalIgnoreCase),
                $"Linked worktrees must resolve their Git and common directories. Actual: {linked}");
            var common = linked!.CommonDirectory;
            Check(linked.Affects(Path.Combine(linked.GitDirectory, "HEAD")) && linked.Affects(Path.Combine(common, "refs", "heads", "main")) &&
                linked.Affects(Path.Combine(worktree, "tracked.txt")) && !linked.Affects(Path.Combine(common, "objects", "ab", "cdef")) &&
                !linked.Affects(Path.Combine(common, "HEAD")), "Watchers must react to this worktree's HEAD, index, refs and files only.");
            Check(GitRepository.Locate(Path.Combine(root, "nested", "deeper"))?.WorkTree == root, "Subdirectories must resolve their repository root.");
        }
        finally
        {
            await Git(root, "worktree", "remove", "--force", worktree);
        }
    }

    private static Task Git(string directory, params string[] arguments) => Git(directory, arguments, 0);

    private static Task Git(string directory, string command, string argument, int expectedExit) => Git(directory, [command, argument], expectedExit);

    private static async Task Git(string directory, string[] arguments, int expectedExit)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        if (process.ExitCode != expectedExit) throw new Exception(await error);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
