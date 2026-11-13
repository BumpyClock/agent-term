using System;
using System.Diagnostics;
using AgentTerm.Terminal;

namespace AgentTerm_App;

public sealed partial class MainPage
{
    private readonly TerminalScrollAnimation _scrollAnimation = new();
    private long _scrollTick;

    private void ScrollHistoryWheel(TerminalSession session, double rows)
    {
        var frame = _presentedFrame ?? session.ReadFrame();
        if (frame.Viewport.IsAlternateScreen) return;
        // Animation needs the frame driver. CanRefreshFrames is false for retained history (stopped or exited, Idle), so scroll there at once.
        _scrollAnimation.Add(rows, frame.Viewport.Offset, frame.Viewport.MaximumOffset,
            _animationsEnabled && CanRefreshFrames && !_selecting);
        if (!_scrollAnimation.IsActive)
        {
            _scrollTick = 0;
            session.SetOverscanEnabled(false);
            Terminal.SetScrollFraction(0);
            session.ScrollTo(_scrollAnimation.IntegerOffset);
            RefreshTerminalFrame(session, refreshCards: true);
            return;
        }
        session.SetOverscanEnabled(true);
        _scrollTick = Stopwatch.GetTimestamp();
        RequestFrameRefresh();
    }

    private void AdvanceSmoothScroll(TerminalSession session)
    {
        if (_scrollTick == 0) return;
        var now = Stopwatch.GetTimestamp();
        _scrollAnimation.Advance(Stopwatch.GetElapsedTime(_scrollTick, now).TotalSeconds);
        _scrollTick = _scrollAnimation.IsActive ? now : 0;
        session.ScrollTo(_scrollAnimation.IntegerOffset);
        session.SetOverscanEnabled(_scrollAnimation.IsActive);
        Terminal.SetScrollFraction(_scrollAnimation.IsActive ? _scrollAnimation.Fraction : 0);
    }

    private void StopSmoothScroll(bool refresh = true)
    {
        if (_scrollTick == 0) return;
        _scrollTick = 0;
        var offset = (long)Math.Round(_scrollAnimation.Position);
        _scrollAnimation.Reset(offset);
        Terminal.SetScrollFraction(0);
        if (_session is not { } session) return;
        session.SetOverscanEnabled(false);
        session.ScrollTo(offset);
        if (refresh) RefreshTerminalFrame(session, refreshCards: true);
    }
}
