using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using AgentTerm.Terminal;

if (args.Contains("--tui-fixture"))
{
    FullScreenFixture.Run();
    return;
}

if (args.Contains("--mouse-fixture"))
{
    MouseSessionChecks.RunFixture();
    return;
}

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("AgentTerm.ConPty smoke checks require Windows. Skipped.");
    return;
}

var timeout = TimeSpan.FromSeconds(20);
ShellProfileDiscoveryChecks.Run();
await TerminalEnvironmentChecks.RunAsync();
await FullScreenChecks.RunAsync();
await MouseSessionChecks.RunAsync();
await ScrollbackSessionChecks.RunAsync();
await ProfileChecks.RunAsync();
await DirectoryChecks.RunAsync();
await SmartPromptChecks.RunAsync();
var prompt = new Regex(@"(?m)^PS [^\r\n]*> ?$", RegexOptions.CultureInvariant);
var id = Guid.NewGuid().ToString("N")[..8];

async Task WaitForScreen(TerminalSession session, Func<string, bool> expected, string message)
{
    var elapsed = Stopwatch.StartNew();
    string screen;
    do
    {
        screen = session.ReadText();
        if (expected(screen))
            return;
        if (session.Completion.IsCompleted)
        {
            var result = session.Completion.IsCompletedSuccessfully
                ? $"Exit code {session.Completion.Result}."
                : session.Completion.Exception?.ToString() ?? "Completion canceled.";
            throw new InvalidOperationException($"{message} Shell ended before the expected output. {result}\nScreen:\n{screen}");
        }
        await Task.Delay(50);
    } while (elapsed.Elapsed < timeout);

    throw new TimeoutException($"{message} Timed out after {timeout.TotalSeconds} seconds.\nScreen:\n{screen}");
}

async Task ExpectCommand(TerminalSession session, string command, string expected)
{
    session.Send(command + "\r");
    await WaitForScreen(session, screen =>
    {
        var output = screen.LastIndexOf(expected, StringComparison.Ordinal);
        return output >= 0 && prompt.IsMatch(screen[(output + expected.Length)..]);
    }, $"Expected command output <{expected}> followed by a PowerShell prompt.");
}

async Task DisposeSession(TerminalSession session)
{
    var screen = "Snapshot unavailable while cleanup is waiting for the terminal.";
    var cleanup = Task.Run(async () =>
    {
        try { screen = session.ReadText(); }
        catch (ObjectDisposedException) { screen = "Session already disposed."; }
        await session.DisposeAsync();
    });
    try
    {
        await cleanup.WaitAsync(timeout);
    }
    catch (TimeoutException exception)
    {
        throw new TimeoutException($"Session disposal did not finish.\nLast screen:\n{screen}", exception);
    }
}

static void AssertExited(Process process, string screen)
{
    if (!process.HasExited)
        throw new InvalidOperationException($"Owned PowerShell process {process.Id} is still running after session disposal.\nLast screen:\n{screen}");
}

var session = new TerminalSession(120, 30, TestShell.Profile);
try
{
    session.Start();
    await WaitForScreen(session, screen => prompt.IsMatch(screen), "PowerShell should display its initial prompt.");
    using var process = Process.GetProcessById(session.ProcessId);

    await ExpectCommand(session, $"Write-Output ('AT_{id}:'+'hello')", $"AT_{id}:hello");
    await ExpectCommand(session, $"Write-Output ('AT_{id}:'+'é€🙂界')", $"AT_{id}:é€🙂界");
    await ExpectCommand(session, $"$global:agentTermSmoke='retained'; Write-Output ('AT_{id}:'+'stored')", $"AT_{id}:stored");
    await ExpectCommand(session, $"Write-Output ('AT_{id}:'+$global:agentTermSmoke)", $"AT_{id}:retained");

    session.Resize(101, 28);
    await ExpectCommand(session, $"Write-Output ('AT_{id}:'+ [Console]::WindowWidth+'x'+[Console]::WindowHeight)", $"AT_{id}:101x28");

    var running = $"AT_{id}:running";
    session.Send($"Write-Output ('AT_{id}:'+'running'); Start-Sleep -Seconds 60\r");
    await WaitForScreen(session, screen => screen.Contains(running, StringComparison.Ordinal), "The interruptible command should start.");
    session.Send("\x03");
    await WaitForScreen(session, screen =>
    {
        var output = screen.LastIndexOf(running, StringComparison.Ordinal);
        return output >= 0 && prompt.IsMatch(screen[(output + running.Length)..]);
    }, "Ctrl+C should interrupt the command and restore the prompt.");
    await ExpectCommand(session, $"Write-Output ('AT_{id}:'+'after-interrupt')", $"AT_{id}:after-interrupt");

    var finalOutput = $"AT_{id}:final-output";
    session.Send($"Write-Output ('AT_{id}:'+'final-output'); exit 7\r");
    int exitCode;
    try
    {
        exitCode = await session.Completion.WaitAsync(timeout);
    }
    catch (TimeoutException exception)
    {
        throw new TimeoutException($"PowerShell should exit after the exit command.\nScreen:\n{session.ReadText()}", exception);
    }
    var finalScreen = session.ReadText();
    if (exitCode != 7 || !finalScreen.Contains(finalOutput, StringComparison.Ordinal))
        throw new InvalidOperationException($"Completion should report exit code 7 and retain final output. Exit code {exitCode}.\nScreen:\n{finalScreen}");
    await DisposeSession(session);
    AssertExited(process, finalScreen);
}
finally
{
    await DisposeSession(session);
}

