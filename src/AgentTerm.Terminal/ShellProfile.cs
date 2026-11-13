using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AgentTerm.Terminal;

/// <summary>A shell launch configuration. Arguments are passed directly to CreateProcess.</summary>
public sealed record ShellProfile(string Id, string Name, string Executable, string Arguments, string StartingDirectory)
{
    public static IReadOnlyList<ShellProfile> Discover() => [.. DiscoverBuiltIn(), .. DiscoverWsl()];

    public static IReadOnlyList<ShellProfile> DiscoverBuiltIn()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var profiles = new List<ShellProfile>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (!File.Exists(pwsh))
        {
            pwsh = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(path => Path.Combine(path.Trim('"'), "pwsh.exe"))
                .FirstOrDefault(path => Path.IsPathFullyQualified(path) && File.Exists(path)) ?? pwsh;
        }
        Add("pwsh", "PowerShell", pwsh, "-NoLogo");
        Add("powershell", "Windows PowerShell", Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"), "-NoLogo");
        Add("cmd", "Command Prompt", Path.Combine(system, "cmd.exe"), "/D /Q");
        return profiles;

        void Add(string id, string name, string executable, string arguments)
        {
            if (File.Exists(executable)) profiles.Add(new ShellProfile(id, name, executable, arguments, home));
        }
    }

    public static IReadOnlyList<ShellProfile> DiscoverWsl()
    {
        if (!OperatingSystem.IsWindows() || WslExecutable() is not { } wsl) return [];
        return FindWslDistributions(wsl).Select(distro => CreateWsl(wsl, distro)).ToArray();
    }

    public static ShellProfile? FromId(string? id)
    {
        if (id is null || !id.StartsWith("wsl:", StringComparison.Ordinal) || id.Length == 4 || !OperatingSystem.IsWindows()) return null;
        var distro = id[4..];
        return distro.Any(char.IsControl) || WslExecutable() is not { } wsl ? null : CreateWsl(wsl, distro);
    }

    private static string? WslExecutable()
    {
        var wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        return File.Exists(wsl) ? wsl : null;
    }

    private static ShellProfile CreateWsl(string wsl, string distro) => new("wsl:" + distro, distro, wsl,
        "--distribution " + QuoteArgument(distro) + " --cd ~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private static IReadOnlyList<string> FindWslDistributions(string executable)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.Unicode,
            StandardErrorEncoding = Encoding.Unicode,
        };
        info.ArgumentList.Add("--list");
        info.ArgumentList.Add("--quiet");
        try
        {
            using var process = Process.Start(info);
            if (process is null) return [];
            var deadline = Stopwatch.StartNew();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(2000))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
                return [];
            }
            var remaining = Math.Max(0, 2000 - (int)deadline.ElapsedMilliseconds);
            if (process.ExitCode != 0 || !output.Wait(remaining)) return [];
            return ParseWslDistributions(output.Result);
        }
        catch (Win32Exception) { return []; }
        catch (IOException) { return []; }
        catch (AggregateException) { return []; }
        catch (InvalidOperationException) { return []; }
    }

    // WSL emits UTF-16 lines. Reject malformed/control-containing names before they
    // become profile IDs or arguments, and collapse duplicate registrations.
    private static IReadOnlyList<string> ParseWslDistributions(string output) => output
        .Split('\n')
        .Select(line => line.TrimEnd('\r', '\0').TrimStart('\uFEFF'))
        .Where(name => name.Length > 0 && !name.Any(char.IsControl))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    // Quote one argument according to the Windows command-line backslash/quote rules.
    private static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') result.Append('\\', slashes * 2 + 1);
            else result.Append('\\', slashes);
            result.Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }

    public static ShellProfile Default => DiscoverBuiltIn().FirstOrDefault() ?? DiscoverWsl().FirstOrDefault()
        ?? throw new FileNotFoundException("No supported Windows shell was found.");
}
