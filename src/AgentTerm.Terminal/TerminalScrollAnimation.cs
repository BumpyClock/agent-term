namespace AgentTerm.Terminal;

/// <summary>Bounded row motion. The native viewport follows IntegerOffset on every tick.</summary>
public sealed class TerminalScrollAnimation
{
    private double _requested, _start, _elapsed;
    public double Position { get; private set; }
    public double Target { get; private set; }
    public bool IsActive { get; private set; }
    public long IntegerOffset => (long)Math.Floor(Position);
    public double Fraction => Position - IntegerOffset;

    public void Reset(long offset)
    {
        Position = Target = _requested = offset;
        IsActive = false;
    }

    public void Add(double rows, long offset, long maximum, bool animate)
    {
        if (!IsActive && IntegerOffset != offset) Reset(offset);
        _requested = Math.Clamp(_requested + rows, 0, maximum);
        Target = Math.Clamp(Math.Round(_requested), 0, maximum);
        _start = Position;
        _elapsed = 0;
        IsActive = animate && Target != Position;
        if (!IsActive) Position = Target;
    }

    public void Advance(double seconds)
    {
        if (!IsActive) return;
        _elapsed += Math.Max(0, seconds);
        var t = Math.Clamp(_elapsed / .14, 0, 1);
        var eased = t * t * (3 - 2 * t);
        Position = _start + (Target - _start) * eased;
        if (t >= 1) { Position = Target; IsActive = false; }
    }

    public static bool UseHistoryScrolling(bool mouseTracking, bool shift) => !mouseTracking || shift;
}
