using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using AgentTerm.Terminal;

namespace AgentTerm_App.PaneStatus;

internal sealed class GitStatusProvider
{
    internal async Task<GitRepositoryDiscovery> LocateWslAsync(ShellDirectory directory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var result = await RunAsync(directory,
                ["rev-parse", "--path-format=absolute", "--show-toplevel", "--absolute-git-dir", "--git-common-dir"], deadline.Token).ConfigureAwait(false);
            if (result.Code != 0)
                return new(null, result.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
                    ? GitQueryState.NoRepository : GitQueryState.Unavailable);
            var paths = result.Output.TrimEnd('\r', '\n').Split('\n');
            if (paths.Length != 3 || paths.Any(path => !path.StartsWith('/') || path.Contains('\r')))
                return new(null, GitQueryState.Failed);
            return new(new(paths[0], paths[1], paths[2]), GitQueryState.Ready);
        }
        catch (OperationCanceledException) { return new(null, GitQueryState.TimedOut); }
        catch (Win32Exception) { return new(null, GitQueryState.Unavailable); }
        catch (Exception error) when (error is IOException or InvalidOperationException) { return new(null, GitQueryState.Failed); }
    }

    internal async Task<GitResult> QueryAsync(ShellDirectory directory, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var status = await RunAsync(directory, ["status", "--porcelain=v2", "--branch", "--ahead-behind", "-z", "--untracked-files=normal"], deadline.Token).ConfigureAwait(false);
            if (status.Code != 0)
            {
                if (status.Error.Contains("unknown option", StringComparison.OrdinalIgnoreCase) && status.Error.Contains("no-lazy-fetch", StringComparison.OrdinalIgnoreCase))
                    return new(GitQueryState.Unavailable);
                return new(status.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) ? GitQueryState.NoRepository : GitQueryState.Failed);
            }
            var snapshot = GitStatusParser.ParseStatus(status.Output);
            if (!snapshot.Conflicts)
            {
                var baseline = "HEAD";
                if (snapshot.Kind == GitBranchKind.Unborn)
                {
                    var tree = await RunAsync(directory, ["hash-object", "-t", "tree", "--stdin"], deadline.Token).ConfigureAwait(false);
                    if (tree.Code != 0) return new(GitQueryState.Failed);
                    baseline = tree.Output.Trim();
                    if (baseline.Length is not (40 or 64) || !baseline.All(char.IsAsciiHexDigit)) return new(GitQueryState.Failed);
                }
                // The root pathspec includes the whole worktree, even when this pane is in a subdirectory.
                var diff = await RunAsync(directory, ["diff", "--numstat", "-z", "--no-ext-diff", "--no-textconv", "--no-renames", baseline, "--", ":/"], deadline.Token).ConfigureAwait(false);
                if (diff.Code != 0) return new(GitQueryState.Failed);
                snapshot = snapshot with { Lines = GitStatusParser.ParseLines(diff.Output) };
            }
            return new(GitQueryState.Ready, snapshot);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return new(GitQueryState.TimedOut); }
        catch (Win32Exception) { return new(GitQueryState.Unavailable); }
        catch (Exception exception) when (exception is IOException or FormatException or OverflowException or InvalidOperationException)
        {
            Debug.WriteLine($"Pane Git query failed: {exception.Message}");
            return new(GitQueryState.Failed);
        }
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(ShellDirectory directory, string[] arguments, CancellationToken cancellation)
    {
        var info = new ProcessStartInfo(directory.WslDistribution is null ? "git" : "wsl.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // A terminal launched from Git tooling must not query that tool's repository instead of this pane's.
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["LC_ALL"] = "C";
        if (directory.WslDistribution is { } distro)
        {
            foreach (var value in new[] { "--distribution", distro, "--exec", "/usr/bin/timeout", "--signal=KILL", "5s", "/usr/bin/env",
                "-u", "GIT_DIR", "-u", "GIT_WORK_TREE", "-u", "GIT_INDEX_FILE", "LC_ALL=C", "GIT_TERMINAL_PROMPT=0", "git" })
                info.ArgumentList.Add(value);
        }
        foreach (var value in new[] { "--no-pager", "--no-optional-locks", "--no-lazy-fetch", "-c", "core.fsmonitor=false", "-C", directory.Path })
            info.ArgumentList.Add(value);
        foreach (var value in arguments) info.ArgumentList.Add(value);
        using var process = new Process { StartInfo = info };
        cancellation.ThrowIfCancellationRequested();
        process.Start();
        process.StandardInput.Close();
        using var stop = cancellation.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        });
        using var readers = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var output = ReadAsync(process.StandardOutput, readers);
        var error = ReadAsync(process.StandardError, readers);
        try
        {
            await Task.WhenAll(output, error, process.WaitForExitAsync(cancellation)).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            return (process.ExitCode, output.Result, error.Result);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadAsync(StreamReader reader, CancellationTokenSource cancellation)
    {
        var buffer = new char[4096];
        var text = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation.Token).ConfigureAwait(false)) > 0)
        {
            if (text.Length + count > 16 * 1024 * 1024)
            {
                cancellation.Cancel();
                throw new IOException("Git output exceeded 16 MiB.");
            }
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
