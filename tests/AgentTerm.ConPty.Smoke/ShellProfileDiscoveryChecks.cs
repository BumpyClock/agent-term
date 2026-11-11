using System.Reflection;
using System.Runtime.InteropServices;
using AgentTerm.Terminal;

internal static class ShellProfileDiscoveryChecks
{
    public static void Run()
    {
        var type = typeof(ShellProfile);
        var parse = type.GetMethod("ParseWslDistributions", BindingFlags.NonPublic | BindingFlags.Static)!;
        var quote = type.GetMethod("QuoteArgument", BindingFlags.NonPublic | BindingFlags.Static)!;
        var names = (IReadOnlyList<string>)parse.Invoke(null, ["\uFEFFUbuntu\r\nFedora Linux\r\nUbuntu\r\nbad\0name\r\n\r\n"])!;
        if (!names.SequenceEqual(["Ubuntu", "Fedora Linux"]))
            throw new Exception("WSL list parsing must preserve names and reject duplicates and control characters.");
        if (!OperatingSystem.IsWindows()) return;
        foreach (var name in new[] { "Ubuntu", "Fedora Linux", "name\"quote", "name\\", "name\\\"quoted" })
        {
            var argument = (string)quote.Invoke(null, [name])!;
            var argv = CommandLineToArgvW("wsl.exe --distribution " + argument + " --cd ~", out var count);
            if (argv == IntPtr.Zero) throw new Exception("CommandLineToArgvW failed.");
            try
            {
                if (count != 5 || Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, 2 * IntPtr.Size)) != name)
                    throw new Exception("WSL names must round-trip as one command-line argument.");
            }
            finally { LocalFree(argv); }
        }
        var profiles = ShellProfile.Discover();
        if (profiles.Any(p => p.Id is "pwsh" or "powershell" && p.Arguments != "-NoLogo"))
            throw new Exception("PowerShell profiles must load the user's shell profile.");
        if (profiles.Any(p => p.Id == "wsl" || p.Id.StartsWith("wsl:", StringComparison.Ordinal) && p.Name != p.Id[4..]))
            throw new Exception("WSL profiles must represent individual installed distributions.");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
