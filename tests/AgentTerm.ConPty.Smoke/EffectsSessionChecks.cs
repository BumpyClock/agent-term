using AgentTerm.Terminal;

/// <summary>Session-level checks that effects reach subscribers outside the session locks, in order, and end with the shell.</summary>
internal static class EffectsSessionChecks
{
    internal static async Task RunAsync()
    {
        var batches = new List<IReadOnlyList<TerminalEffect>>();
        var lockProbe = new List<bool>();
        TerminalSession? current = null;
        await using (var session = new TerminalSession(100, 20, TestShell.Profile))
        {
            current = session;
            session.ConfigureEffects(TerminalEffectOptions.Default with { Bell = BellStyle.Both });
            session.EffectsAvailable += batch =>
            {
                // The handler runs on the reader thread. If it still held the terminal lock, another thread could not read the screen.
                lockProbe.Add(Task.Run(() => session.ReadText()).Wait(TimeSpan.FromSeconds(3)));
                lock (batches) batches.Add(batch);
            };
            session.Start();
            await WaitAsync(() => session.PromptState == ShellPromptState.Ready, "PowerShell should reach its prompt.");

            session.Send("[Console]::Write([char]27 + ']9;4;1;42' + [char]7 + [char]27 + ']9;Build finished' + [char]7 + [char]7); Start-Sleep -Seconds 1\r");
            await WaitAsync(() => Flat(batches).Any(effect => effect is ProgressEffect { State: TerminalProgressState.Set, Percent: 42 }) &&
                Flat(batches).Any(effect => effect is NotificationEffect { Body: "Build finished" }) &&
                Flat(batches).Any(effect => effect is BellEffect), "Progress, notification, and bell sequences should reach EffectsAvailable.");
            var order = Flat(batches).Select(effect => effect.GetType().Name).ToList();
            if (order.IndexOf(nameof(ProgressEffect)) > order.IndexOf(nameof(NotificationEffect)))
                throw new Exception($"Effects should keep their request order, got {string.Join(", ", order)}.");
            if (lockProbe.Count == 0 || lockProbe.Any(ok => !ok))
                throw new Exception("EffectsAvailable must run outside the terminal lock.");

            // A throwing subscriber must not stop output.
            session.EffectsAvailable += _ => throw new InvalidOperationException("subscriber fault");
            session.Send("[Console]::Write([char]27 + ']9;4;0' + [char]7); Write-Host 'effects-alive'\r");
            await WaitAsync(() => session.ReadText().Contains("effects-alive", StringComparison.Ordinal), "Output should continue after a subscriber throws.");
            await WaitAsync(() => Flat(batches).Any(effect => effect is ProgressEffect { State: TerminalProgressState.Remove }), "Removing progress should reach the host.");

            // Progress still showing when the shell exits is removed for the host, since its program can no longer clear it.
            session.Send("[Console]::Write([char]27 + ']9;4;1;10' + [char]7); Start-Sleep -Milliseconds 300; exit\r");
            await WaitAsync(() => Flat(batches).Count(effect => effect is ProgressEffect) >= 4 &&
                Flat(batches).OfType<ProgressEffect>().Last().State == TerminalProgressState.Remove,
                "A shell exit should remove progress that was showing.");
        }
        _ = current;
        Console.WriteLine("Effects session checks passed delivery outside the lock, ordering, subscriber faults, and exit cleanup.");
    }

    private static List<TerminalEffect> Flat(List<IReadOnlyList<TerminalEffect>> batches)
    {
        lock (batches) return batches.SelectMany(batch => batch).ToList();
    }

    private static async Task WaitAsync(Func<bool> condition, string message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            while (!condition()) await Task.Delay(40, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new Exception(message);
        }
    }
}