for (var cycle = 0; cycle < 2; cycle++)
{
    var active = new TerminalSession(profile: TestShell.Profile);
    try
    {
        active.Start();
        await WaitForScreen(active, screen => prompt.IsMatch(screen), $"Cleanup cycle {cycle + 1} should start PowerShell.");
        using var process = Process.GetProcessById(active.ProcessId);
        var closing = $"AT_{id}:closing-{cycle}";
        active.Send($"Write-Output ('AT_{id}:'+'closing-{cycle}'); Start-Sleep -Seconds 60\r");
        await WaitForScreen(active, screen => screen.Contains(closing, StringComparison.Ordinal), "The command should be running before disposal.");
        var closingScreen = active.ReadText();
        await DisposeSession(active);
        AssertExited(process, closingScreen);
    }
    finally
    {
        await DisposeSession(active);
    }
}

var stress = new TerminalSession(120, 30, TestShell.Profile);
try
{
    stress.Start();
    await WaitForScreen(stress, screen => prompt.IsMatch(screen), "The resize stress session should start.");
    using var process = Process.GetProcessById(stress.ProcessId);
    stress.Send($"1..400 | % {{ Write-Output ('AT_{id}:'+'stress-'+$_); Start-Sleep -Milliseconds 10 }}\r");
    var stressMarker = new Regex($@"AT_{id}:stress-(\d+)", RegexOptions.CultureInvariant);
    await WaitForScreen(stress, screen => stressMarker.IsMatch(screen), "The bounded output stream should start.");
    var firstScreen = stress.ReadText();
    var firstNumber = stressMarker.Matches(firstScreen).Select(match => int.Parse(match.Groups[1].Value)).Max();
    var lastNumber = firstNumber;
    var lastScreen = firstScreen;
    var resizing = Task.Run(async () =>
    {
        for (var index = 0; index < 60; index++)
        {
            stress.Resize((ushort)(90 + index % 3 * 10), (ushort)(24 + index % 3));
            lastScreen = stress.ReadText();
            foreach (Match match in stressMarker.Matches(lastScreen))
                lastNumber = Math.Max(lastNumber, int.Parse(match.Groups[1].Value));
            await Task.Delay(10);
        }
    });
    try { await resizing.WaitAsync(timeout); }
    catch (TimeoutException exception)
    {
        throw new TimeoutException($"Resize and snapshot reads should finish during output.\nLast screen:\n{lastScreen}", exception);
    }
    if (lastNumber <= firstNumber)
        throw new InvalidOperationException($"Shell output should advance during resize and snapshot reads.\nLast screen:\n{lastScreen}");
    await DisposeSession(stress);
    AssertExited(process, lastScreen);
}
finally
{
    await DisposeSession(stress);
}

var failed = new ConPtyConnection(80, 24);
try
{
    var missingExecutable = Path.Combine(Path.GetTempPath(), $"agentterm-missing-{Guid.NewGuid():N}.exe");
    try
    {
        failed.Start(missingExecutable, "");
        throw new InvalidOperationException("Starting a missing executable should fail.");
    }
    catch (Win32Exception) { }
    try
    {
        await failed.Completion.WaitAsync(timeout);
        throw new InvalidOperationException("Startup failure should also fault Completion.");
    }
    catch (Win32Exception) { }
}
finally
{
    for (var attempt = 0; attempt < 2; attempt++)
        await Task.Run(async () => await failed.DisposeAsync()).WaitAsync(timeout);
}

Console.WriteLine("AgentTerm.ConPty smoke checks passed. PowerShell input, UTF-8, persistent state, resize, Ctrl+C, exit, cleanup, sustained output, and startup failure verified.");
