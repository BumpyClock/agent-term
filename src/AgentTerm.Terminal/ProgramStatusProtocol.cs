namespace AgentTerm.Terminal;

public enum ProgramState { Idle, Working, Done, Blocked, Error }

public sealed record ProgramStatus(string Id, ProgramState State, string? Kind, int? Progress,
    string? App, string? EffectiveApp, string? Title, string? Message, DateTimeOffset UpdatedAt)
{
    public bool IsActive { get; init; } = true;
}

/// <summary>
/// Keeps OSC 7501 records from native reports. libghostty-vt validates reports and answers support queries.
/// Access is serialized by the session.
/// </summary>
public sealed class ProgramStatusProtocol
{
    private readonly Dictionary<string, ProgramStatus> _records = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recency = new();
    private IReadOnlyList<ProgramStatus>? _snapshot;

    public int Version { get; private set; }

    public IReadOnlyList<ProgramStatus> ReadStatuses() => _snapshot ??= _records.Values
        .Select(record => record with { EffectiveApp = EffectiveApp(record.Id) })
        .ToArray();

    private void Touch()
    {
        Version++;
        _snapshot = null;
    }

    private string? EffectiveApp(string id)
    {
        while (true)
        {
            if (_records.TryGetValue(id, out var record) && record.App is not null) return record.App;
            if (id.Length == 0) return null;
            var slash = id.LastIndexOf('/');
            id = slash < 0 ? "" : id[..slash];
        }
    }

    public void EndCommand()
    {
        foreach (var record in _records.Values.ToArray())
        {
            if (record.State is ProgramState.Working or ProgramState.Blocked) Remove(record.Id);
            else if (record.IsActive) { _records[record.Id] = record with { IsActive = false }; Touch(); }
        }
    }

    public void Reset()
    {
        if (_records.Count == 0) return;
        _records.Clear();
        _recency.Clear();
        Touch();
    }

    /// <summary>Applies one natively validated OSC 7501 report. A report replaces its record completely.</summary>
    public void Apply(TerminalProgramStatusReport report)
    {
        var id = report.Id;
        if (report.State == ProgramStatusReportState.Clear)
        {
            foreach (var key in _records.Keys.Where(key => id.Length == 0 || key == id || key.StartsWith(id + "/", StringComparison.Ordinal)).ToArray()) Remove(key);
            return;
        }
        var state = report.State switch
        {
            ProgramStatusReportState.Working => ProgramState.Working, ProgramStatusReportState.Done => ProgramState.Done,
            ProgramStatusReportState.Blocked => ProgramState.Blocked, ProgramStatusReportState.Error => ProgramState.Error,
            _ => ProgramState.Idle
        };
        if (!_records.ContainsKey(id) && _records.Count == 256) Remove(_recency.First!.Value);
        _recency.Remove(id);
        _recency.AddLast(id);
        _records[id] = new(id, state, state == ProgramState.Blocked ? report.Kind : null,
            state is ProgramState.Working or ProgramState.Blocked ? report.Progress : null,
            report.App, report.App, report.Title, report.Message, DateTimeOffset.UtcNow);
        Touch();
    }

    private void Remove(string id)
    {
        if (!_records.Remove(id)) return;
        _recency.Remove(id);
        Touch();
    }
}
