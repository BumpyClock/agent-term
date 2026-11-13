# libghostty integration

AgentTerm uses libghostty-vt 1.0.1 for parsing, render capture, input encoding, search, history formatting, snapshots and program protocols. WinUI owns presentation and Windows interactions. The integration preserves complete grapheme clusters and resolved styles through the public C API; it does not decode the private raw-cell bit layout.

## Features and contracts

| Area | Guide |
| --- | --- |
| Captured state, synchronized output, failure recovery and disposal | [Render ownership](render-ownership.md) |
| Ordered native prompt, title, directory and status callbacks | [Protocol events](protocol-events.md) |
| Underline styles, overline, blink and cursor shapes | [Text fidelity](text-fidelity.md) |
| Pixel geometry, identity, capabilities, theme reports and diagnostics | [Host integration](host.md) |
| Progressive Find, semantic prompt navigation, rich export and compression | [History](history.md) |
| Save and reopen an independent read-only terminal | [Archives](snapshot-archives.md) |
| Fractional history scrolling and shared hit-test geometry | [Overscan](overscan.md) |
| Inline graphics, animation, layers and memory limits | [Kitty images](kitty-images.md) |
| Clipboard policy, bell, notifications, progress and requested resizing | [Program effects](program-effects.md) |
| OSC 8 links, URI policy and application pointer shapes | [Hyperlinks](hyperlinks-and-pointer-shapes.md) |

## Integration verification

The integration passed the following checks on Windows with the pinned 1.0.1 DLL. Broad suites ran once after feature integration; focused checks ran again for subsequent fixes.

- Release x64 solution build, with zero warnings and errors.
- Terminal, ConPTY, Agents and Copilot normalization smoke suites.
- Renderer raster checks, including decorations beneath the block cursor, wide Unicode cells, overscan coordinates and Kitty image layers, alpha, crop, scaling and cache lifetime.
- Agent UI smoke, including expanded and compact sidebar hover, focus, timeline updates and narrow approval controls. The sidebar dismissal fixture moves outside both the tab and its card.
- Startup fixtures with left and top tabs, including synchronized-output timeout, idle wake, selection clearing, hidden cursor timers, covered-tab resource release, splitting, closing and resizing.
- The `--libghostty` app fixture, using actual WinUI surfaces. It checks inline image pixels, decoration capture, hyperlink color refresh, fractional scroll completion, progressive Find, progress/notification reset, and archive save/reopen/search/disposal.
- Focused ConPTY mouse regression. Output scrolls behind a held frame while clicks and releases retain the displayed row mapping.

Boat Linux CI rebuilt the pinned native library and passed `scripts/check-linux.sh`, including Terminal, Agents and Copilot normalization. The sandbox was deleted and its absence confirmed. The Linux Git fixture skipped because the image's Git version was below 2.45; Windows exercised that fixture. File URI policy treats Windows path separators consistently on both platforms.

The review correction run repeated the complete Terminal, ConPTY, Agent UI, renderer raster, live `--libghostty`, and Boat Linux suites. New regressions cover repository membership changes, retained icon contrast, pane reparenting, actual search-selection ownership, full URI confirmation, default-shell prompt navigation, anchored hold failures, cropped-image memory bounds and overscan layers, clipboard decision ownership, progress coalescing, and exact custom privacy limits. The integrated source was compared with the tested source after stack rebasing; all corrected file contents matched. No extra runtime pass is implied for changes that only relocate those commits between stack layers.

The startup fixture is built with `-p:StartupSmoke=true`. `scripts/check-startup.ps1` runs both tab layouts. To run the feature fixture against the same build, pass a report path, `Left`, and `--libghostty` to its `AgentTerm.App.exe`. It writes a text report, a PNG raster and a `.agtvt` archive beside the report. Run GUI fixtures sequentially.

## Verification limits

OS toast activation, launching a confirmed file link, file-picker interaction and the physical Windows clipboard were not exercised by the automated fixture. Clipboard policy and protocol behavior use an injected adapter to preserve the user's clipboard. Kitty `a=c` composition remains untested. These are coverage limits, not claims of passing end-to-end checks.

No displayed-FPS or keystroke-to-photon comparison was made. This change does not establish a need for C++, a dedicated render thread or NativeAOT. Search ticks serialize with terminal writes because the pinned implementation still reads terminal-owned history despite the header's independence claim. Smooth scrolling captures the bounded viewport plus overscan while moving; ordinary output uses dirty rows.
