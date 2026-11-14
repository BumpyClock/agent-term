namespace AgentTerm_App;

internal sealed class TrailingThrottle(Action startTimer, Action stopTimer, Action run)
{
    private bool _scheduled;

    public void Request()
    {
        if (_scheduled) return;
        _scheduled = true;
        startTimer();
    }

    public void Flush()
    {
        Cancel();
        run();
    }

    public void Cancel()
    {
        if (!_scheduled) return;
        _scheduled = false;
        stopTimer();
    }
}
