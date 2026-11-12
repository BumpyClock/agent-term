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

## Remaining limits.

Displayed 60 FPS, GPU residency, monitor-to-monitor DPI changes, and forced device loss were not established by these runs. See [current measurements](terminal-20261112-current.md), [startup evidence](../terminal-startup.md), and [the measurement boundary](../terminal-frame-budget.md).

The complete native keyboard UI suite stopped when its window lost foreground ownership. It did not inject input into the other window. Focused timeline and pane-header checks passed. A CMD trailing-text artifact after GUI resize is documented in the startup evidence and remains unresolved.
