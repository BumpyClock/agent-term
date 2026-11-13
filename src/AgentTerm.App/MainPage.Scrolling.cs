using System;
using System.Diagnostics;
using AgentTerm.Terminal;

namespace AgentTerm_App;

public sealed partial class MainPage
{
    private readonly TerminalScrollAnimation _scrollAnimation = new();
    private long _scrollTick;
    private TerminalSession? _wheelSession;
    private long? _pendingWheelTarget;

    private void ScrollHistoryWheel(TerminalSession session, double rows)
    {
        if (_presentedFrame is not { } frame) return;
        if (frame.Viewport.IsAlternateScreen) return;
        if (_wheelSession != session || frame.Viewport.Offset == _pendingWheelTarget) _pendingWheelTarget = null;
        _wheelSession = session;
        // Animation needs the frame driver. CanRefreshFrames is false for retained history (stopped or exited, Idle), so scroll there at once.
        _scrollAnimation.Add(rows, _pendingWheelTarget ?? frame.Viewport.Offset, frame.Viewport.MaximumOffset,
            _animationsEnabled && CanRefreshFrames && !_selecting);
        if (!_scrollAnimation.IsActive)
        {
            _scrollTick = 0;
            Terminal.SetScrollFraction(0);
            var offset = _scrollAnimation.IntegerOffset;
            _pendingWheelTarget = offset;
            QueueSessionUpdate(session, owner => { owner.SetOverscanEnabled(false); owner.ScrollTo(offset); });
            RefreshTerminalFrame(session, refreshCards: true);
            return;
        }
        QueueSessionUpdate(session, owner => owner.SetOverscanEnabled(true));
        _scrollTick = Stopwatch.GetTimestamp();
        RequestFrameRefresh();
    }

    private void AdvanceSmoothScroll(TerminalSession session)
    {
        if (_scrollTick == 0) return;
        var now = Stopwatch.GetTimestamp();
        _scrollAnimation.Advance(Stopwatch.GetElapsedTime(_scrollTick, now).TotalSeconds);
        _scrollTick = _scrollAnimation.IsActive ? now : 0;
        var offset = _scrollAnimation.IntegerOffset;
        var active = _scrollAnimation.IsActive;
        QueueSessionUpdate(session, owner => { owner.ScrollTo(offset); owner.SetOverscanEnabled(active); });
        Terminal.SetScrollFraction(_scrollAnimation.IsActive ? _scrollAnimation.Fraction : 0);
    }

    private void StopSmoothScroll(bool refresh = true)
    {
        _pendingWheelTarget = null;
        if (_scrollTick == 0) return;
        _scrollTick = 0;
        var offset = (long)Math.Round(_scrollAnimation.Position);
        _scrollAnimation.Reset(offset);
        Terminal.SetScrollFraction(0);
        if (_session is not { } session) return;
        QueueSessionUpdate(session, owner => { owner.SetOverscanEnabled(false); owner.ScrollTo(offset); });
        if (refresh) RefreshTerminalFrame(session, refreshCards: true);
    }

    private async void QueueSessionUpdate(TerminalSession session, Action<TerminalSession> update, bool refresh = false)
    {
        try
        {
            await session.DispatchAsync(update);
            if (refresh && _session == session && _state != SessionState.Disposed) RefreshTerminalFrame(session, refreshCards: true);
        }
        catch (ObjectDisposedException) when (_session != session || _state == SessionState.Disposed) { }
        catch (Exception error)
        {
            if (_session == session && _state != SessionState.Disposed) await FailSessionAsync(error);
        }
    }
}
