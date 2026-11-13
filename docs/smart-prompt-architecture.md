# Smart prompt and agent harness architecture

Status. Initial Copilot implementation on feat/smart-prompt. Live and automated verification on 2026-11-11 is summarized in interaction-testing.md. Remaining acceptance cases are explicitly noted there.

The app-owned command composer described in older sections below was removed by the integrated Smart terminal correction. The shell's own prompt is now the only command input. Read [Integrated Smart terminal](#integrated-smart-terminal) first. Older sections record history and are marked where superseded.

## Outcome

Give each pane one input, the shell's own prompt, that can run a shell command or start an agent turn with `/agent`. Keep ordinary terminal interaction intact. Start with Copilot. Add Codex and Pi without another workflow controller, transcript model, or approval UI.

The architectural boundary is the **agent harness**, not the model API. Copilot, Codex, and Pi already own agent loops and tools. Agent Term hosts those loops. It must not implement a separate agent loop for each runtime.

Distinguish harness, configured harness instance, model selection, conversation, and turn. A different model within Copilot is not a different harness. A Windows session and a WSL session are different execution environments.

## Evidence

Both repositories were cloned with history into `../references`.

- Warp HEAD is `2b2ed236f087b47b463f1cd55598485dd7965338`.
- T3 Code HEAD is `429c0ac2d369fe27e21d697813950756ba260371`.
- Warp's `app/src/input_classifier.rs` selects a local ONNX BertTiny classifier when enabled, with a heuristic fallback. `crates/natural_language_detection/src/lib.rs` supplies vocabulary and shell-syntax scoring for that fallback. The vocabulary helper is not the entire routing pipeline. Detection need not call a remote LLM.
- Warp's `app/src/ai/blocklist/input_mode_policy.rs` separates input-mode policy from input handling. Its GUI and TUI can have different policies.
- T3 Code's `packages/provider-core/src/server/ProviderAdapter.ts` defines normalized events, capabilities, sessions, turns, interruptions, and runtime requests. `apps/server/src/orchestration-v2/ProviderAdapterRegistry.ts` resolves configured instances.
- Agent Term's `ShellIntegration.cs` reports OSC 7 working directories. It does not report command start, command end, or prompt readiness.
- `MainPage.xaml.cs` immediately forwards ordinary text. `TerminalSurface.cs` owns a small IME input bridge, not a retained command editor.
- `TerminalSession.cs` owns the live shell and serializes terminal state. `TerminalFrame.IsAlternateScreen` identifies alternate-screen programs, not all interactive programs.

Warp's README identifies its main application code as AGPL v3 and its UI framework as MIT. T3 Code's root license is MIT. Use Warp as behavioral and architectural reference. This proposal copies no implementation code.

## One boundary, shared behavior

Dependency direction.

```text
WinUI pane composer and transcript
                |
       PaneInteractionController
          /                 \
 Shell command path       AgentSessionController
          |                 |
 TerminalSession       IAgentHarness contract
          |              /      |       \
 ConPTY + Ghostty    Copilot   Codex     Pi
```

These are responsibility boundaries, not a requirement for six new projects. Start with a WinUI-free `AgentTerm.Agents` project and one Copilot adapter project. Keep provider SDK dependencies out of the shared project and app presentation.

The shared controller owns turn admission, draft submission, transcript projection, pending requests, UI state, and lifecycle. The adapter owns protocol parsing, runtime startup, native identifiers, authentication integration, and translation to the shared contract. The harness itself owns planning, tool selection, and execution of its agent loop.

The implemented controller retains one turn owner through startup and interruption. Stop during availability checks or session creation cancels that startup; a late session is disposed without sending the prompt. Session reuse requires both the same location and the same host-terminal identity. Repeated Stop calls share one operation. In the Copilot adapter, a native error retains turn ownership until native idle arrives. A failed or timed-out drain closes the event stream so the controller discards the unusable session before admitting a retry. Native drain is bounded to 30 seconds. Deterministic lifecycle tests exercise these transitions; the shared-controller fixture pumps one UI synchronization context like the app.

