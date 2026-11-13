namespace AgentTerm_App;

public sealed partial class TerminalSurface
{
    private double _scrollFraction;

    internal void SetScrollFraction(double fraction)
    {
        if (_scrollFraction == fraction) return;
        _scrollFraction = fraction;
        PositionInput();
        RequestRaster();
        UpdateCursorOverlay();
    }

}
