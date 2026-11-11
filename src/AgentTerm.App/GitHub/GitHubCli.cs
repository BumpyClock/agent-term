using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgentTerm_App.GitHub;

internal enum GitHubAuthState
{
    SignedIn,
    SignedOut,
    Unknown
}

internal static class GitHubCli
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    public static Task<JsonElement> ReadApiAsync(string endpoint, string? query, CancellationToken cancellationToken) =>
        Task.Run(() => RunAsync(string.IsNullOrEmpty(query)
            ? ["api", "--hostname", "github.com", endpoint]
            : ["api", "--hostname", "github.com", endpoint, "-f", "query=" + query], cancellationToken), cancellationToken);

    public static Task<GitHubAuthState> ProbeAuthAsync(CancellationToken cancellationToken) => Task.Run(async () =>
    {
        try
        {
            return ClassifyAuth(await RunAsync(["auth", "status", "--hostname", "github.com", "--json", "hosts", "--no-refresh"],
                cancellationToken).ConfigureAwait(false));
        }
        catch (GitHubException exception) when (exception.Failure != GitHubFailure.MissingCli)
        {
            return GitHubAuthState.Unknown;
        }
    }, cancellationToken);

    internal static GitHubAuthState ClassifyAuth(JsonElement status)
    {
        var hosts = GitHubJson.Get(status, "hosts");
        GitHubJson.Require(hosts.ValueKind == JsonValueKind.Object);
        var accounts = GitHubJson.Get(hosts, "github.com");
        if (GitHubJson.IsNull(accounts)) return GitHubAuthState.SignedOut;
        GitHubJson.RequireArray(accounts, 100);
        var active = accounts.EnumerateArray().Where(account => GitHubJson.Flag(account, "active", false)).ToArray();
        if (active.Length == 0) return GitHubAuthState.SignedOut;
        GitHubJson.Require(active.Length == 1);
        var state = GitHubJson.Text(active[0], "state");
        if (state == "success") return GitHubAuthState.SignedIn;
        var error = GitHubJson.Text(active[0], "error", true);
        return state == "error" && error.Contains("401 Unauthorized", StringComparison.Ordinal)
            ? GitHubAuthState.SignedOut
            : GitHubAuthState.Unknown;
    }

    private static async Task<JsonElement> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(FindExecutable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            throw new GitHubException(GitHubFailure.Request);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        try
        {
            process.StandardInput.Close();
            // Never surface stderr: credential helpers and gh errors can contain secrets. Drain it so gh cannot block.
            var drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            var output = await ReadOutputAsync(process.StandardOutput.BaseStream, deadline.Token).ConfigureAwait(false);
            await drain.ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new GitHubException(GitHubFailure.Request);
            return GitHubJson.Parse(output);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubException(GitHubFailure.Timeout);
        }
        catch (IOException)
        {
            throw new GitHubException(GitHubFailure.Request);
        }
        finally
        {
            Terminate(process);
        }
    }

    // Searches only absolute PATH entries, so gh is never run from the current directory.
    private static string FindExecutable()
    {
        var name = OperatingSystem.IsWindows() ? "gh.exe" : "gh";
        var entries = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(entry => entry.Length >= 2 && entry[0] == '"' && entry[^1] == '"' ? entry[1..^1] : entry)
            .Where(Path.IsPathFullyQualified);
        return entries.Select(entry => Path.Combine(entry, name)).FirstOrDefault(File.Exists)
            ?? throw new GitHubException(GitHubFailure.MissingCli);
    }

    private static async Task<byte[]> ReadOutputAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > GitHubJson.MaximumBytes) throw new GitHubException(GitHubFailure.InvalidResponse);
        }
        return output.ToArray();
    }

    private static void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The process exited between the check and the kill.
        }
    }
}
