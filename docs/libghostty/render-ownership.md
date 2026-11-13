# Render ownership, render holds and the native pin.

Terminal users see complete frames from programs that use synchronized output (mode 2026), such as full-screen TUIs and agent CLIs. A frame never shows a half-drawn screen while a hold is active, and a program that never releases its hold shows live content after one second. The next maintainer inherits one owner for render state, one lock contract, and tests that pin each hold edge case.

## Native dependency.

`native-dependencies.json` pins libghostty-vt 1.0.1. The source archive SHA256 is `333dfce97641ef9b536bb9e08907ad86abaaada50e8f7417b70d9284adef37aa`. The archive minisign signature verifies against Ghostty's published key `RWQlAjJC23149WL2sEpT/l0QKy7hMIFhYdQOFy0Z7z7PbneUgvlsnYcV`. 1.0.1 keeps rows dirty after a failed render update, so a retried capture rebuilds them.

`scripts/build-native.ps1` and `scripts/check-linux.sh` share one cache strategy.

- Archives are named by version, so a pin change never reuses an older download.
- A cached archive with the wrong hash is deleted and downloaded again. A fresh download with the wrong hash fails the build.
- Each extracted tree carries `.agentterm-archive-sha256`. A tree without a matching stamp is deleted and extracted again through a staging directory.
- The signature is verified when `minisign` is installed (`winget install jedisct1.minisign`). Without it the build warns and relies on the pinned SHA256.
- The Windows build writes `artifacts/native/win-x64/native-build.json` with the source hash and DLL hash. `AGENTTERM_NATIVE_PREFIX` redirects the output for private builds.

The terminal smoke test fails when the loaded library version differs from the pin, or when a managed struct mirror differs from the library's `ghostty_type_json` manifest.

## Lock contract.

`TerminalSession` has two locks. The only nested order is `_renderGate`, then `_gate`.

| Lock | Protects |
| --- | --- |
| `_renderGate` | Render state, its iterators, the retained frame, capture metadata, disposal. |
| `_gate` | Live terminal, protocol state, live metadata, hold state written by the render-hold callback. |

- `BeginFrame` holds both locks. It adopts any handoff, decides hold behavior, settles presentation, records viewport, selection, reverse mode, theme and view revision, and calls `begin_update`. All metadata describes one moment.
- `CompleteFrame` holds only `_renderGate`. `end_update`, getters and frame materialization read render-owned memory, so output keeps parsing. `_captureInFlight` marks this window.
- A frame records the view revision captured at begin. A newer revision stays pending for the next capture.
- Native callbacks run inside `vt_write` under `_gate`. They never take `_renderGate`. They copy payloads, catch every exception, and never call UI or user code.
- Shutdown rejects new requests, waits for ConPTY without either lock, then takes `_renderGate` and `_gate` to free native state.

## Render holds.

libghostty-vt calls `GHOSTTY_TERMINAL_OPT_RENDER_HOLD` when a hold starts, before it parses the bytes that follow in the same write. The callback captures the boundary at that moment.

1. No capture in flight. The callback owns the render state, because every other user holds `_gate`. It settles presentation and updates the render state. The next `BeginFrame` materializes that snapshot without another update.
2. A capture in flight. The callback settles bottom anchoring through capture-local render, row, cell and image handles, then updates a separate handoff state. It never replaces active capture fields or borrows their iterators. A new handoff is a fresh state; a pending one updates incrementally. The next render-exclusive caller retires the current state and adopts the handoff. A fresh state rebuilds every row, so terminal dirtiness consumed by the handoff is never lost.

The hold deadline starts at the callback, not at the first frame read. While held, `ReadFrame` returns the boundary frame and `PendingFrameRefreshDelay` reports the remaining time. Repeated starts do not extend the hold. Release, resize and full reset end it through the callback. At the deadline, `BeginFrame` clears native mode 2026 with `GHOSTTY_TERMINAL_OPT_MODE`, which does not invoke the callback, and captures live content. A scroll, selection, resize or theme change during a hold shows the changed view, and the hold resumes on that frame.

Tradeoff, measured on a 200x50 terminal with the 1.0.1 DLL (median of 400 iterations, two runs after warm-up):

