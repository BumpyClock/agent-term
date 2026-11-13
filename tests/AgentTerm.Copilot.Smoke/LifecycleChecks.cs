using AgentTerm.Agents;
using AgentTerm.Copilot;
using GitHub.Copilot;

static class LifecycleChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static SessionIdleEvent Idle() => new() { Data = new() };
    private static SessionErrorEvent Error() => new() { Data = new() { Message = "fixture error", ErrorType = "fixture" } };
    public static async Task RunAsync()
    {
        await ApprovalOwnershipAsync();
        await CallbackOwnershipAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var abortEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAbort = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborts = 0;
        await using (var session = new CopilotHarness.Session(null, (_, _) => Task.CompletedTask, async _ =>
        {
            Interlocked.Increment(ref aborts); abortEntered.TrySetResult(); await releaseAbort.Task;
            throw new IOException("Old abort failed after native idle.");
        }))
        {
            var turn = Guid.NewGuid();
            await session.SubmitAsync(new(turn, "stop"));
            var first = session.InterruptAsync(turn);
            await abortEntered.Task.WaitAsync(timeout.Token);
            var second = session.InterruptAsync(turn);
            Check(ReferenceEquals(first, second), "Concurrent Stop must share the same native drain.");
            session.OnEvent(Idle());
            await session.SubmitAsync(new(Guid.NewGuid(), "new turn"));
            releaseAbort.SetResult();
            await Task.WhenAll(first, second).WaitAsync(timeout.Token);
            Check(aborts == 1, "Concurrent Stop must abort once.");
            await using var events = session.Events(timeout.Token).GetAsyncEnumerator();
            Check(await events.MoveNextAsync() && events.Current is TurnInterrupted interrupted && interrupted.Turn == turn, "Only the stopped turn is interrupted.");
            session.OnEvent(Idle());
            Check(await events.MoveNextAsync() && events.Current is TurnCompleted completed && completed.Turn != turn, "Old Stop cannot finish the new turn.");
        }
        abortEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var session = new CopilotHarness.Session(null, (_, _) => Task.CompletedTask, _ => { abortEntered.TrySetResult(); return Task.CompletedTask; }))
        {
            var turn = Guid.NewGuid();
            await session.SubmitAsync(new(turn, "error"));
            session.OnEvent(Error());
            await abortEntered.Task.WaitAsync(timeout.Token);
            var rejected = false;
            try { await session.SubmitAsync(new(Guid.NewGuid(), "too early")); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Error must not admit retry before native idle.");
            await using var events = session.Events(timeout.Token).GetAsyncEnumerator();
            var pending = events.MoveNextAsync().AsTask();
            Check(!pending.IsCompleted, "Failure must remain unpublished until drained.");
            session.OnEvent(Idle());
            Check(await pending && events.Current is TurnFailed failed && failed.Turn == turn && failed.Message == "fixture error", "Idle publishes the retained error for its original owner.");
            var next = Guid.NewGuid();
            await session.SubmitAsync(new(next, "retry"));
            session.OnEvent(Idle());
            Check(await events.MoveNextAsync() && events.Current is TurnCompleted completed && completed.Turn == next, "Retry completes only on its own idle.");
        }
        await using (var session = new CopilotHarness.Session(null, (_, _) => Task.CompletedTask, _ => Task.CompletedTask, TimeSpan.FromMilliseconds(50)))
        {
            await session.SubmitAsync(new(Guid.NewGuid(), "never idle"));
            session.OnEvent(Error());
            var failed = false;
            try { await foreach (var item in session.Events(timeout.Token)) { } } catch (IOException ex) { failed = ex.Message.Contains("fixture error"); }
            Check(failed, "Missing native idle must terminate the stream within the drain bound.");
            var rejected = false;
            session.OnEvent(Idle());
            try { await session.SubmitAsync(new(Guid.NewGuid(), "unusable")); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Late idle cannot revive a timed-out session.");
        }
        await using (var session = new CopilotHarness.Session(null, (_, _) => Task.CompletedTask))
        {
            var turn = Guid.NewGuid();
            await session.SubmitAsync(new(turn, "fixture"));
            session.OnEvent(new AssistantMessageEvent { Data = new() { MessageId = "one", Content = "final only" } });
            session.OnEvent(new AssistantMessageDeltaEvent { Data = new() { MessageId = "two", DeltaContent = "stream" } });
            session.OnEvent(new AssistantMessageEvent { Data = new() { MessageId = "two", Content = "stream tail" } });
            session.OnEvent(new AssistantMessageEvent { Data = new() { MessageId = "two", Content = "corrected final" } });
            session.OnEvent(Idle());
            await using var events = session.Events(timeout.Token).GetAsyncEnumerator();
            var text = "";
            while (await events.MoveNextAsync() && events.Current is TextDelta delta) text += delta.Text;
            Check(text == "final onlystream tail\n[Final response differs from streamed text]\ncorrected final", "Coalesced delivery preserves final-only text, suffix reconciliation, and labeled correction exactly.");
            Check(events.Current is TurnCompleted, "Native idle completes the turn without duplicating content.");
        }
        Console.WriteLine("Copilot native error/idle, concurrent Stop and bounded drain checks passed.");
    }
    private static async Task CallbackOwnershipAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var abortEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAbort = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var session = new CopilotHarness.Session(null, (_, _) => Task.CompletedTask, async _ => { abortEntered.TrySetResult(); await releaseAbort.Task; }))
        {
            var turn = Guid.NewGuid();
            await session.SubmitAsync(new(turn, "stopping"));
            var stopping = session.InterruptAsync(turn);
            await abortEntered.Task.WaitAsync(timeout.Token);
            var permission = session.RequestPermissionAsync("late permission", "late-call");
            var question = session.RequestInputAsync("late question", ["yes"], true);
            Check(permission.IsCompletedSuccessfully && !permission.Result, "A permission callback after Stop declines without waiting.");
            Check(question.IsCompletedSuccessfully && question.Result is { Approved: false, Answer: null }, "A question callback after Stop declines without waiting.");
            releaseAbort.SetResult();
            session.OnEvent(Idle());
            await stopping.WaitAsync(timeout.Token);
            await using var events = session.Events(timeout.Token).GetAsyncEnumerator();
            Check(await events.MoveNextAsync() && events.Current is TurnInterrupted interrupted && interrupted.Turn == turn, "A declined late callback emits no request.");
        }
        await using (var session = new CopilotHarness.Session(null, (_, _) => Task.CompletedTask, _ => Task.CompletedTask))
        {
            await using var events = session.Events(timeout.Token).GetAsyncEnumerator();
            var turn = Guid.NewGuid();
            await session.SubmitAsync(new(turn, "idle with pending requests"));
            var permission = session.RequestPermissionAsync("pending permission", "call");
            Check(await events.MoveNextAsync() && events.Current is ApprovalRequest approval && approval.Turn == turn && approval.CallId == "call", "Permission request names its owning turn.");
            var stale = ((ApprovalRequest)events.Current).Request;
            var question = session.RequestInputAsync("pending question", [], true);
            Check(await events.MoveNextAsync() && events.Current is QuestionRequest asked && asked.Turn == turn, "Question request names its owning turn.");
            session.OnEvent(Idle());
            Check(!await permission.WaitAsync(timeout.Token), "Native idle declines an outstanding permission.");
            Check((await question.WaitAsync(timeout.Token)) is { Approved: false, Answer: null }, "Native idle declines an outstanding question.");
            Check(await events.MoveNextAsync() && events.Current is TurnCompleted completed && completed.Turn == turn, "Idle completes the turn.");
            var orphan = session.RequestPermissionAsync("after idle", null);
            var orphanQuestion = session.RequestInputAsync("after idle", [], true);
            Check(orphan.IsCompletedSuccessfully && !orphan.Result && orphanQuestion.IsCompletedSuccessfully && orphanQuestion.Result.Answer is null, "A callback without an active turn declines without waiting.");
            var next = Guid.NewGuid();
            await session.SubmitAsync(new(next, "fresh turn"));
            var fresh = session.RequestPermissionAsync("fresh permission", "fresh-call");
            Check(await events.MoveNextAsync() && events.Current is ApprovalRequest freshApproval && freshApproval.Turn == next && freshApproval.Turn != Guid.Empty, "No orphan request precedes the fresh turn request.");
            await session.RespondAsync(new(stale, false));
            Check(!fresh.IsCompleted, "A stale response cannot settle the fresh request.");
            await session.RespondAsync(new(((ApprovalRequest)events.Current).Request, true));
            Check(await fresh.WaitAsync(timeout.Token), "Fresh turn approval succeeds.");
            session.OnEvent(Idle());
            Check(await events.MoveNextAsync() && events.Current is TurnCompleted done && done.Turn == next, "Fresh turn completes on its idle.");
        }
    }
    private sealed class Terminal : IHostTerminal
    {
        public List<string> Commands { get; } = [];
        public Func<CancellationToken, Task<TerminalExecutionResult>>? Execute;
        public Task<TerminalExecutionResult> ExecuteAsync(TerminalExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Commands.Add(request.Command);
            return Execute?.Invoke(cancellationToken) ?? Task.FromResult(new TerminalExecutionResult(TerminalExecutionStatus.Completed));
        }
    }
    private static async Task ApprovalOwnershipAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var transition in new[] { "stop", "new-turn", "dispose" })
        {
            var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var terminal = new Terminal();
            CopilotHarness.Session? session = null;
            await using (session = new(null, (_, _) => Task.CompletedTask, _ =>
            {
                session!.OnEvent(Idle());
                return Task.CompletedTask;
            }, terminal: terminal, approvalContinuation: async () => { paused.TrySetResult(); await resume.Task; }))
            {
                var turn = Guid.NewGuid();
                await session.SubmitAsync(new(turn, "old turn"));
                var command = session.RunInTerminalAsync("old command", "old-call");
                await using var events = session.Events(timeout.Token).GetAsyncEnumerator();
                Check(await events.MoveNextAsync() && events.Current is ApprovalRequest, "Command requests approval before execution.");
                await session.RespondAsync(new(((ApprovalRequest)events.Current).Request, true));
                await paused.Task.WaitAsync(timeout.Token);
                if (transition == "dispose") await session.DisposeAsync();
                else
                {
                    await session.InterruptAsync(turn).WaitAsync(timeout.Token);
                    if (transition == "new-turn") await session.SubmitAsync(new(Guid.NewGuid(), "new turn"));
                }
                resume.SetResult();
                Check((await command.WaitAsync(timeout.Token)).Status == TerminalExecutionStatus.Denied && terminal.Commands.Count == 0,
                    "An approved old command cannot start after " + transition + ".");
                if (transition == "new-turn")
                {
                    var fresh = session.RunInTerminalAsync("fresh command", "fresh-call");
                    while (await events.MoveNextAsync() && events.Current is not ApprovalRequest) { }
                    await session.RespondAsync(new(((ApprovalRequest)events.Current).Request, true));
                    Check((await fresh.WaitAsync(timeout.Token)).Status == TerminalExecutionStatus.Completed && terminal.Commands.SequenceEqual(new[] { "fresh command" }),
                        "Rejecting a stale approval must not disable the new turn.");
                }
            }
        }
        {
            var terminal = new Terminal();
            CopilotHarness.Session? session = null;
            await using (session = new(null, (_, _) => Task.CompletedTask, _ => { session!.OnEvent(Idle()); return Task.CompletedTask; }, terminal: terminal))
            {
                var turn = Guid.NewGuid();
                await session.SubmitAsync(new(turn, "unapproved"));
                var command = session.RunInTerminalAsync("must not execute", "call");
                await using var events = session.Events(timeout.Token).GetAsyncEnumerator();
                Check(await events.MoveNextAsync() && events.Current is ApprovalRequest, "Approval is pending.");
                var request = (ApprovalRequest)events.Current;
                await session.InterruptAsync(turn).WaitAsync(timeout.Token);
                await session.RespondAsync(new(request.Request, true));
                Check((await command.WaitAsync(timeout.Token)).Status == TerminalExecutionStatus.Denied && terminal.Commands.Count == 0,
                    "Stop before approval rejects a later response.");
            }
        }
        {
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var drained = new TaskCompletionSource<TerminalExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var terminal = new Terminal { Execute = token => { token.Register(() => canceled.TrySetResult()); return drained.Task; } };
            var abortEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CopilotHarness.Session? session = null;
            await using (session = new(null, (_, _) => Task.CompletedTask, _ => { abortEntered.SetResult(); session!.OnEvent(Idle()); return Task.CompletedTask; }, terminal: terminal))
            {
                var turn = Guid.NewGuid();
                await session.SubmitAsync(new(turn, "executing"));
                var command = session.RunInTerminalAsync("running command", "call");
                await using var events = session.Events(timeout.Token).GetAsyncEnumerator();
                await events.MoveNextAsync();
                await session.RespondAsync(new(((ApprovalRequest)events.Current).Request, true));
                Check(await events.MoveNextAsync() && events.Current is ToolExecuting, "Approved command starts on its owning turn.");
                var stopping = session.InterruptAsync(turn);
                await canceled.Task.WaitAsync(timeout.Token);
                Check(!stopping.IsCompleted && !abortEntered.Task.IsCompleted, "Stop cancels and drains registered execution before native abort.");
                drained.SetResult(new(TerminalExecutionStatus.Interrupted));
                await stopping.WaitAsync(timeout.Token);
                Check((await command).Status == TerminalExecutionStatus.Interrupted && terminal.Commands.Count == 1, "Stop preserves the terminal drain result.");
            }
        }
    }

}
