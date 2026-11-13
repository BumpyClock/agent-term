# Review resolution.

Opus 5.5 and GPT-6 Astra medium reviewed the performance and startup changes. Opus and GPT-6.1 Sol high implemented separate fix scopes. These results apply to the final source, not the earlier continuously polling prototype.

## Confirmed findings.

- Idle refresh work. `MainPage` now coalesces output notifications into one composition refresh and drops its subscription after dispatch. Cursor blinking has its own timer. The fixture measures zero terminal reads during its silent interval.
- Silent synchronized output. The adapter exposes the remaining hold deadline. A nonrepeating timer requests a frame at expiry even if the process writes no further bytes. Both the fixture and application startup check require painted output without polling a snapshot in the wait predicate.
- Input without process echo. Key, text, IME and paste paths refresh local viewport and selection changes immediately.
- Hidden resources. Suspension releases the canvas, retained raster and glyph layouts. Activation creates one canvas for the loaded attachment. Shell and input state survive.
- Automatic focus. Startup keeps the input focusable while shell input remains gated on `Running`. Newly attached panes request focus from their own load event. The application test asserts focus without repairing it first.
- Scrollbar synchronization. Explicit view refreshes resynchronize its value even when the native viewport is unchanged.
- CJK layout churn. Cache capacity follows grid size with a hard cap. Functional CJK runs exceeded capacity while cache count stayed bounded. The dense workload remains outside the claimed frame budget.
- Benchmark provenance. The build script exports the baseline terminal and agents libraries and renderer from Git. Demand and matched-timer schedules are explicit. Historical intermediate measurements are labelled and separated from current evidence.
- Raster coverage. The normal Windows check script now runs retained-raster parity, including combining edits, selection during output, scrolling, cursor-only changes, and suspension.

## No-comments pass.

Comment-sicko agents removed narration from the changed product, test and script files. Public API contracts and verified external constraints remain. No application logic changed during those passes.

The pass also identified maintainability follow-ups. `TerminalSurface.UpdateRaster` has numeric damage margins. `ProfileAppearanceOverride.FontFamily` encodes inheritance and automatic selection with null and an empty string. Sidebar reveal and GitHub fitting depend on queued layout work. Existing tooltip and transition behavior also warrants a focused UI contract check before a future redesign. These are design flags, not demonstrated runtime defects from these reviews. Their behavior was preserved in this change.

## GitHub review follow-up.

Copilot and CodeRabbit reviewed `46eb3a1`. The follow-up fixes preserve two contracts. Only an uncovered terminal needs rendering resources and cursor animation. Every local selection mutation must reach the displayed frame without relying on child-process echo. Covered-tab activation now leaves the existing suspension intact, hidden cursors cannot start the blink timer, and accepted application mouse presses refresh the frame immediately. Both Left and Top startup runs passed these regressions.

Startup input stays focusable but read-only until the session is running. This prevents text from entering an invisible editing buffer before it can be sent. The startup check verifies both focus retention and the transition back to editable input.

The glyph cache now trims and disposes old layouts as soon as its grid capacity shrinks. The 35-transition raster suite passed. Rebuilding that suite against the previous renderer failed the new shrink case with 43,168 layouts above the new 1,024 bound.

The comparison script allows workload duration plus 60 seconds for startup, idle sampling and final checks. Workload completion JSON is published by a same-directory rename after the file closes. A controlled partial-write probe reproduced invalid JSON with the previous code and complete publication with the fix. Capture completion now rejects recorded errors and zero-frame samples. Compiled contract probes rejected both failures and preserved valid capture statistics.

The real 60-second comparison completed both 3,600-update trials with identical final pixels. A separate capture smoke returned 102 valid frames without an error. These are functional checks, not display-FPS evidence. The [post-review comparison](terminal-20261112-review.md) records timing on the updated renderer separately.

The source-hash mismatch claim was a newline mismatch. Raw measured Windows hashes are retained. LF-normalized adapter and session bytes equal the Git blobs at `46eb3a1` exactly. The evidence now records both conventions and the equivalent revision instead of relabelling measured hashes.

Copilot's next review found two fixture admission defects. A valid start gate can arrive immediately after readiness or the preceding completion marker. The workload now checks freshness before publishing readiness and does not reject that valid gate inside the trial loop. A deterministic protocol test covers early first and second gates and still rejects stale directories. The old code fails both early-gate cases. This test runs in pre-commit on Windows and Linux.

Minimum draw and changed-snapshot counts now stop at the expected workload count. A real one-update comparison at 1 Hz for one second passed both timer and demand schedules with identical final pixels. The old native fixture failed this case despite one draw, one changed snapshot and the expected output. This is a functional check, not timing qualification. These fixture changes leave the measured production source hashes unchanged.

The possible-secret alert targeted `currentSourceLfHashes.currentSession`. Recomputing SHA-256 from the `TerminalSession.cs` Git blob at `46eb3a1` reproduces that value exactly. It is a source digest, not a credential, and remains in the evidence.

## Remaining limits.

Displayed 60 FPS, GPU residency, monitor-to-monitor DPI changes, and forced device loss were not established by these runs. See [current measurements](terminal-20261112-current.md), [startup evidence](../terminal-startup.md), and [the measurement boundary](../terminal-frame-budget.md).

The complete native keyboard UI suite stopped when its window lost foreground ownership. It did not inject input into the other window. Focused timeline and pane-header checks passed. A CMD trailing-text artifact after GUI resize is documented in the startup evidence and remains unresolved.
