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

    internal static void RunBurstFixture()
    {
        var line = Encoding.ASCII.GetBytes("\u001b[32mburst\u001b[0m " + new string('x', 80) + "\r\n");
        var block = new byte[64 * 1024 / line.Length * line.Length];
        for (var offset = 0; offset < block.Length; offset += line.Length) line.CopyTo(block, offset);
        using var stdout = Console.OpenStandardOutput();
        for (long written = 0; written < BurstSize; written += block.Length) stdout.Write(block);
        stdout.Write("\r\nBURST_DONE\r\n"u8);
    }

    private const long BurstSize = 16L << 20;

    internal static async Task RunAsync()
    {
        var executable = Environment.ProcessPath!;
        var prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? "\"" + Assembly.GetExecutingAssembly().Location + "\" " : "";
        await CheckBurstAsync(executable, prefix);
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

    private static async Task CheckBurstAsync(string executable, string prefix)
    {
        await using var connection = new ConPtyConnection(120, 30);
        long received = 0;
        int chunks = 0, largest = 0;
        var tail = "";
        var done = false;
        connection.OutputReceived += bytes =>
        {
            received += bytes.Length;
            chunks++;
            largest = Math.Max(largest, bytes.Length);
            tail += Encoding.ASCII.GetString(bytes.Span[Math.Max(0, bytes.Length - 64)..]);
            if (tail.Length > 64) tail = tail[^64..];
            done |= tail.Contains("BURST_DONE");
        };
        var elapsed = Stopwatch.StartNew();
        connection.Start(executable, prefix + "--transport-burst-fixture");
        if (await connection.Completion.WaitAsync(TimeSpan.FromSeconds(60)) != 0) throw new Exception("Burst fixture failed.");
        elapsed.Stop();
        if (!done) throw new Exception("Burst output must drain through its final marker before completion.");
        if (largest <= 16 * 1024)
            throw new Exception($"Burst output arrived in {chunks} reads of at most {largest} bytes. 64 KiB producer writes should fill reads above 16 KiB.");
        Console.WriteLine($"ConPTY burst delivered {received / 1048576.0:F1} MiB in {chunks} reads (largest {largest / 1024} KiB) at {received / elapsed.Elapsed.TotalSeconds / 1048576:F1} MiB/s including process start.");
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int kind);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetConsoleMode(IntPtr handle, uint mode);
}
