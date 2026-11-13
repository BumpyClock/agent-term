using System;
using System.Diagnostics;
using AgentTerm.Terminal;
using Microsoft.UI.Dispatching;

namespace AgentTerm_App;

public sealed partial class MainPage
{
    private readonly TerminalScrollAnimation _scrollAnimation = new();
    private DispatcherQueueTimer? _scrollTimer;
    private long _scrollTick;

    private void ScrollHistoryWheel(TerminalSession session, double rows)
    {
        var frame = _presentedFrame ?? session.ReadFrame();
        if (frame.Viewport.IsAlternateScreen) return;
        _scrollAnimation.Add(rows, frame.Viewport.Offset, frame.Viewport.MaximumOffset,
            _animationsEnabled && _viewActive && IsLoaded && !_surfaceOpen && !_selecting);
        if (!_scrollAnimation.IsActive)
        {
            session.ScrollTo(_scrollAnimation.IntegerOffset);
            RefreshOutput(session);
            return;
        }
        session.SetOverscanEnabled(true);
        _scrollTimer ??= CreateScrollTimer();
        _scrollTick = Stopwatch.GetTimestamp();
        _scrollTimer.Start();
    }

    private DispatcherQueueTimer CreateScrollTimer()
    {
        var timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.Tick += (_, _) =>
        {
            try
            {
                if (_session is not { } session || !_viewActive || !IsLoaded || _surfaceOpen)
                { StopSmoothScroll(false); return; }
                var now = Stopwatch.GetTimestamp();
                _scrollAnimation.Advance(Stopwatch.GetElapsedTime(_scrollTick, now).TotalSeconds);
                _scrollTick = now;
                session.ScrollTo(_scrollAnimation.IntegerOffset);
                RefreshOutput(session);
                // One below row covers every fractional position. Native offset advances each crossing.
                var fraction = _presentedFrame?.OverscanBelow.Length > 0 ? _scrollAnimation.Fraction : 0;
                Terminal.SetScrollFraction(fraction);
                RefreshLinkFeedback();
                if (_presentedFrame is { } frame) RefreshTaskCards(frame);
                if (!_scrollAnimation.IsActive)
                {
                    timer.Stop();
                    session.SetOverscanEnabled(false);
                    RefreshOutput(session);
                }
            }
            catch (Exception exception) { StopSmoothScroll(false); ShowInteractionFailure(exception); }
        };
        return timer;
    }

    private void StopSmoothScroll(bool refresh = true)
    {
        if (_scrollTimer?.IsRunning != true) return;
        _scrollTimer.Stop();
        var offset = (long)Math.Round(_scrollAnimation.Position);
        _scrollAnimation.Reset(offset);
        Terminal.SetScrollFraction(0);
        if (_session is not { } session) return;
        session.SetOverscanEnabled(false);
        session.ScrollTo(offset);
        if (refresh) RefreshOutput(session);
    }
}
