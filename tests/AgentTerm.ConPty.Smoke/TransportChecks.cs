using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AgentTerm.Terminal;

internal static class TransportChecks
{
    private const int PayloadSize = 256 * 1024;

    internal static void RunFixture(bool hold)
    {
        if (!SetConsoleMode(GetStdHandle(-10), 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        Console.WriteLine("TRANSPORT_READY");
        if (hold) { Thread.Sleep(Timeout.Infinite); return; }
        var bytes = new byte[PayloadSize];
        Console.OpenStandardInput().ReadExactly(bytes);
        Console.WriteLine("TRANSPORT_SHA256=" + Convert.ToHexString(SHA256.HashData(bytes)));
    }

    internal static async Task RunAsync()
    {
        var executable = Environment.ProcessPath!;
        var prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? "\"" + Assembly.GetExecutingAssembly().Location + "\" " : "";
        foreach (var hold in new[] { false, true })
        {
            await using var connection = new ConPtyConnection(100, 10);
            var output = new StringBuilder();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.OutputReceived += bytes =>
            {
                lock (output)
                {
                    output.Append(Encoding.UTF8.GetString(bytes.Span));
                    if (output.ToString().Contains("TRANSPORT_READY")) ready.TrySetResult();
                }
            };
            connection.Start(executable, prefix + (hold ? "--transport-hold-fixture" : "--transport-fixture"));
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var payload = Enumerable.Range(0, hold ? PayloadSize * 4 : PayloadSize).Select(i => (byte)('a' + i % 26)).ToArray();
            var expected = Convert.ToHexString(SHA256.HashData(payload));
            connection.Send(payload);
            Array.Fill(payload, (byte)'x');
            if (hold) await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            else
            {
                if (await connection.Completion.WaitAsync(TimeSpan.FromSeconds(20)) != 0) throw new Exception("Transport fixture failed.");
                lock (output)
                    if (!output.ToString().Contains("TRANSPORT_SHA256=" + expected))
                        throw new Exception("Large input must arrive byte-for-byte from owned queue memory, and final output must drain before completion.\n" + output);
            }
        }
        for (var i = 0; i < 10; i++)
        {
            await using var quick = new ConPtyConnection(80, 10);
            quick.Start(Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe", "/d /c exit 7");
            if (await quick.Completion.WaitAsync(TimeSpan.FromSeconds(10)) != 7) throw new Exception("Rapid process exit must preserve its exit code.");
        }
        Console.WriteLine("ConPTY transport passed large owned input, blocked-writer disposal, final-output draining, and rapid exit notification.");
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int kind);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetConsoleMode(IntPtr handle, uint mode);
}
