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
        await RepositoriesAsync();
        Console.WriteLine("Pane status checks passed native OSC metadata, URI boundaries, Git fixtures, and stale-query/hidden-pane scheduling.");
    }

    private static async Task MonitorAsync()
    {
        var queries = new List<TaskCompletionSource<GitResult>>();
        using var monitor = new PaneStatusMonitor((_, _) =>
        {
            var query = new TaskCompletionSource<GitResult>();
            queries.Add(query);
            return query.Task;
        });
        var now = DateTimeOffset.UtcNow;
        var first = new ShellDirectory("first");
        var second = new ShellDirectory("second");
        monitor.Refresh(first, true, now);
        monitor.Refresh(second, true, now);
        Check(queries.Count == 1 && monitor.Result.Snapshot is null, "Changing directory must clear Git immediately and keep one query slot.");
        queries[0].SetResult(new(GitQueryState.Ready, new("obsolete", GitBranchKind.Named, 0, 0, false, false, false, false)));
        await Task.Yield();
        Check(monitor.Result.Snapshot is null, "A canceled directory query must not publish stale Git.");
        monitor.Refresh(second, true, now);
        Check(queries.Count == 2, "The new directory must query after cancellation completes.");
        queries[1].SetResult(new(GitQueryState.NoRepository));
        await Task.Yield();
        monitor.Refresh(second, true, now + TimeSpan.FromSeconds(4));
        Check(queries.Count == 2, "Polling must honor its interval.");
        monitor.Refresh(second, false, now + TimeSpan.FromMinutes(1));
        Check(queries.Count == 2, "Hidden panes must not poll.");
        monitor.Refresh(second, true, now + TimeSpan.FromMinutes(1));
        Check(queries.Count == 3, "Reactivated panes must refresh immediately.");
        monitor.Dispose();
        queries[2].SetResult(new(GitQueryState.Ready, new("disposed", GitBranchKind.Named, 0, 0, false, false, false, false)));
        await Task.Yield();
        Check(monitor.Result.Snapshot is null, "Disposed panes must reject completions.");
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
            await Git(root, "init", "-b", "nested", outside);
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
