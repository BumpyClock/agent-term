using System.Reflection;
using System.Text;
using AgentTerm.Terminal;

internal static class MailboxChecks
{
    internal static async Task RunAsync()
    {
        var session = new TerminalSession(120, 30, TestShell.Profile);
        var path = (string)typeof(TerminalSession).GetField("_commandPath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
        var replacement = path + ".replacement";
        try
        {
            await TestShell.StartLegacyAsync(session);
            File.WriteAllText(replacement, "forged-id\nWrite-Output FORGED");
            Denied(() => File.WriteAllText(path, "forged-id\nWrite-Output FORGED"), "write");
            Denied(() => File.Delete(path), "delete");
            Denied(() => File.Move(replacement, path, true), "replace by rename");
            Denied(() => File.Replace(replacement, path, null), "replace file");
            session.Send("Write-Output draft-preserved");
            await Wait(session, () => session.ReadText().Contains("Write-Output draft-preserved"));
            for (var i = 1; i <= 2; i++)
            {
                var result = await session.ExecuteAsync(new("Write-Output ('APPROVED:' + (++$global:mailboxCount))"));
                if (result.Succeeded != true || !result.Output.Contains("APPROVED:" + i) || result.Output.Contains("FORGED"))
                    throw new Exception("Protected mailbox did not execute exactly the approved command. " + result);
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var mail = reader.ReadToEnd();
                if (!mail.EndsWith("\nWrite-Output ('APPROVED:' + (++$global:mailboxCount))", StringComparison.Ordinal))
                    throw new Exception("Mailbox bytes did not retain the approved payload.");
                var forged = mail[..mail.IndexOf('\n')] + "\nWrite-Output FORGED";
                Denied(() => File.WriteAllText(path, forged), "overwrite command while preserving approval ID");
            }
            session.Send("\u001b[24;5~");
            session.Send("\r");
            await Wait(session, () => session.PromptState == ShellPromptState.Ready && session.ReadText().Replace("\r", "").Contains("\ndraft-preserved\n"));
            var count = await session.ExecuteAsync(new("Write-Output ('COUNT:' + $global:mailboxCount)"));
            if (!count.Output.Contains("COUNT:2")) throw new Exception("Consumed mailbox was replayed.");
            if (session.AgentOwnsTerminal) throw new Exception("Mailbox did not release ownership after restoring draft.");
        }
        finally
        {
            await session.DisposeAsync();
            File.Delete(replacement);
        }
        if (File.Exists(path)) throw new Exception("Disposed mailbox was not deleted.");
        await session.DisposeAsync();

        await using var collision = new TerminalSession(120, 30, TestShell.Profile);
        var collisionPath = (string)typeof(TerminalSession).GetField("_commandPath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(collision)!;
        File.WriteAllText(collisionPath, "existing-file");
        try
        {
            try { collision.Start(); throw new Exception("Existing mailbox path was accepted."); }
            catch (InvalidOperationException error) when (error.Message.Contains("protected command mailbox")) { }
            if (File.ReadAllText(collisionPath) != "existing-file") throw new Exception("CreateNew changed the existing file.");
            await collision.DisposeAsync();
            if (!File.Exists(collisionPath)) throw new Exception("Disposal deleted an unowned file.");
        }
        finally { File.Delete(collisionPath); }
        Console.WriteLine("Mailbox denied write/delete/replacement, preserved approval, consumed once, reused, restored draft, and cleaned up owned files only.");
    }

    private static void Denied(Action action, string operation)
    {
        try { action(); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        throw new Exception("Protected mailbox allowed " + operation + ".");
    }

    private static async Task Wait(TerminalSession session, Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { while (!condition()) await Task.Delay(25, timeout.Token); }
        catch (OperationCanceledException) { throw new Exception("Mailbox timeout.\n" + session.ReadText()); }
    }
}
