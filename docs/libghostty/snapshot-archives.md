# Terminal archives

Use the new-session dropdown menu and choose **Save terminal archive…**. Choose a `.agtvt` file. The selected pane is captured after the file picker closes. Saving is user-triggered. AgentTerm does not save terminal history automatically.

Encoding, hashing, disk I/O, and decoding run off the window dispatcher. A save writes a temporary file beside the destination, flushes it to disk, and only then replaces the chosen file. A failed encode or replacement leaves the prior archive intact. Opening transfers the newly decoded terminal to the viewer's dispatcher before the viewer accesses it.

Choose **Open terminal archive…** in the same menu. A separate offline window renders the archive through TerminalSurface. It owns a GhosttyTerminal, with no TerminalSession, PTY, process, transport tasks, agent command ownership, or polling timer. Closing it releases the terminal and graphics resources. Closing the main window closes its archive windows.

Use the history scrollbar or mouse wheel. Drag to select and use **Copy selection** or Ctrl+C. Find text using the search field, Previous, Next, or Enter. Ctrl+F focuses search. Page Up and Page Down scroll history when search does not have focus. Large original grids also have horizontal and vertical screen-area scrollbars. The viewer retains the saved grid dimensions and hides its cursor. It does not feed keyboard input to the restored parser.

Offline search yields between native search steps. A new query cancels and awaits the old search before taking ownership. Closing cancels search before disposing terminal and graphics resources.

The binary payload preserves the native snapshot contract, including primary and alternate screens, active screen, history, modes, cursor, styles, palette, wide cells, graphemes, and tracked unfinished VT/UTF-8 input. Selection, viewport, and derived semantic-prompt state are omitted by native format version 1. Users can make new selections and search restored history. See the pinned `src/terminal/snapshot/screen.zig` description, lines 17–19. Archives restore terminal state, not a shell or agent execution. Kitty payloads are decoded by the native library; their host rendering follows the application's graphics support.

The envelope is separate from the C ABI and native format versions. Version 1 contains an eight-byte `AGTERMVT` magic, little-endian uint32 envelope version, uint32 payload length, int64 UTC Unix timestamp, SHA-256 payload digest, then native snapshot bytes. The header is 56 bytes. SHA-256 detects damage, not authenticity. Readers require an exact payload and EOF, reject unknown envelope versions and payloads above 64 MiB, then delegate native format validation to the native decoder. Native errors include their result code. Restoring constructs a fresh terminal only after full decode through FINISH. A failed restore never changes an existing terminal.

Continuation tracking starts before the first terminal write, with a 1 MiB limit. An unfinished sequence beyond that limit cannot be archived and reports an error. Decode applies the same limit, retains continuation for re-export, compresses restored history, and applies a 64 MiB Kitty image limit per screen. The buffer encoder first measures output and then writes into one bounded managed allocation. No borrowed native data escapes. The restored wrapper reconnects managed PTY reply handling and continuation tracking; it intentionally does not apply a new theme or overwrite the saved scrollback configuration. Additional feature callback hooks must also be attached in the common terminal constructor during integration.

Focused validation command.

```powershell
dotnet run --project tests/AgentTerm.Terminal.Smoke -- --snapshot-only
```

Checks cover active and inactive screen content, history search, styles, wide text, a 97-byte combining cluster, cursor state, native selection omission, split UTF-8/CSI/OSC continuation and re-export, callback reconnection, disposal, and rejection of corrupt, truncated, version-mismatched, oversized, and trailing-data input. Parent verification owns the final GUI workflow and aggregate Windows/Boat checks.
