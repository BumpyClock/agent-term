using System.Text;
using AgentTerm.Agents;
using AgentTerm.Copilot;
using GitHub.Copilot;

internal static class DeliveryBudgetChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static AssistantMessageDeltaEvent Delta(string text, string id = "message") => new() { Data = new() { MessageId = id, DeltaContent = text } };
    private static AssistantMessageEvent Final(string text, string id = "message") => new() { Data = new() { MessageId = id, Content = text } };
    private static SessionIdleEvent Idle() => new() { Data = new() };

    internal static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var turn = Guid.NewGuid();
        var mailbox = new AgentEventMailbox();
        const int count = 10_000;
        var chunk = new string('x', 80);
        for (var i = 0; i < count; i++) Check(mailbox.TryWrite(new TextDelta(turn, chunk)), "An ordinary burst must coalesce without overload.");
        var backlog = mailbox.Backlog;
        Check(backlog.Events < 30 && backlog.Characters == count * chunk.Length, "Coalescing bounds event overhead and retains every character.");
        var request = new ApprovalRequest(turn, Guid.NewGuid(), "permission");
        Check(mailbox.TryWrite(request) && mailbox.TryWrite(new TextDelta(turn, "after approval")) && mailbox.TryWrite(new TurnCompleted(turn)), "Control events fit after the burst.");
        mailbox.Complete();
        var delivered = new List<AgentEvent>();
        await foreach (var item in mailbox.ReadAllAsync(timeout.Token)) delivered.Add(item);
        var approval = delivered.IndexOf(request);
        Check(approval > 0 && string.Concat(delivered.Take(approval).Cast<TextDelta>().Select(x => x.Text)) == new string('x', 800_000), "Text before approval is exact and ordered.");
        Check(delivered[approval + 1] is TextDelta { Text: "after approval" } && delivered[approval + 2] is TurnCompleted, "Batching cannot cross approval or completion.");
        Console.WriteLine($"Agent delivery burst: {count} original text events -> {backlog.Events} queued batches; {backlog.Characters} characters preserved.");
        var baselineTimeline = new AgentTimeline();
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < count; i++) baselineTimeline.Apply(new TextDelta(turn, chunk));
        var baselineAllocation = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        var batchedTimeline = new AgentTimeline();
        allocationStart = GC.GetAllocatedBytesForCurrentThread();
        foreach (var item in delivered.Take(approval)) batchedTimeline.Apply(item);
        var batchedAllocation = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        Check(baselineTimeline.Entries.OfType<TimelineMessage>().Single().Text == batchedTimeline.Entries.OfType<TimelineMessage>().Single().Text, "Coalescing preserves bounded timeline text and its omission marker.");
        Check(batchedAllocation < baselineAllocation / 10, "Batch delivery must remove repeated full-text copies in the presentation model.");
        Console.WriteLine($"Timeline application allocation for same burst: individual deltas {baselineAllocation:N0} bytes; batches {batchedAllocation:N0} bytes. Producer allocations excluded.");

        // Count and byte bounds are independent. Oversized individual events must not enter the queue.
        mailbox = new AgentEventMailbox();
        Check(!mailbox.TryWrite(new TextDelta(turn, new string('x', AgentEventMailbox.CharacterLimit + 1))) && mailbox.Backlog == (0, 0), "Oversized event is rejected before retaining it.");
        for (var i = 0; i < AgentEventMailbox.EventLimit; i++) Check(mailbox.TryWrite(new ToolExecuting(turn, i.ToString())), "Events within the count budget are accepted.");
        Check(!mailbox.TryWrite(new TurnCompleted(turn)) && mailbox.Backlog.Events == AgentEventMailbox.EventLimit, "Non-text events cannot exceed the count budget.");

        await using (var session = new CopilotHarness.Session(null, (_, _) => Task.CompletedTask))
        {
            await session.SubmitAsync(new(turn, "Unicode"));
            session.OnEvent(Delta("a\ud83e"));
            session.OnEvent(Delta("\udd89"));
            session.OnEvent(Final("a🦉 tail"));
            session.OnEvent(Final("a🦉 tail"));
            session.OnEvent(Final("revised 🦉"));
            session.OnEvent(Idle());
            await using var events = session.Events(timeout.Token).GetAsyncEnumerator();
            var output = new StringBuilder();
            while (await events.MoveNextAsync() && events.Current is TextDelta text) output.Append(text.Text);
            Check(output.ToString() == "a🦉 tail\n[Final response differs from streamed text]\nrevised 🦉", "Reconciliation preserves split surrogates, suffixes, repeated finals and differing finals.");
            Check(events.Current is TurnCompleted, "Reconciliation preserves idle completion.");
        }

        var abortEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CopilotHarness.Session? overloaded = null;
        await using (overloaded = new CopilotHarness.Session(null, (_, _) => Task.CompletedTask, _ =>
        {
            abortEntered.TrySetResult();
            overloaded!.OnEvent(Idle());
            return Task.CompletedTask;
        }))
        {
            await overloaded.SubmitAsync(new(turn, "overflow"));
            overloaded.OnEvent(Delta("accepted prefix"));
            var permission = overloaded.RequestPermissionAsync("pending approval", "call");
            overloaded.OnEvent(Delta(new string('x', AgentEventMailbox.CharacterLimit + 1)));
            Check(!await permission.WaitAsync(timeout.Token), "Overflow declines pending approval without blocking SDK callbacks.");
            await abortEntered.Task.WaitAsync(timeout.Token);
            var items = new List<AgentEvent>();
            var failed = false;
            try { await foreach (var item in overloaded.Events(timeout.Token)) items.Add(item); }
            catch (IOException ex) { failed = ex.Message.Contains("pending event budget", StringComparison.Ordinal); }
            Check(failed && items is [TextDelta { Text: "accepted prefix" }, ApprovalRequest], "Accepted events drain in order before explicit overload failure.");
            var rejected = false;
            try { await overloaded.SubmitAsync(new(Guid.NewGuid(), "retry")); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Overload cannot revive on late idle.");
        }

        CopilotHarness.Session? responses = null;
        await using (responses = new CopilotHarness.Session(null, (_, _) => Task.CompletedTask, _ => { responses!.OnEvent(Idle()); return Task.CompletedTask; }))
        {
            await responses.SubmitAsync(new(turn, "response count"));
            for (var i = 0; i <= CopilotHarness.Session.ReconciliationLimit; i++) responses.OnEvent(Final("", i.ToString()));
            var failed = false;
            try { await foreach (var item in responses.Events(timeout.Token)) { } }
            catch (IOException ex) { failed = ex.Message.Contains("response count budget", StringComparison.Ordinal); }
            Check(failed, "Finalized response metadata is bounded even with a fast consumer.");
        }

        MeasureReconciliationAllocation();
        await OverflowDuringExecutionAsync(timeout.Token);
        Console.WriteLine("Agent delivery budgets, overload failure/approval ordering, and Unicode reconciliation checks passed.");
    }

    private static async Task OverflowDuringExecutionAsync(CancellationToken cancellation)
    {
        var terminal = new HeldTerminal();
        var aborts = 0;
        await using var session = new CopilotHarness.Session(null, (_, _) => Task.CompletedTask,
            _ => { Interlocked.Increment(ref aborts); return Task.CompletedTask; }, terminal: terminal);
        await session.SubmitAsync(new(Guid.NewGuid(), "owned execution"));
        var execution = session.RunInTerminalAsync("owned command", "call");
        await using var events = session.Events(cancellation).GetAsyncEnumerator();
        Check(await events.MoveNextAsync() && events.Current is ApprovalRequest, "Execution first requests permission.");
        await session.RespondAsync(new(((ApprovalRequest)events.Current).Request, Approved: true));
        await terminal.Started.Task.WaitAsync(cancellation);
        session.OnEvent(Delta(new string('x', AgentEventMailbox.CharacterLimit + 1)));
        await terminal.Cancelled.Task.WaitAsync(cancellation);
        Check(Volatile.Read(ref aborts) == 0 && !execution.IsCompleted, "Overflow retains terminal ownership until command interruption settles.");
        var disposal = session.DisposeAsync().AsTask();
        Check(!disposal.IsCompleted, "Session disposal also awaits owned terminal execution.");
        terminal.Release.SetResult();
        Check((await execution.WaitAsync(cancellation)).Status == TerminalExecutionStatus.Interrupted, "Owned execution reports its interruption.");
        await disposal.WaitAsync(cancellation);
    }

    private sealed class HeldTerminal : IHostTerminal
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TerminalExecutionResult> ExecuteAsync(TerminalExecutionRequest request, CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            Started.SetResult();
            await Release.Task;
            return new(TerminalExecutionStatus.Interrupted);
        }
    }

    private static void MeasureReconciliationAllocation()
    {
        var chunk = new string('x', 4096);
        // Warm up both implementations; compare only append work over the same 4 Mi UTF-16 characters.
        using (var warm = new ResponseReconciliation()) { warm.Append(chunk); warm.IsPrefixOf(chunk); }
        _ = new StringBuilder().Append(chunk).ToString();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var old = new StringBuilder();
        for (var i = 0; i < 1024; i++) old.Append(chunk);
        var baseline = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        using var current = new ResponseReconciliation();
        for (var i = 0; i < 1024; i++) current.Append(chunk);
        var changed = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(current.Length == old.Length && changed < baseline / 100, "Response reconciliation allocation must stay independent of response length.");
        Console.WriteLine($"Response reconciliation append allocation: StringBuilder {baseline:N0} bytes; digest {changed:N0} bytes; {old.Length:N0} UTF-16 characters. Native hash state not included.");
        GC.KeepAlive(old);
    }
}
