# Host geometry, identity, theme and diagnostics.

Programs receive actual device pixel cell dimensions through the native size callback. `MainPage` multiplies WinUI cell dimensions by `XamlRoot.RasterizationScale` and rounds to whole device pixels. Font metric and XamlRoot changes update native geometry even when the character grid stays unchanged. No polling loop is added.

`GhosttyTerminal.Resize` and `TerminalSession.Resize` accept optional pixel width and height after the existing grid arguments. Zero means unknown. Native CSI 14/16/18/19t replies use this geometry. The library emits mode 2048 reports only while enabled. Session resize and theme changes drain their replies immediately to ConPTY input, as output parsing already does.

Only ConPTY-backed `TerminalSession` calls `ConfigureConPtyHost`, which disables native resize scrollback pulling. Standalone terminals retain the library default. History remains available when a ConPTY grid grows, without drawing old history into the active grid before the host redraw.

XTGETTCAP TN reports `xterm-256color`, matching the existing shell environment. XTVERSION reports `AgentTerm` and the terminal assembly version. Environment TERM variables are unchanged. Primary DA advertises VT220 conformance and ANSI color. Secondary DA reports VT220 emulator identity with firmware and cartridge zero. No image or clipboard capability is inferred from the native build.

`TerminalHostCapabilities` is the integration seam for the image and clipboard owners. Both flags default false. Image storage is zero until `KittyImages` is explicitly enabled after host rendering is operational. Enabling uses `KittyImageStorageLimitBytes`, default 64 MiB. Kitty graphics has no DA1 feature code, so this flag enables native protocol support without advertising Sixel. `Clipboard` adds DA1 feature 52 only after clipboard support and its preferences are operational. Application sessions explicitly enable image storage. Clipboard advertisement also reads the configured effects adapter and current write policy. Effects preference changes do not change image storage.

Theme queries remain native callbacks. Actual dark/light changes queue CSI ? 997 reports only while mode 2031 is enabled. Changing colors within the same scheme is silent.

Ctrl+Shift+F12 opens a selectable, read-only diagnostic report. `MainPage.ShowHostDiagnosticsAsync` is also a shared command for menu integration. `TerminalSession.ReadDiagnosticReport` and `GhosttyTerminal.ReadDiagnosticReport` return owned strings. Reports include loaded native version, optimization, compiled features, host runtime, host capabilities, geometry, resident/reserved page memory, image memory, compression support, and the sticky VT-processing error flag. Compiled Kitty support is listed separately from host rendering support. Malformed VT remains best-effort input and does not become a fatal host error.

## Contract and validation.

Bindings follow the bundled `terminal.h`, `device.h`, `size_report.h`, `color_scheme.h`, and `build_info.h`. All five were byte-compared with the official 1.0.1 archive and matched. The official VT documentation at https://lib.ghostty.org/vt/ returned HTTP 200 during implementation.

Focused checks and aggregate Windows suites passed with the pinned 1.0.1 DLL.

- `dotnet run --project tests/AgentTerm.Terminal.Smoke -- --host-only` checks exact native replies, mode gating, unchanged-grid pixel changes, identity, capability opt-in, disabled image queries, theme queries/reports, diagnostics after malformed VT, standalone resize semantics and ConPTY history preservation.
- `dotnet run --project tests/AgentTerm.ConPty.Smoke -- --resize-input-only` checks immediate/delayed resize adjacency, blank output, long transport, Unicode/multiline capture and Legacy history.
- `dotnet build src/AgentTerm.App -p:Platform=x64 --nologo -v:q` passed with zero warnings and errors.
- `git diff --check` passed.

The shared constructor registers host, snapshot, render-hold, effects and reset callbacks. Session mutation paths drain replies before returning. The terminal assembly version currently resolves to 1.0.0; central versioning should update it when the app release version changes. See the [integration verification](README.md) for platform and UI coverage.
