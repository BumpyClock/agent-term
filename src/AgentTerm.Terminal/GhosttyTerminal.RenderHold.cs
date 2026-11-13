using System.Runtime.InteropServices;

namespace AgentTerm.Terminal;

// Synchronized output (mode 2026) as render holds. libghostty-vt reports each hold start before it processes the
// bytes that follow in the same write, so the frame the program wants on screen is captured inside the callback.
//
// Ownership. The callback runs inside vt_write, under terminal exclusion (TerminalSession._gate). It never takes
// the render gate. Every user of the render state holds terminal exclusion, except CompleteFrame after BeginFrame,
// which is marked by _captureInFlight. So the callback captures straight into the render state when no capture is
// in flight. Otherwise it captures into a fresh state (the handoff) that the next render-exclusive caller adopts,
// replacing the current state. A fresh state rebuilds every row, so terminal dirtiness consumed by the handoff
// capture is never lost to the adopted state.
public sealed partial class GhosttyTerminal
{
    /// <summary>How long a program may hold rendering before the host releases synchronized output itself.</summary>
    public static readonly TimeSpan RenderHoldTimeout = TimeSpan.FromSeconds(1);

    private HoldNative.RenderHoldCallback? _renderHold;
    // Terminal exclusion guards these; the native callback writes them.
    private bool _holdActive;
    private long _holdStartedAt;
    private long _holdGeneration;
    private FrameCapture? _boundary;
    private RenderHandle? _handoff;
    private bool _renderStale;
    private long? _failedBoundaryViewRevision;
    // Render exclusion guards this; CompleteFrame writes it.
    private long _frameHoldGeneration = -1;
    private RenderHoldCounters _holdCounters;

    /// <summary>Counts of hold events since construction. Read under terminal exclusion.</summary>
    internal RenderHoldCounters HoldCounters => _holdCounters;

    /// <summary>True while a program holds rendering and the host has not timed the hold out. Read under terminal exclusion.</summary>
    public bool IsRenderHeld { get { ThrowIfDisposed(); return _holdActive; } }

    /// <summary>The native mode 2026 value, which the host clears itself when a hold times out.</summary>
    internal bool SynchronizedOutputEnabled => ReadMode(SynchronizedOutputMode);

    internal struct RenderHoldCounters
    {
        public int Starts, DirectCaptures, HandoffCaptures, Adoptions, Timeouts, CaptureFailures;
    }

    private void ConfigureRenderHold()
    {
        _renderHold = (_, _, held) => OnRenderHold(held != 0);
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, (int)TerminalOption.RenderHold, Marshal.GetFunctionPointerForDelegate(_renderHold)),
            "Could not register render holds.");
    }

    private void OnRenderHold(bool held)
    {
        // Exceptions must not unwind through the parser. A failed boundary retains the last published frame.
        try
        {
            _boundary = null;
            _failedBoundaryViewRevision = null;
            _holdGeneration++;
            _holdActive = held;
            if (!held) return;
            _holdStartedAt = Environment.TickCount64;
            _holdCounters.Starts++;
            CaptureBoundary();
        }
        catch (Exception) { _boundary = null; _failedBoundaryViewRevision = _viewRevision; _holdCounters.CaptureFailures++; }
    }

    private unsafe void CaptureBoundary()
    {
        SettleAfterWrite();
        EnsurePresentation(boundary: true);
        if (Volatile.Read(ref _captureInFlight))
        {
            // A pending handoff belongs to this capture window: every render-exclusive caller adopts it before starting
            // another. Nothing else consumed terminal dirtiness since its last update, so it updates incrementally.
            var handoff = _handoff ?? NewRenderState();
            _handoff = null;
            using (var lease = new HandleLease(handoff))
            {
                ConfigureOverscan(lease.Raw, _overscanEnabled);
                var result = RenderNative.ghostty_render_state_update(lease.Raw, _handle);
                if (result != NativeResult.Success)
                {
                    // The failed update may have consumed terminal dirtiness the current state never saw.
                    handoff.Dispose();
                    _renderStale = true;
                    ThrowIfFailed(result, "Could not capture a render hold boundary.");
                }
            }
            _handoff = handoff;
            _boundary = CaptureMetadata() with { Boundary = true };
            _holdCounters.HandoffCaptures++;
            return;
        }
        EnsureRender();
        var capture = CaptureMetadata() with { Boundary = true };
        using (var lease = new HandleLease(_render))
        {
            ConfigureOverscan(lease.Raw, capture.OverscanEnabled);
            var result = RenderNative.ghostty_render_state_update(lease.Raw, _handle);
            if (result != NativeResult.Success) { RetireRender(); ThrowIfFailed(result, "Could not capture a render hold boundary."); }
        }
        _boundary = capture;
        _holdCounters.DirectCaptures++;
    }

    /// <summary>
    /// Decides whether BeginFrame publishes held content. Returns the boundary snapshot (already in the render state),
    /// the retained held frame, or null for a live capture. Runs under render and terminal exclusion, after adoption.
    /// </summary>
    private FrameCapture? HeldCapture()
    {
        if (!_holdActive) return null;
        var deadline = _holdStartedAt + (long)RenderHoldTimeout.TotalMilliseconds;
        if (Environment.TickCount64 >= deadline)
        {
            ReleaseExpiredHold();
            return null;
        }
        if (_boundary is { } boundary)
        {
            _boundary = null;
            // A scroll, selection, resize or theme change after the boundary outranks the hold.
            if (boundary.ViewRevision != _viewRevision) return null;
            Volatile.Write(ref _frameRefreshAt, deadline);
            return boundary;
        }
        if (_frame is null) return null;
        var publishedForHold = _frameHoldGeneration == _holdGeneration && _viewRevision == _capturedViewRevision;
        var failedBoundaryFallback = _failedBoundaryViewRevision == _viewRevision;
        if (!publishedForHold && !failedBoundaryFallback) return null;
        Volatile.Write(ref _frameRefreshAt, deadline);
        return new FrameCapture(_frame, default, false, false, 0, null);
    }

    /// <summary>Gives up on a hold that outlived the timeout. Setting the mode directly never invokes the callback.</summary>
    private unsafe void ReleaseExpiredHold()
    {
        var mode = new Mode { Value = SynchronizedOutputMode, Enabled = 0 };
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, (int)TerminalOption.Mode, (IntPtr)(&mode)), "Could not release synchronized output.");
        _holdActive = false;
        _boundary = null;
        _holdGeneration++;
        _holdCounters.Timeouts++;
    }

    /// <summary>Replaces the render state with a boundary handoff. Requires render and terminal exclusion.</summary>
    private void AdoptHandoff()
    {
        if (_handoff is null && !_renderStale) return;
        if (Volatile.Read(ref _captureInFlight)) throw new InvalidOperationException("A render state handoff cannot be adopted during a capture.");
        RetireRender();
        _renderStale = false;
        if (_handoff is not { } handoff) return;
        _handoff = null;
        _render = handoff;
        _renderState = handoff.DangerousGetHandle();
        _holdCounters.Adoptions++;
    }

    private static class HoldNative
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void RenderHoldCallback(IntPtr terminal, IntPtr userdata, byte held);
    }
}
