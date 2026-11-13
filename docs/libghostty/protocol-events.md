# Protocol events and ordered output.

Users see program titles, working directories, OSC 7501 status cards and command transcripts update from one parse of each output chunk. A title or status set right after a prompt in the same chunk survives, and one set before it clears. The next maintainer owns one adapter, `TerminalProtocolStream`, instead of a managed escape-state machine that had to agree with the native parser.

## What runs.

`TerminalSession` feeds every ConPTY chunk to `TerminalProtocolStream.Write`. The stream calls `ghostty_terminal_vt_write_until` with every APC, DCS and OSC start and end boundary. Plain output and CSI cost nothing extra, because the native bulk paths do not stop for them (documented in `stream.zig`). A chunk with no control strings is one native call. Each control string adds two calls.

Native callbacks, registered on first stream use by `GhosttyTerminal.EnableProtocolEvents`.

| Option | Event record | Session effect |
| --- | --- | --- |
| `TITLE_CHANGED` (6) | `TerminalTitleChanged` | Sets `Title`. Native truncates to 1024 bytes. |
| `PWD_CHANGED` (26) | `TerminalWorkingDirectoryChanged` | None. `ReadWorkingDirectory` reads the live value. |
| `SEMANTIC_PROMPT` (43) | `TerminalSemanticPrompt` | A primary or right prompt start ends working and blocked status records. |
| `RESET` (44) | `TerminalReset` | Clears status records and `Title`. |
| `PROGRAM_STATUS` (47) | `TerminalProgramStatusReport` | `ProgramStatusProtocol.Apply` keeps records. |

Callbacks copy borrowed strings into owned records and queue them. They never call UI or user code, never write to the terminal, and capture exceptions instead of letting them cross the ABI. The write path rethrows a captured exception after the chunk, as it already did for query replies. Delegates stay rooted in terminal fields until the terminal is freed.

Registering the program status callback makes the terminal answer `OSC 7501 ; ?` itself, through the normal reply queue. The reply mirrors the query terminator (BEL or ST). The session still drops replies after disposal starts.

## Ordering contract.

The stream processes each boundary segment in this order.

1. Resolve a pending ESC terminator. A backslash completes ST and dispatches a retained private marker. Any other byte makes the ESC an ordinary escape.
2. Report plain output in the segment, or collect OSC content.
3. Drain native events raised while the segment was parsed.
4. Dispatch a retained private marker that ended with BEL.
5. Announce a string that starts at the segment end (`ControlStringStarted`).

Consequences, all verified by the split-invariance checks at every single cut and bytewise:

- Printable bytes before a control string reach `Output` before that string's marker or native event. Bytes after it arrive afterward.
- OSC 133, OSC 7501, title and directory events sit at their exact OSC positions relative to private markers and each other.
- A full reset reports the cleared status record (`State = Clear`, empty id) and then `TerminalReset`, as the native contract documents. Titles set after the reset arrive after it.
- Reset (`ESC c`) is not a control string. It is exact relative to control strings and other events. Relative to plain bytes in the same uninterrupted run, it is delivered after that run. Capture ignores resets, so no consumer observes this. Stopping at every return to ground would cost 1 to 5 percent of parse time (native documentation) to order an event nobody depends on.

`TerminalSession.ProtocolEvent` raises the same records on the reader thread after `_gate` is released and before `OutputAvailable`. Session state already reflects the whole chunk. A reset clears title and directory without separate change events, so handlers that show them update on `TerminalReset`.

## Private markers.

Shell integration markers stay authenticated by the per-session token. The stream retains only OSC content that starts with `777;<token>;`, compared incrementally, and stops buffering at the first mismatching byte. Content longer than 1,000,000 bytes never dispatches. The reported byte length keeps the old formula: content plus introducer plus terminator. Strings ended by CAN, SUB, or ESC without a backslash never dispatch, matching the previous parser. libghostty-vt rejects `OSC 777` that is not `notify`, so these markers have no native effect.

## Transcript capture.

`Output` carries bytes outside every control string, without introducers or terminators. A trailing ESC run is held until the next byte proves whether it introduces a string, then released verbatim. `CommandOutputCapture` keeps its fences, ANSI filtering, streaming UTF-8 decoding, replay reset and 64,000-character cap. Two filter changes follow the VT parser. An ESC inside an escape restarts it. A control string ends any CSI left open (`EndEscape`), so the first byte after the string is no longer swallowed.

## C1 bytes.

At ground, bytes 0x80 to 0x9F are UTF-8 data and stay in output, as before. Inside an escape sequence, a C1 introducer starts a string natively, and the stream filters it. An APC or a DCS still in its header ends on 0x9C or aborts on another C1 introducer, which can start a new string in the same byte. OSC and DCS payload bytes above 0x7F are content.

## Behavior differences from the managed parser.

Equivalence checks compare against a frozen copy of the old scanner and capture for 7-bit input where both define behavior. Native results are authoritative elsewhere.

- C1 introducers inside escape sequences now start strings. `ESC DEL ]` and `ESC <C0> ]` start an OSC, as in xterm-compatible parsers.
- OSC 7501 follows the specification's last-value rule. A replaced `msg` or `title` is length-checked but not decoded, so `msg=AA==:msg=SGk=` reports `Hi`.
- OSC 133 prompt starts include `133;N` and `133;P`. Continuation and secondary prompts no longer end status records.
- An OSC 0 or 2 title reaches `Title` when the event fires, not after the chunk.

## Integration hooks.

- `GhosttyTerminal.WriteUntilStringBoundary` and `FinishWrite` are internal. `Write` keeps its contract and shares `FinishWrite`. A render-hold callback runs inside the same writes, under `_gate`, and must not take `_renderGate`.
- `GhosttyTerminal.EnableProtocolEvents` is idempotent. Other effect callbacks register with their own options and do not conflict.
- `TerminalSession.ConfigureProtocol` and `ApplyProtocolEvent` live in `TerminalSession.Protocol.cs`.

## Verification.

- `tests/AgentTerm.Terminal.Smoke/ProtocolStreamChecks.cs` covers authentication, OSC 133 and 7501, title and reset order, cancellation, improper ST, C1, APC/DCS/SOS/PM filtering, same-chunk start and end, Unicode, truncation, retention limit, support replies and plain pass-through. Each case runs whole, at every single cut, and bytewise.
- `tests/AgentTerm.Terminal.Smoke/ProtocolStreamEquivalenceChecks.cs` compares 300 random 7-bit compositions at every split against the frozen parser, and 200 random 8-bit noise buffers for chunking invariance and terminal state identical to a plain write.
- `tests/AgentTerm.ConPty.Smoke/ProtocolEventSessionChecks.cs` runs PowerShell through ConPTY and checks event order, same-chunk status clearing and that handlers run without the session lock.