The app composition root registers adapters. No `if Copilot`, `if Codex`, or `if Pi` in the composer, reducer, approval UI, or context builder. Capability checks are allowed. Provider wire payloads never reach these consumers.

### Proposed contract

Use a factory plus a session handle within one architectural boundary. Proposed names below are not claims about existing APIs.

- `IAgentHarness.DescribeAsync` returns availability, authentication status, model choices, and supported capabilities.
- `IAgentHarness.OpenAsync` accepts execution location, model selection, permission policy, and an optional same-harness resume reference. It returns `IAgentSession`.
- `IAgentSession.SubmitAsync` accepts a typed prompt and explicit context attachments. Submission acceptance is not turn completion.
- `IAgentSession.Events` streams normalized events through `IAsyncEnumerable<AgentEvent>`.
- `IAgentSession.RespondAsync` answers a typed approval or user-input request.
- `IAgentSession.InterruptAsync` interrupts a specific active turn.
- `IAgentSession.DisposeAsync` releases that session without killing the user's shell or deleting saved history.

Model events as closed record variants. Start with text delta, tool started, tool output, tool completed, approval requested, user input requested, turn completed, turn failed, and turn interrupted. Associate events with session, turn, item, and request identities where applicable. The adapter translates native completion semantics. A text stream ending is not automatically success.

Use separate shell and agent state machines. Shell states are Unknown, Ready, Executing, and Exited. Agent states are Starting, Idle, Running, WaitingForApproval, WaitingForInput, Interrupting, Failed, and Disposed. Derive button availability from these states. Do not create independent booleans that must agree.

Capabilities describe differences such as resume, steering, attachments, and permission enforcement. An unsupported policy produces a clear unsupported result. Never silently weaken it or pretend every harness offers identical controls. Keep provider-specific diagnostics inside adapters.

Resume references belong to a harness instance and environment. Changing harness starts a new native conversation. A later explicit handoff can attach a summary. It must not claim to preserve hidden reasoning, native tool state, or complete context.

## Composer behavior

Superseded twice. The integrated Smart terminal section below replaces this behavior. Historical text follows. The pane has one prompt line. Text runs in the shell unchanged. A leading `/agent` token is the only route to the agent (`PromptRouter.Parse` returns `ShellIntent` or `AgentIntent`). The earlier Auto, Command and Agent modes and the prose grammar were removed with their callers.

At a verified prompt, the prompt line owns the draft. Once a command starts, terminal focus and raw input return to the shell. This includes password prompts and REPLs that do not use the alternate screen. Escape or the visible **Native prompt** action provides a raw-mode escape hatch. Keep IME, paste, selection, Ctrl+C, and terminal shortcuts functional.

### Agent surface and task card contract

- **Model.** `AgentTask` is one `/agent` invocation and its follow-ups, a view over timeline turn ids. `AgentSessionController` keeps at most 32 tasks and routes follow-ups to `CurrentTask`. All tasks in a pane share one harness session.
- **Surface.** The surface replaces the terminal view inside the same grid cell. The terminal is collapsed, not resized; the grid sizing ignores zero sizes, so ConPTY sees no resize and an owned command's capture sees no replay. Leaving the surface never interrupts the agent. Stop is a separate, explicit action.
- **Anchor.** `TerminalSession.ReserveRows` runs only at a ready integrated prompt. It anchors the cursor row with a Ghostty tracked grid reference, then submits a reservation id that PSReadLine accepts as an empty line. The prompt function prints one more blank line. No command runs and nothing is echoed. The anchor reads back in screen coordinates, the same space as the viewport offset.
- **Expiry.** An anchor expires when history trimming removes its row, when its terminal is disposed, or when it reads below the shell cursor at the live viewport, which means an in-place clear erased it. At most 64 anchors exist per terminal.
- **Cards.** Each frame positions a card from its anchor through the surface's grid mapping. Cards hide in the alternate screen, while the surface is open, over the live prompt row, and whenever a reserved row is not blank, so a card never covers shell text. A card forwards wheel input to the terminal. An expired anchor removes the card but not the task.
- **Presentation.** Conversation rows are retained by entry id, so expansion and focus survive streaming events. One tool call renders as a card. Several collapse behind a summary that keeps a failure mark. Failures and cancellations also add an outcome row outside any group. A pending request appears once, above the agent reply field. An approval outcome shows on its tool call, and a question answer is recorded when sent. Disclosure rows expose the UI Automation ExpandCollapse pattern. Styles use theme brushes only.