| Operation | Cost |
| --- | --- |
| Write that starts and ends a hold, no capture in flight | 0.7 to 1.7 µs, versus 0.4 µs without a hold |
| Write that starts a hold during an in-flight capture (handoff) | 35 µs |
| Full dense frame capture | 731 to 742 µs, versus 674 to 702 µs for the previous code on the same DLL |
| One-row frame capture | 48 µs, unchanged |

The full-frame difference is within run-to-run noise and has not been attributed. A serialized design (output takes `_renderGate`) would stall parsing for a whole conversion, about 0.7 ms per full frame, so it was not chosen.

## Capture failures.

A failed `begin_update`, render-state allocation or materialization never publishes partial state. The render state and its iterators are retired together, the previous complete frame stays published, and `PendingFrameRefreshDelay` schedules a retry with backoff from 16 ms to 1 s. With no previous frame the error surfaces. A failed boundary capture retains the last published frame until release or timeout, when one exists. The failure records its view revision so an explicit later view change can still override the hold. Hold fallback is checked before allocating another render state.

## Interop.

- Terminal calls take the `SafeHandle`, so marshalling retains the terminal for each call. The raw terminal pointer field is gone. New terminal P/Invokes must take `TerminalHandle`.
- Render-state loops use raw pointers under a `HandleLease`, which holds `DangerousAddRef` for the whole capture.
- In the cell capture path, `SuppressGCTransition` stays only on `ghostty_cell_get`, `row_iterator_next` and `row_cells_next`, which do fixed work. Getters that copy graphemes, scan dirty rows, clear rows or dispatch generically run with a normal transition.
- Keys and flags are named enums. State reads use one `get_multi` for always-present keys plus the aggregate cursor. Cells read raw value, style and UTF-8 text with one `get_multi`; a cluster over 64 bytes retries into an exact heap buffer. Selection uses one inclusive range per row.

## Capture lifecycle.

- `ConfigureRenderHold` runs in the constructor. Disposal frees the handoff in `DisposeRender`.
- `FinishWrite` runs viewport and selection upkeep and surfaces callback failures once. `TerminalProtocolStream` calls it after completing each incoming output buffer. The hold callback works with both native write entry points because it only needs `_gate`.
- `FrameCapture` gained init properties `Boundary` and `HoldGeneration`. Its positional constructor is unchanged.
- Test seams are internal: `BeginFrame`, `CompleteFrame`, `HoldCounters`, `SynchronizedOutputEnabled`, `RenderAllocator`, `CaptureFailures`, `RenderLayoutMismatch`.

## Tests.

`tests/AgentTerm.Terminal.Smoke/RenderOwnershipChecks.cs` covers the native pin and layouts, the exact boundary, first capture while held, release and restart in one write, repeated start, indefinite hold with native mode cleared, resize and reset, view changes during a hold, handoff during an in-flight capture, split-capture metadata, Unicode clusters over 64 bytes, retained frames, allocation-failure recovery through an injected allocator, handle lifetime after disposal and collection, and a two-thread stress that fails on any half-drawn frame. `tests/AgentTerm.ConPty.Smoke/ConcurrentFrameChecks.cs` races disposal against captures, view changes and resizes during synchronized output.

Application mouse events use the displayed frame's captured active-screen offset and viewport. Live output can scroll behind a render hold without moving a click to another row. Release events use the same mapping and retain existing outside-grid clamping.

## Review correction validation.

The 120x40 diagnostic callback probe in `RenderOwnershipChecks` writes a row before each hold, then checks that all 100 callbacks took the requested path. Five trials follow one warmup trial. A Debug run measured direct callback mean-per-trial min/median/max of 8.4/9.3/13.5 microseconds, and in-flight handoff 27.5/31.4/36.0 microseconds. This is a callback cost observation under a shared development workload, not a before/after speed claim. Native allocations and scanning are included; frame completion and publication are outside the timed region. A second Debug run measured 5.7/6.1/7.4 microseconds direct and 17.9/21.5/28.1 microseconds handoff. Variation under shared-machine load prevents a production performance conclusion. No profiler attribution is available.

Regressions compare a bottom-anchored in-flight boundary with an independently settled reference terminal, inject allocator failure into both direct and in-flight boundaries.

Cursor overlay invalidation includes the exact grid origin alongside its rounded resource bounds. A bottom-aligned resize or fractional scroll redraws the cursor even when both positions share one four-DIP bounds bucket. Overlay drawing uses the origin captured by that key. Renderer geometry/raster checks cover both triggers without changing machine preferences or page scheduling.
