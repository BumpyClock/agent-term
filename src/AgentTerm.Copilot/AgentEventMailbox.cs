using System.Runtime.CompilerServices;
using System.Text;
using AgentTerm.Agents;

namespace AgentTerm.Copilot;

/// <summary>One ordered consumer. SDK callbacks never block on the UI; overload ends the stream explicitly.</summary>
internal sealed class AgentEventMailbox
{
    internal const int EventLimit = 256;
    internal const int CharacterLimit = 1024 * 1024;
    private const int BatchCharacters = 32 * 1024;
    private readonly Lock _gate = new();
    private readonly Queue<Pending> _pending = new();
    private Pending? _tail;
    private int _characters;
    private bool _completed;
    private Exception? _error;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Pending(AgentEvent item, int characters)
    {
        internal AgentEvent Item { get; } = item;
        internal int Characters = characters;
        internal StringBuilder? Text;
        internal AgentEvent Take() => Text is null ? Item : new TextDelta(Item.Turn, Text.ToString());
    }

    internal (int Events, int Characters) Backlog { get { lock (_gate) return (_pending.Count, _characters); } }

    internal bool TryWrite(AgentEvent item)
    {
        lock (_gate)
        {
            if (_completed) return false;
            var characters = Characters(item);
            var merge = item is TextDelta && _tail?.Item is TextDelta && _tail.Item.Turn == item.Turn &&
                characters <= BatchCharacters - _tail.Characters;
            if (characters > CharacterLimit - _characters || (!merge && _pending.Count == EventLimit)) return false;
            if (merge)
            {
                _tail!.Text ??= new StringBuilder(((TextDelta)_tail.Item).Text);
                _tail.Text.Append(((TextDelta)item).Text);
                _tail.Characters += (int)characters;
            }
            else
            {
                _tail = new Pending(item, (int)characters);
                _pending.Enqueue(_tail);
            }
            _characters += (int)characters;
            Pulse();
            return true;
        }
    }

    internal void Complete(Exception? error = null)
    {
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            _error = error;
            Pulse();
        }
    }

    private void Pulse()
    {
        // Allocate a wakeup only when a reader actually begins waiting again.
        _changed.TrySetResult();
    }

    internal async IAsyncEnumerable<AgentEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            AgentEvent? item = null;
            Task? wait = null;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_pending.TryDequeue(out var pending))
                {
                    _characters -= pending.Characters;
                    if (_pending.Count == 0) _tail = null;
                    item = pending.Take();
                }
                else if (_completed)
                {
                    if (_error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_error).Throw();
                    yield break;
                }
                else
                {
                    if (_changed.Task.IsCompleted) _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = _changed.Task;
                }
            }
            if (item is not null) yield return item;
            else await wait!.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static long Characters(AgentEvent item) => item switch
    {
        TextDelta text => text.Text.Length,
        ToolStarted tool => (long)tool.CallId.Length + tool.Name.Length + tool.Action.Length + tool.Detail.Length,
        ToolOutput output => (long)output.CallId.Length + output.Text.Length,
        ToolExecuting executing => executing.CallId.Length,
        ToolCompleted completed => completed.CallId.Length,
        ReasoningSummary summary => summary.Text.Length,
        ApprovalRequest approval => (long)approval.Description.Length + (approval.CallId?.Length ?? 0),
        QuestionRequest question => (long)question.Question.Length + question.Choices.Sum(choice => 1L + choice.Length),
        TurnFailed failure => failure.Message.Length,
        _ => 0,
    };
}