## Execution and context

User commands run in the existing shell so `cd`, variables, aliases, and activated environments survive. A new `Process.Start` per command is not equivalent.

User clarification on 2026-11-11 requires agent commands to run in the invoking pane's existing terminal. Implement a shared terminal execution capability above harness adapters. The pane grants exclusive command ownership only when the integrated shell is ready. The tool submits through the existing semantic command path and returns bounded output, completion status and resulting cwd. The UI shows agent ownership and prevents competing composer submissions. Interrupting the agent interrupts its owned command, not the shell process. A busy or unsupported shell produces an explicit tool result, never a silent new-process fallback.

Adapters register this host capability with their native tool mechanism. They do not implement shell orchestration themselves. For Copilot, exclude its independent shell execution tools and supply the pane terminal tool with clear instructions. Verify the tool selection and session-state continuity with the actual SDK/runtime. Other file tools may remain native to the harness, but their environment must not be represented as sharing shell-local variables. Update native file-tool cwd handling or clearly constrain it when the pane changes directory.

Freeze the execution location at turn submission. Reuse `ShellDirectory`, including the WSL distribution identity. A local Windows harness cannot silently execute against a Linux path. Initially reject unsupported execution environments with an actionable explanation. Do not fall back to the application's launch directory.

Attach only selected output or bounded command blocks plus useful location metadata. Label terminal output as context, not instructions. Do not send all scrollback or shell environment variables by default. Existing frame snapshots are not a complete transcript.

The native shell and agent conversation remain independent. Changing shell directory does not silently retarget a running agent. Show the conversation's bound location and start a new binding when the user chooses another location.

## Runtime integrations

**Copilot first.** Use the .NET `GitHub.Copilot.SDK`, not terminal-output scraping. Its documentation exposes session creation, working directory, streamed events, interruption, permissions, and user-input callbacks. Pin the SDK and compatible runtime. Use its authentication status rather than assuming the dashboard's `gh` authentication is sufficient. Connect callbacks to the shared request model. Do not adopt an approve-all sample as product policy.

**Codex later.** Implement the same boundary using `codex app-server` and its structured protocol. The installed CLI advertises stdio transport and protocol schema generation. Keep its thread, turn, and approval messages private to the adapter.

**Pi later.** Use `pi --mode rpc` for a language-independent subprocess integration. Map JSONL responses and events into the same contract. Confirm permission and cancellation semantics against the pinned version before declaring those capabilities supported.

Sources read for this design.

- [Copilot .NET SDK](https://github.com/github/copilot-sdk/blob/main/dotnet/README.md).
- [Copilot SDK and CLI compatibility](https://docs.github.com/en/copilot/how-tos/copilot-sdk/troubleshooting/compatibility).
- [Codex App Server](https://learn.chatgpt.com/docs/app-server).
- [Pi RPC documentation](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/docs/rpc.md).

## Delivery and acceptance

1. Build the shared contract and deterministic fake harness. Prove one transcript/controller handles streaming, approvals, questions, failure, interruption, and disposal. This establishes the contract before adapter implementation is delegated.
2. Build PowerShell readiness and command submission with the explicit `/agent` composer. Verify persistent cwd and variables, multiline commands, interactive input, exit status, IME, and raw fallback. Do not call prompt replacement complete while both editors are active.
3. Add Copilot through the same controller. Verify a real streamed turn, approval rejection and acceptance, user input, cancellation, and missing-auth behavior. Cancelling an agent must leave the shell usable.
4. Verify explicit `/agent` routing. Ordinary commands and aliases run unchanged without a network classifier. Bare `/agent` opens a task; `/agent <request>` submits it. `/agentx`, `/Agent` and leading-space text remain shell commands. Returning to the terminal and reopening a task must preserve shell and conversation state.
5. Add Codex and then Pi through the same contract suite. UI and shared controller should not require harness-name branches. Test actual protocol behavior in addition to the fake harness.

Run relevant repository checks on Boat for implementation changes. Windows shell and WinUI acceptance require a Windows environment. Use the requested 6.1-Sol medium/high or 6.1-Terra high for implementation delegates, and 6.1-Terra high for computer-use verification. Check Warp visually during that implementation pass before claiming visual parity.

This pass verified reference checkout completion and source contracts. It did not run Warp, implement the composer, run live agent turns, or establish runtime/visual parity. The highest-priority technical spike is PowerShell prompt ownership and readiness, followed by one real Copilot turn through the shared boundary.

## Initial implementation status

The shared boundary is `src/AgentTerm.Agents/Harness.cs`. `AgentSessionController` owns pane-local admission, bounded transcript, queued human requests, interruption and disposal. `PromptRouter` has no SDK or network dependency. Copilot SDK 1.0.31 is pinned in the adapter project. Its published XML documentation, packaged README and compiled API were inspected. Runtime 1.0.109 is acquired by its checksum-validating build targets. The installed VS Code copilot.exe was inspected as a launcher reporting 1.0.106-8 and is not selected.

PowerShell startup keeps profiles and wraps the existing prompt. A per-session OSC 777 token reports native/raw readiness, managed readiness, start and end status, plus nonexecuting command metadata. The OSC parser accepts fragmented bytes. A pane explicitly enables prompt suppression only after handshake. PSReadLine Ctrl+F12 decodes the composer transport into the native edit buffer and accepts the actual multiline command. This keeps native scope, history, echo and error status. It is session-local, not a profile edit. Other shells stay raw. The original prompt is called again when returning to raw mode.

The UI is `MainPage.Composer.cs` and the pane XAML. The composition root alone names Copilot. All lifecycle and request rendering consume the shared types. Tool results remain in the transcript. No terminal scrollback or environment variables are attached automatically. No Codex or Pi adapter is included. Resume, attachments and cross-harness handoff remain future work rather than advertised capabilities.

Deterministic tests use a test-only fake harness. Real ConPTY checks cover PowerShell state, aliases/functions, multiline execution, failure, interactive input, interruption and raw fallback. The optional Copilot smoke checks existing sign-in and streamed completion in an isolated directory. UI verification and screenshots remain required before release.

### Recorded verification on 2026-11-11

- Windows `scripts/check.ps1` passed. Release WinUI build had zero warnings and errors. Icon decoding, terminal behavior, real ConPTY sessions, renderer pixels and shared agent behavior passed.
- Targeted `dotnet run --project tests/AgentTerm.Agents.Smoke -c Release` and `dotnet run --project tests/AgentTerm.ConPty.Smoke -c Release` passed after the final routing/protocol changes. The shell suite includes persistent cwd and Ctrl+C.
- `dotnet run --project tests/AgentTerm.Copilot.Smoke -c Release` passed twice using existing Copilot sign-in. The harmless isolated turn streamed the expected marker. No auth changes or tokens were logged. Live UI approval, denial, interruption and recovery passed. Question response remains fake-harness tested only.
- Local pre-commit passed with Git Bash on the process PATH, including untracked implementation files via `--files`. Initial invocation found the WSL bash shim rather than Git Bash and failed Bash syntax startup; the corrected invocation passed.
- Boat sandbox portable `bash scripts/check-linux.sh` passed, including native Ghostty build, terminal smoke and shared smart-prompt tests. Its real Git fixtures were explicitly skipped because the sandbox Git is older than 2.45. Windows real Git fixtures passed. Boat pre-commit passed over all uploaded source. An initial upload had CRLF shell-script line endings; the repository script was normalized to LF. A first native build hit Boat exec's default 30-second timeout; rerunning with a 600-second command timeout passed.
- No commit, push or PR was created during verification.


## Pane host tool implementation

`IHostTerminal`, `TerminalExecutionRequest` and `TerminalExecutionResult` live in the WinUI-free Agents project. `TerminalSession` implements the capability once, for every harness. The controller obtains the pane capability through composition. Copilot only translates it to `CopilotTool.DefineTool` and the existing shared approval events. The custom handler disables the SDK's extra custom-tool prompt because the handler itself awaits exact-command approval; it does not skip human approval.

SDK 1.0.31 packaged README Tools section and XML `SessionConfigBase.AvailableTools` were inspected. The supported allow-list property restricts the session to `run_in_terminal` and the SDK-documented `ask_user` human-input tool; no guessed built-in exclusion names are used. Tradeoff. Native file tools, independent execution and subagent dispatch are unavailable. All file operations use the same pane tool, so no native cwd synchronization API is needed. The existing controller rebinds the native conversation on the next submit after a changed shell directory, not during an active turn.

Each command receives a correlation ID through PSReadLine's transport. An owned command excludes competing submissions and terminal input under the terminal state lock. An explicit interactive-input handoff permits raw input only for that lease. Cancellation sends Ctrl+C and waits for both correlated output completion and prompt readiness. A 20-second timeout returns InterruptionFailed without releasing the lease. Exit releases ownership. Pane disposal closes both resources.

Measured ConPTY behavior. The inbox console reordered custom OSC end markers before final output text. See https://github.com/microsoft/terminal/issues/17313 for the same reported ordering problem. Capture therefore uses visible ordered text fences, `Command started [a:id]` and `Command ended [a:id]`, correlated with OSC readiness. The fences name shell command boundaries only. They never claim an agent-turn outcome, which the task card and surface own. It consumes the incremental output stream, not a viewport or scrollback scrape. Fences, command echo and OSC metadata are excluded. UTF-8 decoding spans byte chunks. Capture is bounded to 64,000 characters and keeps draining after truncation. ANSI is stripped; terminal cursor redraws, background-job output and full-screen programs are not reconstructed as raw process output. Readiness still depends on authenticated per-session OSC, not text matching. Shell success is authoritative; ExitCode is PowerShell's last native LASTEXITCODE and can remain from an earlier native command when a subsequent command uses only cmdlets.

Managed submission validates the full prefixed command before accepting it. Syntax errors return a normal shell error and a completed capture. PowerShell constructs that require the first statement, such as top-level `using namespace` and `param`, are currently rejected in managed submission instead of hanging capture; run these through raw terminal input when needed. This does not create a child scope for ordinary commands.

### Structured activity refinement

`AgentTimeline` is the shared bounded presentation model. User and assistant messages, work groups, tools, failures and request notices are separate typed entries. Tool start, output and completion events carry native call identity. Overlapping calls complete independently. Approvals do not split consecutive work groups. Assistant messages do. The controller settles remaining active calls on terminal turn events and interruption. Late events from another turn are ignored.

`CopilotToolPresentation` owns the SDK boundary. It selects the command argument and decodes the custom terminal result into output, directory, exit code and outcome. The shared controller never parses SDK JSON or switches on Copilot names. SDK 1.0.31 XML confirms ToolCallId on start/complete events. AssistantReasoning Content is not treated as a safe public summary. The adapter does not forward it. A future adapter can explicitly emit the optional shared ReasoningSummary event.

`AgentTimelineView` retains native row controls by entry identity. The model retains user group/output expansion state. Only changed text is replaced. Quiet disclosure buttons with an ExpandCollapse automation peer replace the earlier ToggleButton and Expander, which drew a selection fill and nested chrome. ActivityLabel owns text-only gradient shimmer, system-motion/high-contrast subscriptions and unload cleanup. No idle animation timer runs. Pane activity gates all motion. The sticky composer and terminal ownership boundary are unchanged.

## Integrated Smart terminal

Correction on 2026-11-11. The user rejected the detached composer. The prompt must be the real shell prompt inside terminal content, bottom-aligned at first, with history above it in order, real Tab completion, history and editing, and `/agent` on the same line. Top-aligned presentation stays a conventional terminal. The later settings refinement replaces the Smart/Legacy pane toggle with one saved terminal start position. The decision and its rejected alternatives are in [the design note](notes/integrated-smart-terminal-design.md).

**What the user gets.** Typing goes to the shell at either start position. Tab completion, Up, Ctrl+R, predictions, custom PSReadLine bindings, multiline input and IME are the shell's own. Settings > Startup selects Bottom (the default) or Top for every pane. `/agent <request>` is typed at the shell prompt. The only app-owned text field is the reply field inside the agent surface.

**What the next maintainer inherits.** One saved presentation preference and one cell mapping, instead of a managed prompt mode, a mode handshake, an acknowledgement timeout, a command catalogue and a two-input focus arbiter.

### Contracts

| Contract | Owner | Rule |
| --- | --- | --- |
| Terminal start position | `TerminalPreferences`, `SettingsWindow`, `MainPage` | `TerminalStartPosition` is a saved global Bottom/Top enum. Missing, null or unknown stored values use Bottom. Save notifies every open pane. `ApplyDisplayMode` calls `TerminalSession.SetBottomAnchored` and `TerminalSurface.SetBottomAligned` together, without PTY input, resize, surface changes or focus moves. New and restarted sessions read the preference before `Start`. The header toggle, its event and F6 handler are removed. |
| Live edge | `TerminalSession`, `GhosttyTerminal` | Bottom-anchored presentation trims the active screen's blank tail. `TerminalViewport.TopInset` is the number of blank rows the view leaves above frame row 0 when the document is shorter than the view. It is zero with anchoring off and on the alternate screen. |
| Cell mapping | `TerminalGridGeometry` in `TerminalSurface.cs` | `Top = remainder + TopInset * CellHeight`. The remainder is `ActualHeight - Rows * CellHeight` when bottom-aligned and zero otherwise. Frame row r is drawn at `Top + r * CellHeight`. Only `Rows - TopInset` frame rows are drawn. Glyphs, backgrounds, selection fill, cursor, IME text box, hit testing, selection drag, selection edge scroll, application mouse and task cards all use this one mapping. `Offset` stays in logical screen rows and is never adjusted by the inset. |
| Hit testing | `TerminalGridGeometry.TryCellAt` | The inset and the remainder are not cells. A press there starts no selection. Drags clamp to the nearest drawn cell. |
| Application mouse | `MainPage.SendApplicationMouse` | Coordinates are relative to frame row 0. The session drops a button press outside the drawn rows. Wheel, motion and release clamp into the drawn rows, so a held button is always released. |
| Agent entry | `TerminalSession.TakeAgentRequest` at `Ready` | The shell's Enter handler routes a leading `/agent` line. The app takes it only at `Ready`, reserves two rows, opens the surface and submits. If a turn is running, the running task opens and the request waits in its reply field. |
| Entry availability | `TerminalSession.AgentEntryAvailable` | Messaging only. The pane header shows **/agent unavailable**, with the reason in its tooltip, for shells without integration and for PowerShell sessions where AgentTerm's Enter handler is not active. It never gates `TakeAgentRequest`. |
| Focus | `PromptFocusPolicy` | The open agent surface owns input. Otherwise the terminal does, at either start position. Input moves only when the surface opens or closes. Explicit opens and leaves hand focus over. A shell-driven `/agent` open hands focus over only while this pane's input still holds it. Passive updates never move focus. |
| Task cards | `MainPage.RefreshTaskCards` | A card is visible only while its reserved rows inside the frame are blank: no text, default background, no underline or strikethrough. A non-blank row hides the card instead of covering it. The task stays reachable by Ctrl+Shift+Y and the task status button. |
| Agent ownership | `TerminalSession` | While an agent command owns the shell, terminal keystrokes are dropped unless the user chooses **Type into command**. That remains its own header button, independent of start position. |

`AgentStatusBar` owns shell integration availability, background task status and input handoff controls. Narrow panes keep shell availability as an information icon with the same tooltip and accessible explanation. It has no display-mode state or actions.

Removed from the app. The page-level composer row, the shell submit branch, terminal drafts, shell placeholders and hints, the command catalogue, `ReconcilePromptMode` calls, cursor suppression, `Raw` and `Switching` handling, the managed-prompt focus arbitration and the `GettingFocus` redirect. The app never calls `SubmitCommand`.

### Resize and the native editor

**Who notices.** A user whose prompt is wider than the pane, for example a deep working directory, resizes the pane and keeps typing. Before this change, what they typed after a resize could be drawn inside the prompt text, and the command still ran. Now the input continues right after the prompt.

**Cause.** Ghostty reflows soft-wrapped rows on resize, as any reflowing terminal does. PSReadLine 2.x then moves its cached input origin to its old column modulo the new width (`RecomputeInitialCoords` in PSReadLine `Render.cs`). That is right only when the prompt did not wrap at the old width. ConPTY reports the reflowed cursor honestly. The stale value is PSReadLine's cached origin.

**Correction.** After a ConPTY resize that changes the width at a ready integrated prompt, `TerminalSession` sends Ctrl+Shift+F12. The integration handler derives the origin from the reflowed cursor and the text before it. It checks the result with PSReadLine's own offset-to-point geometry, then updates the same cached fields `InvokePrompt` sets. It does not rerun, rewrite or re-encode the prompt, and it writes no screen output. It reports `anchor;<width>;<result>`. If ConPTY had not applied the resize yet, the reported width is stale and the session sends one more chord, up to four. No timer is involved. The handler abstains, leaving PSReadLine's behavior unchanged, when the cursor is on a later line of a multi-line draft, when it is parked past an exactly full row, or when the PSReadLine internals it reads are missing.

### Differences from Warp

Warp pins an app editor at the bottom and reimplements shell editing. AgentTerm keeps the shell's editor, so the prompt is terminal content. It scrolls away with history when you scroll up, and typing returns to the live edge. There is no per-command block grid. When rows below the prompt are erased, for example when a completion menu closes, the content moves down until the prompt sits on the bottom row again.

### Known limits

- `/agent` entry needs integrated PowerShell with PSReadLine and AgentTerm's Enter handler. A profile that binds Enter itself keeps its binding, and `/agent` lines run as ordinary commands in that pane.
- A card whose reserved rows still hold prompt text stays hidden. Whether the reservation leaves blank rows under the user's real prompt depends on the shell protocol and is a Terra acceptance item.
- Resize re-anchoring reads PSReadLine private fields by reflection. A PSReadLine release that renames them makes the handler abstain, which brings back the upstream misplaced-echo behavior for wrapped prompts. If a key typed before the resize is processed after it but ahead of the chord, PSReadLine applies its stale origin first, and the rest of that input line keeps it.
- Prompt decorations and OSC 133 row marks are not used. ConPTY can reorder OSC against text, so OSC arrival does not anchor rows.
