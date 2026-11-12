# Interaction testing

Use these checks for tab-strip changes. They cover native WinUI interaction that build and terminal tests cannot establish.

## Tab reordering

1. Open a separate test window. Keep existing terminal windows and sessions open.
2. Open PowerShell and Command Prompt tabs so their labels distinguish the order.
3. In each shell, set and print a different session marker. For PowerShell, use `$env:AGENTTERM_DRAG_TEST = 'powershell'; $env:AGENTTERM_DRAG_TEST; $PID`. For Command Prompt, use `set AGENTTERM_DRAG_TEST=cmd`, then `echo %AGENTTERM_DRAG_TEST%`. Record PowerShell's process ID too.
4. Press and hold the left mouse button over the Command Prompt tab label, away from its close button. Move it slowly left across the PowerShell tab. Keep holding until the insertion feedback appears, then release within the tab strip.
5. Confirm Command Prompt is first and remains selected. Switch between both tabs. Confirm each retains its output and session variable. In PowerShell, confirm `$PID` is unchanged. Confirm both shells still accept input.
6. Repeat in the other direction, then start a drag from an unselected tab. With three tabs, also move a middle tab to each end.

Pass when order changes, each tab retains its shell and history, the dragged tab stays selected, and the window does not move. A screenshot or UI Automation tab order establishes the visible result. If using a temporary event trace, native completion with `Move` plus matching tab identities before and after establishes the collection change. Remove temporary probes after diagnosis.

## Left tabs

1. Open a separate test window. Back up `%LOCALAPPDATA%\AgentTerm\settings.json` first, because saving the tab position writes it. Restore the file afterwards.
2. Open PowerShell, Command Prompt, and Windows PowerShell tabs. Split one tab. In each shell, set and print a session marker and its process ID, as in [Tab reordering](#tab-reordering).
3. In **Settings > Tabs**, choose **Left**. Confirm the footer reports unsaved changes and **Discard changes** restores **Top**. Choose **Left** again and save.
4. Confirm the window keeps every tab, the split, and the selected tab. Print each marker again; the process IDs must not change. Return to **Top** and repeat.
5. In Left mode, check hover, the selected fill, outline, and accent indicator, and the close button. Close one tab with its close button and one with middle-click. Drag an unselected tab and the selected tab to new positions; Ctrl+Tab must follow the new order and the selected shell must keep accepting input.
6. Press Ctrl+Shift+E. Up and Down move focus without switching. Enter or Space opens the focused tab and focuses its terminal. Shift+F10 opens a close action labeled with the target tab title beside the row. Escape returns to the terminal. Ctrl+F4 closes the focused sidebar row. With terminal focus, it closes the selected tab.
   - Ctrl+click the selected row. Its selected marker must remain visible and its terminal must stay selected.
   - While a keyboard close is waiting for session disposal, move focus to another control. Closing must not reclaim focus. If focus stays on the closing row, it must move to the selected row after removal.
7. Drag the window from the empty title bar and double-click it to maximize. Check the new-tab button, profile menu, GitHub avatar, and caption buttons.
8. Resize below 680 DIPs. The sidebar shows centered shell icons whose tooltips name the tab and mention right-click tab actions, and the title bar names the selected tab. The new-tab button stays centered over the icon column.
9. Open more tabs than fit. Scroll the sidebar away from the selected tab, then change the selected tab's title by splitting it with another profile, and close an unselected tab. The scroll position must not move. Ctrl+Shift+Tab to an off-screen tab, Ctrl+Shift+E, and switching to Left with the last tab selected must each bring the selected tab into view.
10. In an automation inspector, the sidebar is a `Tab` control with `TabItem` children. Each item exposes its title, selection state, and Invoke. Expanded rows contain a `Close <title>` button.
11. With Windows animations enabled, record the sidebar while adding several tabs using the plus button and Ctrl+Shift+T. Existing rows must stay visible throughout each insertion. Repeat below 680 DIPs in compact mode. Check selection, close, and held drag reorder afterwards.
12. Repeat the visual checks in light mode and high contrast, with Windows animations off, and at a larger Windows text size.

The sidebar explicitly disables `ItemContainerTransitions`. The native ListView defaults can hide existing rows and fade them back in when a tab is added. Setting the property to `{x:Null}` prevents that replay without changing selection, scrolling, or drag handling. An empty XAML property element did not prevent the flash in the diagnostic recording. Keep this check when changing sidebar templates or upgrading Windows App SDK.

On 2026-11-11, recordings of the Release build reproduced the disappearing rows before the fix and showed existing labels staying visible across three plus-button insertions afterwards. A compact-mode recording showed existing icons staying visible across three Ctrl+Shift+T insertions. UI Automation and held pointer input also verified selection, closing an unselected tab without changing selection, and moving Command Prompt before the PowerShell tabs. Windows animations remained enabled. The app's settings file was unchanged.

On 2026-11-11, a Release build on Windows 11 at 150% scaling was checked in dark mode through UI Automation and synthetic input:

- Discard restored **Top**. Closing Settings with an unsaved **Left** kept the saved file at **Top**. Saving switched the open window.
- Two PowerShell tabs, one split pane, and Command Prompt kept their process IDs, markers, and selection across Top to Left to Top.
- Hover, close button, middle-click in both positions, pointer and keyboard context menus, and Ctrl+F4 on a focused row closed the intended tab. After a keyboard close, focus returned to the selected row.
- Ctrl+Shift+E, Up, Enter, Space, and Escape followed step 6.
- Held drags with absolute `mouse_event` moves reordered the top strip and the sidebar, including the selected row; Ctrl+Tab followed the new order.
- New tab, title-bar drag, and double-click maximize worked. The caption buttons were located clear of the title-bar controls at a 502-pixel width but were not clicked.
- Compact mode, trimmed-title tooltips, and overflow scrolling matched step 8.

A light-mode capture came from an isolated preview copy that forced `RequestedTheme` to Light; the production theme path was not changed. High contrast and screen-reader speech were not checked, because they require changing Windows settings.

A second pass on 2026-11-11 used the scripts in `artifacts/left-tabs/round-2`, which drive only the AgentTerm process they start and restore `settings.json` byte for byte:

- UI Automation reported the sidebar as `Tab` and each row as `TabItem` with Invoke, SelectionItem, and ScrollItem. `SelectionItem.Select()` switched tabs, and invoking a row's close button closed only that tab.
- Step 9 passed. A title change, from `PowerShell` to `PowerShell · Command Prompt`, and closing an unselected tab left the scroll position at 0%. Ctrl+Shift+Tab moved it to 72.45%, and Ctrl+Shift+E kept it at 72.45%. Switching to Left with 17 tabs scrolled to the selected last tab.
- The new-tab button and the sidebar icons shared the same horizontal center at 1262, 882, and 502 pixel window widths.
- With Windows animations turned off for the run, a new process read `AnimationsEnabled` as false, and Left mode, keyboard focus, and a drag reorder worked.
- Larger text was checked in a capture-only build that set the sidebar title to 31.5 pixels (14 × 2.25). Rows grew from 48 to 67 pixels without clipping. Writing the Windows text-size registry value did not reach a new app process, so the real Windows text-size setting was not verified.
- High contrast was not switched on, because it re-themes the whole desktop. The indicator's high-contrast brush is the system highlight color on the selected tab's window-color background.
- Compact rows hide the close button, so UI Automation exposes no close button there. Ctrl+F4 and the Shift+F10 menu still close the tab.

The fourth pass captured the production build in Windows High Contrast #2. WinRT reported high contrast enabled. Expanded and compact sidebar selection indicators and keyboard focus remained visible. The harness restored the original high-contrast flags and app settings. This checks navigation appearance in one contrast theme, not terminal text contrast, Narrator speech, or all contrast themes. Expanded and compact rows exposed the same action HelpText. Pointer and keyboard menus named their target tab and opened beside it.

## Automation limitation

On 2026-11-10, the available Windows `sky.drag` and unified CUA drag APIs exposed no duration or held-button controls. A measured `sky.drag` gesture pressed, moved once, and released in about 22 ms. WinUI raised `TabDragStarting`, then completed with `None` and preserved the order. A manual held drag in the same window completed with `Move`, moved Command Prompt before PowerShell, and retained both tab object identities.

Treat this instant synthetic gesture as an inconclusive reorder check. Use an automation API that can keep the button held across multiple moves and wait for insertion feedback, or perform a manual drag. The measured timing is evidence for that tool invocation, not a minimum drag duration requirement.

## Tab icons and alignment

Open PowerShell and Command Prompt tabs. Confirm each shows its shell artwork rather than a generic executable thumbnail. Check the first pill's outer left edge against the pane's 4-DIP gutter, while the icon retains internal padding. Repeat after switching themes, maximizing, and restoring the window. Recheck after Windows App SDK upgrades because the gutter adjustment uses native TabView template parts.

The resource extraction probe decoded icons from PowerShell, Windows PowerShell, Command Prompt, and WSL. A resumed live check exposed a Store PowerShell execution alias that the original probe did not cover. Icon extraction now resolves its executable target without launching it. The durable icon smoke suite checks malformed alias metadata and decodes the installed Store alias, Windows PowerShell, and Command Prompt through WinUI. The Release build passed with zero warnings and errors. A desktop-launched preview visibly displayed the PowerShell artwork and aligned tab gutter.

## Window shutdown

Close the final running tab using its close button. Repeat with a stopped session and with the final pane's close action. Confirm that the terminal window and any Settings window close, and its owned shell/conhost processes exit. Also close a window with multiple running tabs and panes to cover asynchronous cleanup.

The shutdown handler queues its final native close request. The last-tab path has no remaining tabs to await, so cleanup completes synchronously. Calling Close again inside the canceled Closing callback is reentrant and can leave the empty window open. The queued request lets that callback return first. Resumed live testing exposed a separate native backdrop release crash. Rooting its target until disconnect alone did not fix it. Closing the target through public `IClosable` on the UI thread passed pane removal, forced garbage collection/finalization, survivor reattachment, and last-tab shutdown with Settings open in an instrumented app.

## Pane materials

1. Record the existing appearance values. Open a separate test window over contrasting desktop content. Keep Windows transparency and theme settings unchanged.
2. In Settings, choose Acrylic and compare saved opacity values of 0%, 50%, and 100%. Focus the terminal window after each save so inactive-window fallback does not mask the result. Low values should reveal a frosted desktop background; high values should add tint. Text stays opaque. The title bar and outer window keep Mica throughout.
3. Give a second shell profile a different opacity. Create a mixed-shell split and check that each pane retains its own material. Resize, switch tabs, and close one pane. Check that the remaining backdrop still covers its pane without stale rectangles or losing keyboard input.
4. Switch a profile to Solid. Check that its desktop backdrop disappears, the opacity slider disables, and other profiles remain unchanged.
5. Restore the recorded appearance values. Record Windows-controlled fallback behavior separately from the active acrylic result. Do not change Windows configuration just to force the effect.

For the 85% Windows Terminal comparison, use the same terminal background color and the same static content behind each window. Match window position and size, then activate each terminal before capturing it. This user's Windows Terminal configuration disables unfocused acrylic, so comparing an active reference against AgentTerm while Settings has focus is not a valid opacity comparison. Test both already-existing split panes after saving each value, without closing or recreating either pane. The supplied reference screenshots establish a substantial darkness mismatch, but their differing capture times and backgrounds do not establish pixel equivalence.

Resumed checks confirmed existing split panes respond to opacity changes. A separate 85% active capture was much closer in brightness to the supplied Windows Terminal reference; controlled pixel comparison remains blocked by the automation product's Windows Terminal restriction. A diagnostic 100% check found that the native backdrop placement covered the black XAML fallback with gray window material. Native policy callbacks also arrived on a worker thread. The corrected preview passed 85% to 100% to 85%, 0%, profile Solid to Acrylic, focus changes, repeated pane removal/recreation, and last-tab close with Settings open. Its trace recorded 147 brush writes on the UI thread, five native closes, and five completed forced garbage-collection/finalization cycles. No Application Error event was recorded for that executable.

Retain garbage collection after unload in a lifecycle stress harness. Confirm all target-brush writes and native closes occur on the owning UI thread. A separate injected no-policy preview visually confirmed that the fallback now covers the native placement with opaque black while preserving the rounded pane boundary. No Windows configuration was changed. The user's latest 83% preference and PowerShell Acrylic override were restored. These checks are bounded observations, not proof against every native lifetime failure or brief focus flash.

## Pane directory and Git header

1. Open a separate test window. Enter a Git repository in PowerShell. Confirm the top row shows shell, shortened directory, branch, line totals, and change-state indicators without adding a bottom row. Hover the header and read the full path and exact summary.
2. Split right. Leave one pane outside Git and enter a different repository in the other. Confirm each header follows its own shell, without borrowing the active pane's branch. Change directories while an earlier query is pending and confirm the old branch clears immediately.
3. Resize the window and divider. Git status stays beside the directory on the left, separate from the right-aligned pane actions. Counts hide before the branch. Directory text ellipsizes, controls remain available, and the tooltip retains the full summary. Repeat with a long branch and path. Keyboard focus still reaches split and close actions.
4. Check staged and unstaged changes that cancel against HEAD, untracked files, binary changes, an unborn branch, detached HEAD, and conflicts. Untracked files do not increase line additions. Conflicts have no aggregate totals. A subdirectory shows the whole worktree; a nested repository shows its own branch.
5. Repeat directory changes in Command Prompt with spaces, Unicode, `#`, and a literal `%20` in the path. In PowerShell, enter `HKCU:\` and confirm directory and Git content clear. Check both an ordinary prompt and an existing prompt tool.
6. Switch tabs, stop/restart a session, and close panes. Hidden and stopped panes must not start Git queries. Restart clears the old context until the new shell reports. Closing the window cancels pending queries and releases shells.
7. Check light mode and high contrast. Addition/deletion meaning must remain readable through signs, not color alone. Confirm the accessible header summary contains the full path, branch, exact line totals, and named change categories.

Automated smoke checks cover chunked native OSC reports, standard and quoted Windows paths, URI rejection/decoding, real PowerShell and cmd directory changes, non-filesystem clearing, real Git worktrees, net tracked totals, binary changes, conflicts, detached/unborn branches, local upstream counts, nested repositories, cancellation, and hidden-pane polling. Real Git fixtures explicitly skip when Git 2.45+ is unavailable. Live dark-mode single and split panes showed independent paths, branch, counts, and width-dependent hiding. Light/high-contrast UI, screen-reader speech, and WSL execution remain unverified.

## GitHub dashboard

The smoke suite covers the dashboard's parsing, cache, refresh, and text rules with fake transports. It does not open the flyout. These checks cover the native WinUI behavior. Use a separate test window, an installed and signed-in `gh`, and an account with some activity. Never run `gh auth logout` to reach the signed-out state, because it removes the real sign-in.

1. Click the avatar at the right end of the title bar. Confirm the flyout opens over the title bar with its header avatar exactly where the button's avatar was, its right edge meets the minimize button without covering it, and the minimize, maximize, and close buttons do not react. Click the avatar again and confirm the flyout closes. Repeat with the window maximized. The button shows the account picture, or the initials "GH" before an account is known.
2. Press and drag the empty tab-strip area beside the button. Confirm the window still moves.
3. Click each tab: **Activity**, **My PRs**, **Reviews**, and **Repos**. Confirm the list and its count change. With a tab focused, press Left, Right, Home, and End. Confirm selection moves, Left on the first tab wraps to the last, and only the selected tab is a Tab stop.
4. Tab to the heatmap. Press Left and Right to move a week and Up and Down to move a day. Press Home and End. Confirm the readout under the grid names the day and its count, week moves keep the weekday and stop at the ends, and End lands on the latest day.
5. Press F5, then click the refresh button. Confirm the icon spins and the "Updated" line returns to "just now". Close the flyout, wait 5 minutes or more, and open it. Confirm it refreshes on open. Leave it open for 5 minutes and confirm another refresh.
6. Press Esc. Confirm the flyout closes. Open it again and click outside it. Confirm it closes. After each pointer-opened close, type in the terminal and confirm the active pane receives the text. Also open it from the keyboard by tabbing to the button and pressing Enter, then press Esc. Code returns focus to the terminal only after pointer use, so record where focus lands.
7. Click a card and its title. Confirm the default browser opens the matching `https://github.com/` page. Click a checks pill on a pull request and confirm the check list opens.
8. Narrow the window until the avatar disappears. Confirm the close button is never overlapped or triggered by the avatar. Widen the window and confirm the button returns. Repeat with the flyout open and confirm it closes when the button hides.
9. Signed-out panel. In a new PowerShell session, point `gh` at an empty config folder and clear token variables, then start the app from that session. Use `$env:GH_CONFIG_DIR = (New-Item -ItemType Directory "$env:TEMP\gh-empty-$(Get-Random)").FullName` and `Remove-Item Env:GH_TOKEN, Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Open the flyout. Confirm the "Sign in to GitHub CLI" panel shows `gh auth login --hostname github.com`. Confirm **Copy command** puts that text on the clipboard and **Retry** shows "Checking..." and returns to the panel.
10. Missing-`gh` panel. In a new PowerShell session, remove every `PATH` entry that contains `gh.exe`, then start the app from that session. Open the flyout. Confirm the "GitHub CLI not found" panel shows `winget install --id GitHub.cli`. The app reads `PATH` once at launch, so restore it by restarting the app from a normal session.
11. Switch Windows between light and dark with the flyout closed, then open. Repeat with it open. Confirm heatmap levels, legend, pills, and text stay legible.
12. Turn off Windows animation effects and note the original setting. Open the flyout and refresh. Confirm there is no entrance motion, the tab pill and heatmap do not animate, and the refresh icon dims instead of spinning. Restore the setting.
13. Minimize the window and restore it, with the flyout closed and again with it open. Confirm the app keeps running and the avatar is still flush against the minimize button. A minimized window reports a negative caption inset, which once crashed the app.

Pass when the button never overlaps a caption button, the window still drags, every key does what it lists, and each panel shows only for its own cause. After the last check, confirm the app made no change to `gh` sign-in by running `gh auth status --hostname github.com`.

Not verified live so far: high contrast, a failed avatar image (the picture should keep the initials and the button tooltip should say the avatar could not be loaded), and the section-level "Stale data." line (it needs one section to fail after an earlier success). Treat these as open, not as passing.

## Smart prompt verification

PR #3 review-fix automation on 2026-11-11 passed `scripts/check.ps1`, including the Release x64 build with zero warnings/errors, native timeline rendering, real ConPTY, renderer and shared-agent checks. The native timeline produced eight distinct active frames and static approval/completed frames. Added lifecycle regressions cover same-directory terminal replacement, Stop during blocked availability/session creation, repeated Stop, old interruption completion after a newer turn, native error/idle ordering, bounded failed drains and fresh-session retry. The shared-controller smoke fixture serializes events and assertions on a pumped UI synchronization context. These automated results do not establish live approval layout or keyboard focus behavior.

The real Copilot smoke also passed on the review fixes. Its approved command returned an unprompted shell-only marker, fixture cwd and unchanged PID 73056. Stop interrupted `Start-Sleep 60` and the same shell/alias remained usable. Boat's portable terminal, controller lifecycle and Copilot lifecycle/normalization checks passed. Its old Git explicitly skipped real Git fixtures, which passed on Windows. Local and Boat pre-commit passed after removing one extra final blank line from a new test file.

Use a local PowerShell pane. Wait for the visible ready state. Confirm a single composer owns the idle draft. Type `$value = 41`, submit, then `function answer { $value + 1 }; Set-Alias aa answer`. Enter `aa` and confirm it runs in the shell and prints 42. Shift+Enter must retain multiline input. Run an unknown command name. Verify `/agentx`, `/Agent` and a leading-space ` /agent` remain shell text. Confirm no agent starts after the shell error.

Run `Read-Host 'Name'`, type in the terminal and press Enter. Repeat with `Read-Host -AsSecureString 'Secret'` using a disposable test value. Run `Start-Sleep 60` and Ctrl+C in the terminal. Check that readiness returns. Use Native prompt or Escape at an idle prompt, then verify native prompting and `$value`. Use Smart prompt to return. Verify IME candidate selection does not submit on its confirmation Enter. Repeat pane switching and keyboard Tab navigation with retained drafts.

Enter `/agent` followed by a short explanation request. Confirm streamed text and the bound pane directory. Ask it to create a harmless file in a temporary directory. Deny, then ask again and approve once. Ask it to ask a multiple-choice question. Reply. Interrupt a long response. Confirm the shell still works. Open a second pane and confirm independent output and drafts. Close a running agent pane and confirm it releases its runtime. In cmd, confirm native input remains usable. In WSL, confirm Agent reports unsupported local execution instead of using the app directory.

### Historical verification before the /agent redesign on 2026-11-11

Release x64 built with zero warnings and errors. A separate window retained the isolated directory, variable, function and alias. Auto routed the alias to Command and printed 42. Ambiguous input disabled Send. A real Copilot turn responded. Approve once created an isolated fixture. Denying a read returned a rejected-tool result.

On the latest build, a roughly 680 by 500 window showed the editor, status, Stop agent and Send together during a running turn. Stop agent interrupted a request. A subsequent agent turn returned HELLO. The test window and its identified Copilot runtime exited after close. A request for ask_user was interrupted before a question appeared, so question response remains unverified. The shorter-window resize, multiline input, Read-Host, Ctrl+C, raw toggle, split drafts, shell after interruption and IME remain unverified in this pass. The prior gdigrab video was black and is not valid evidence. No usable video was produced. Screenshots are in artifacts/smart-prompt-evidence.

### Existing-pane host execution acceptance

Use the Release x64 app built by `scripts/check.ps1`. At the terminal prompt run `$terraPane = 'pane-only-42'; function TerraProof { $terraPane }; Set-Alias terraProofAlias TerraProof; Set-Location $env:TEMP; $PID`. Record the printed shell PID before invoking the agent. Enter `/agent Use run_in_terminal to run exactly terraProofAlias; (Get-Location).Path; $PID and report the output`. Approve that exact command. Verify visible execution and the shell-only value/cwd in tool output, and compare the agent-reported PID with the recorded baseline. The visible `Command started [a:id]` and `Command ended [a:id]` lines are intentional ConPTY ordering fences, not output returned to the model and not an agent-turn outcome.

Ask the agent to run `Start-Sleep 60`. Approve once. Verify the working status, blocked competing shell submissions, and no accidental raw typing. Press Stop. Wait for readiness. At the terminal prompt run `terraProofAlias; $PID`. Verify the same value and PID. For interactive handoff ask for `Read-Host 'fixture input'`, approve, click **Type into command**, enter a harmless value in the terminal and verify completion. Repeat in a second pane and verify the variable does not exist there. Terra owns this live UI acceptance; no new screenshots or recordings are claimed by implementation checks.

The authenticated smoke now verifies actual SDK ToolStarted, callback invocation, returned unique variable value, alias, cwd and original process ID. Its fixture approves only exact `paneProofAlias; (Get-Location).Path; $PID` and `Start-Sleep 60`. The marker value is never included in the prompt. It also checks stop recovery. Deterministic tests cover fragmented UTF-8, metadata/echo exclusion and reordered OSC. Real ConPTY tests cover output beyond the viewport, truncation, state/alias/cwd, exit 7, busy rejection, input exclusion, cancellation, recovery and two-pane isolation.

Implementation gate evidence on 2026-11-11. Shared smoke passed the ordered-fence and byte-fragmented decoder regressions. ConPTY smoke passed the host execution cases. Real Copilot SDK emitted `ToolStarted run_in_terminal {"command":"paneProofAlias; (Get-Location).Path; $PID"}` and the actual callback returned marker `311dce626960446f8ca82ca06e814cd6`, fixture cwd ending `agent-term-copilot-63be2ffcd7484268ab153987edaaf9ee`, and unchanged PID 78456. A second actual tool execution of `Start-Sleep 60` was interrupted and the original alias/value remained usable. No UI verification is claimed by these checks.

Final Boat verification on 2026-11-11. The parent uploaded the same-terminal and grouped-timeline source to the dedicated `bx_62gy77k3` sandbox. `scripts/check-linux.sh` passed, including native Ghostty, terminal smoke, shared timeline behavior and Copilot normalization. Pre-commit passed over all source. The sandbox's older Git explicitly skipped real Git fixtures; those passed on Windows. This portable check does not verify WinUI or Windows ConPTY.

Final correction verification. `scripts/check.ps1` passed with zero Release build warnings/errors, icon decoding, terminal/status, real ConPTY, renderer and shared-agent suites. Final targeted ConPTY adds syntax-error completion, inherited environment persistence and owned Read-Host handoff. Pre-commit passed on tracked changes and all untracked source files. Local logs are `artifacts/pane-tool-check.log`, `artifacts/pane-tool-conpty.log`, `artifacts/pane-tool-copilot.log` and `artifacts/pane-tool-precommit.log`. No source, screenshots or logs were uploaded. No commits, pushes, PRs or UI automation were performed.

Live Release UI verification on 2026-11-11. In a separate window, Command mode established the fixture in `artifacts/smart-prompt-evidence/workspace` and printed PID 52992. The approved exact agent command returned `pane-only-42`, that workspace path, and PID 52992. The terminal displayed execution. `same-terminal-proof.jpg` captures the result. The approved `Start-Sleep 60` showed **Agent using terminal** and disabled Send. Stop produced `Interrupted`; a subsequent Command `terraProofAlias; $PID` returned `pane-only-42` and 52992. `same-terminal-recovery.jpg` captures recovery. The test window closed and the app process exited. Interactive Read-Host and second-pane isolation were not exercised in this live UI pass. Earlier screenshots are not evidence of this same-terminal pass.

### Grouped timeline acceptance for Terra

Implementation automation covers shared normalized events and adapter presentation. It does not establish live WinUI appearance, animation or accessibility. Prior same-terminal screenshots above predate the grouped-row refinement.

1. In the Release x64 app, preserve the existing same-terminal fixture or create `$terraPane = 'pane-only-42'; function TerraProof { $terraPane }; Set-Alias terraProofAlias TerraProof`. Record `$PID` at the terminal prompt.
2. Enter `/agent` and ask it to call run_in_terminal twice, sequentially, without commentary between calls. First command `Start-Sleep 8; terraProofAlias; $PID`. Second command `(Get-Location).Path; $PID`. Approve each exact command. While waiting for approval, verify static readable labels and enabled approval actions. While running, verify a compact terminal-icon row with a moving highlight on text only.
3. Expand the first running group by click or Space. Verify the command is visible. Leave Output collapsed. Approve the second command. Verify the same group stays expanded through completion and shows both commands. Toggle closed. The settled summary must say **Ran 2 commands**, with no invented file-read count.
4. Toggle open with keyboard. Expand Output and copy the marker, directory and PID. Verify no serialized result envelope, outputFormat, schema or protocol status field. Natural command output can contain arbitrary user text. Confirm the marker is pane-only-42 and PID matches the value recorded at the terminal prompt in step 1.
5. Leave the group expanded. Switch panes and return. Start a later explanatory turn. Verify the earlier group remains expanded. Scroll older activity while a response streams. Verify no forced jump to bottom.
6. Ask for a failing terminal command and deny another. Failures/cancellations must remain visible even with the group collapsed. Ask for Start-Sleep 60, approve, then click Stop. Verify motion stops and shell-only marker/PID recover as in the existing-pane acceptance above.
7. When reduced motion or high contrast is already enabled, verify readable static labels. Do not change Windows configuration for this pass. Verify inactive pane and unload stop shimmer. Verify screen reader toggle names, output selection/copy, sticky editor/action row, and native IME behavior. Report these as live checks only after observing them.

Automated refinement checks. `dotnet run --project tests/AgentTerm.Agents.Smoke -c Release` exercises overlapping/out-of-order IDs, sequential grouping across approval, user expansion persistence, visible failures, interruption settlement, bounds and later turns. `dotnet run --project tests/AgentTerm.Copilot.Smoke -c Release -- --normalization-only` checks argument/result allow-listing without authentication or terminal execution. Run the same project without the flag for actual SDK same-terminal and stop-recovery proof. The parent owns updating and rerunning the existing Boat sandbox. No additional sandbox is required.

Implementation refinement evidence on 2026-11-11. Release x64 builds passed with zero warnings/errors. Shared fake-event and Copilot normalization checks passed. Real ConPTY smoke passed. Authenticated Copilot returned an unprompted unique shell-only marker, the fixture directory, and unchanged PID 36200 through normalized display output without serialized metadata. Stop recovered that same shell and alias. This is automated execution evidence, not new UI evidence.

Live grouped-timeline attempt on 2026-11-11. The Release window established `terraProofAlias` and printed shell PID 65856. After submitting the two-command Agent request, the window disappeared before an approval or timeline was visible. Windows Application event 1000 recorded AgentTerm.App.exe PID 22104, Microsoft.UI.Xaml.dll exception 0xc000027b at 10:24:58. Event 1001 recorded APPCRASH with combase.dll and 0x80070490. No AgentTerm.App process remained. The cause is not established. Grouping, expansion, output, shimmer and accessibility were not verified. No new screenshots or video were captured. No Windows settings were changed.

Native diagnostic follow-up on 2026-11-11. A temporary loaded ActivityLabel probe reproduced COMException 0x80070490 at AccessibilitySettings.HighContrastChanged subscription, before storyboard startup. Replacing that subscription with UISettings.ColorValuesChanged left the same loaded probe running for five seconds without an unhandled exception. The probe and exception logger were removed. This isolates a real native failure, but does not prove the prior agent crash had no additional cause. The actual grouped turn, animation, keyboard behavior and final screenshots remain unverified in this follow-up. No Windows settings were changed.

Live grouped follow-up on 2026-11-11. Screenshot-coordinate input reached the composer despite the accessibility focus report naming the terminal. The fixture printed PID 79784. The two exact approved commands completed in the same pane. The group remained expanded across both calls and settled as Ran 2 commands. Click collapsed it and Space reopened it. The assistant reported pane-only-42, C:\Users\adityasharma and PID 79784. Expanded Output exposed prior terminal echo and boundary text as well as the result, directory and exit code. No serialized schema envelope was observed. This contamination needs investigation. Approval controls were enabled but clipped at the smaller window size; maximizing exposed them. A running capture exists, but movement was not established from multiple captures. Pane switching, later turns, failure, denial, interruption, selection/copy, accessibility speech and IME were not tested in this follow-up. The test process exited after window close. No settings changed. PNG captures are group-running.png, group-expanded.png and group-collapsed.png in artifacts/smart-prompt-evidence.

Agent surface and task card implementation on 2026-11-11. Automated evidence only. Release x64 app build passed. A ten-second launch of the Release app stayed responsive, then was closed; no window content was inspected. Agents smoke covers `/agent` routing (`/agentx`, a leading space and `/Agent` stay shell text), bare `/agent`, task titles, follow-up routing after reopen, invocation order and the 32-task bound. ConPTY smoke covers card row reservation at a ready prompt (two blank rows, no echo), an on-screen card polled through a 60x20 resize repaint and back with its blank rows intact, anchor stability across 60 lines of later output and a 60x20 resize round trip, the same shell PID and variables after reservation, expiry after 10,100 lines of trimming, and expiry after Clear-Host. ConPTY smoke also reproduces the resize replay that contaminated tool output: before the fix, a resize mid-command returned earlier echo and the start fence. Capture now keeps text after the latest replayed start fence. Blank repainted rows remain in that output. When the fence has scrolled off screen before a resize, tail lines are still duplicated; this was measured at three copies and is not fixed. Not verified live: surface layout and density, card appearance and position, approval card keyboard flow, shimmer motion, reduced motion, high contrast, light theme, narrow panes, screen reader output, IME and wheel forwarding over cards.

Live redesigned surface acceptance on 2026-11-11. A bare /agent opened an empty task and reserved a compact card at the invocation. The card reopened the surface. At approximately 680 by 500, both approval buttons were visible. The two exact approved commands returned pane-only-42, C:\Users\adityasharma and the original shell PID 79364. Escape during execution returned to the terminal without stopping the task; the working card reopened it. The settled group said Ran 2 commands, expanded to both commands, and the first detail exposed output and Copy output. The card stayed above subsequent terminal output. Wheel input over the card scrolled history. The app remained responsive and the test process exited after closing. A post-task shell command was submitted, but its result was not inspected because history was scrolled up.

Escape did not leave the surface after clicking tool details, nor after clicking the empty surface title. The back button worked. A scoped preview-key experiment built successfully but did not fix the empty-surface case and was removed. Focus handling remains open. Screenshots surface-task.png, terminal-task-card.png, surface-approval.png and surface-working-a.png/b.png are real captures from this pass. The working captures are approximately 0.4 seconds apart; motion has not been established by image comparison. Copy activation, card reopening after the final shell command, and further accessibility checks remain unverified. No video or Windows settings changes. Shared pane conversation and the previously documented resize replay limitation still apply.

### Focused live regression, 2026-11-11

- Release app at approximately 680 x 500. Clicking the agent surface title then pressing Escape returned to the terminal. Clicking expanded tool details then pressing Escape also returned to the terminal.
- Requested `Start-Sleep 12; Write-Output 'shimmer-done'` through `run_in_terminal` and approved once. The completed card showed `shimmer-done` and exit code 0. Reopening the card preserved its expanded output. After running `Write-Output 'after-agent-card'` in the terminal and reopening the same card, the output remained visible.
- Captured three running states over approximately 2.2 seconds with the pointer away from the label. A moving bright highlight on `running` was not visually distinguishable in those captures. Shimmer remains unverified. The approval-waiting and settled states were not observed over multiple phases, so absence of animation there is also unverified.
- Closed the test app. No `AgentTerm.App` process remained.

Final parent verification on 2026-11-11. Escape is handled at the window shortcut boundary for the active pane, with IME and modifier guards. Terra confirmed return from the title and expanded tool details, preserved completed output after a later shell command, card reopening and app exit. The single-tool row previously hid its animated group header and used a static running label. Its visible running label now uses ActivityLabel. The native AgentTerm.AgentUi.Smoke suite loads the production timeline, styles and activity label in WinUI. With Windows animations enabled and high contrast disabled, eight render samples produced eight distinct active pixel frames; approval-waiting and completed samples stayed identical. This is native rendered-state evidence, not a claim that the live capture made the subtle sweep visually obvious. The test also exercises the native accessibility-event subscription that previously crashed. Release x64 rebuilt with zero warnings/errors after removing the delegate's experimental handler. Final Boat portable suites passed; lint passed after recreating its missing temporary environment under /home/user/agentterm-lint. The sandbox was stopped with its data preserved.

### PR #3 focused Windows UI attempt, 2026-11-11

Launched the requested Release executable in a new window. Native mode displayed "Click Smart prompt to return" and Escape left native mode active. Smart prompt button returned to the composer. This did not prove shell receipt of Escape.

Set `Read-Host 'review input'` in the composer through UI Automation and clicked Send. The prompt appeared. Immediate `type_text` did not visibly enter the value; Enter completed with a blank response. A subsequent `a` appeared in the composer. Because submission used UI Automation SetValue and Send rather than normal typed Enter, this is a potential focus issue, not a conclusive reproduction of the requested keyboard flow. The remaining Ctrl+C, single-flight, approval, and question checks were not verified. No screenshot was saved. Closed only the launched window.

### PR 3 focused interaction pass, 2026-11-11

- Release window approximately 681 x 501. Submitted `Read-Host 'review input'` with Enter from clicked composer. Without a terminal click, individual a, b, c and Return produced `review input: abc` and output `abc`.
- Submitted `Start-Sleep 60` using Send, then Ctrl+C. A subsequent composer command submitted with Send printed `after-interrupt`. This also confirms normal command admission after Read-Host. The Read-Host Send path was not separately tested.
- The agent requested approval for the exact 13-line harmless Write-Output command. Scrolling the approval text reached `PR3_FINAL_UNIQUE_MARKER` while Run and Reject remained visible. Approved it. The tool showed done and the agent reported all lines printed. Screenshot: `artifacts/smart-prompt-evidence/pr3-approval.png`.
- A follow-up requested ask_user with four verbose choices. The question appeared. Scrolled the choices, selected the fourth, and the conversation recorded and acknowledged that choice. Screenshot: `artifacts/smart-prompt-evidence/pr3-question.png`. These choices fit on single lines at this width, so multiline wrapping was not verified.
- Rapid Enter startup admission was not tested. This pass does not establish race coverage. No implementation changes made.
- Closed the test window. `Get-Process AgentTerm.App` returned no process.

## Program status hover

1. In a separate test window, run the README program-status example. While it sleeps, switch to another tab. The background tab must show approval needed, then done. Hover for the message.
2. Switch between Top and Left while the report is present. Both must show the same state and details without restarting the shell. Resize Left below 680 DIPs and hover the compact icon.
3. Emit a root working report and child blocked reports with slash-separated IDs and base64 titles. Hover must retain the root and children, with blocked taking icon priority. Clear one parent ID and confirm only that subtree disappears.
4. Split the tab and emit different reports in both panes. Hover must label each pane and retain both even when one pane is inactive. Closing a pane removes its reports.
5. Emit working, blocked, done, and error reports. A new prompt removes working and blocked but retains done and error. Emit `ESC c` to reset, or `OSC 7501;state=clear ST` to clear. Confirm the shell icon returns in both layouts.
6. Report enough children to overflow the card. Hover each tab layout and move into the card. It must stay open for scrolling and text selection. Escape must dismiss it while terminal focus stays unchanged. Focus a tab and press Alt+Down. End and Ctrl+End must reach the final report. Escape closes the card and restores focus to the tab. Keep open uses the same card. No tab context menu offers a Program status action; the sidebar retains its close action. Hover an empty tab and emit a report without moving the pointer. The card must open automatically in Top, expanded Left, and compact Left. Reopening starts at the first report; updates while reading preserve the scroll offset.
7. Repeat with keyboard focus, high contrast, and large text. Status text must remain readable without interpreting terminal messages as markup.

### Program status hover verification, 2026-11-11

The native AgentUi smoke run exercises pointer hover, crossing into the card, leaving it, and updates while open. Hover preserves input focus, and Escape reaches the terminal input while dismissing the card. Clicking Keep open pins the card after the pointer leaves and focuses its reports. Alt+Down focuses the shared card; Escape restores tab focus. Tests cover 256 reports, end-of-list navigation, selectable text, Shift+Home, and insertion, removal, and urgency reordering while preserving the selected text and visible report.

Native captures cover dark, light, and Windows high-contrast cards, nested reports across panes, long unbroken text at a narrow width, and production top, expanded sidebar, and compact sidebar tabs. The native check asserts the acrylic brush outside high contrast. A real local executable supplies the tab icon. The activity control produced six distinct rendered frames while working, showed 62% determinate progress, and stayed static on completion. With Windows animations disabled it produced one frame and retained the visible determinate ring. Windows appearance and app settings were restored after capture.

The full `scripts/check.ps1` suite passed before the final presentation refinements. The release app build and native UI checks passed after those refinements. Native accessibility-tree checks confirm urgent-first reading order, nested report text, state, and percentage. Screen-reader speech and enlarged system text were not exercised in this pass. Production screenshots establish tab and card appearance; their terminal canvas is blank in the screen capture, so they do not establish terminal rendering. The separate renderer smoke suite checks terminal pixels.

The native layout regressions also cover an earlier report growing without reordering, and insertion below the visible report when creation order differs from display order. Run them without global input using `dotnet run --project tests/AgentTerm.AgentUi.Smoke -c Release -- --status-layout`. The full interactive suite requires the smoke window to own the foreground before every injected input batch; it fails without sending input if ownership is lost.

### Program status hover follow-up, 2026-11-12

Removed the Program status menu action from both tab layouts. The existing hover timer now also starts when the first report arrives under a stationary pointer. Added interactive regressions for that case in Top, expanded Left, and compact Left, and for the sidebar close-only menu.

The isolated Release build, native `--status-layout` checks, and changed-file lint passed. The updated app launched and remained responsive. The interactive suite stopped at its foreground-ownership guard before injecting input, so the new pointer regressions remain unverified. The previous app window and its sessions were left open.

## Terminal color themes

1. In Settings > Profiles > Defaults, select Auto, Ocean Light, and Forest Dark. Save. In a test window, change Windows appearance. Only the active palette must change; both saved slots must remain selected. Output, shell PIDs, scroll position, and selection must remain intact.
2. Select forced Light and forced Dark against the opposite Windows appearance. Verify the corresponding slot is used and the composer, IME text, pane labels, and selection remain readable. Return to Auto. Verify system tracking resumes instead of remaining stuck in the previously forced appearance.
3. Change each inactive slot, then switch appearance. The saved inactive choice must be used. Open split panes and inactive tabs before changing slots; activate them afterwards and verify the current palette appears without a shell restart.
4. On one profile, override only the light theme and inherit its scheme and dark theme. Change Defaults. Verify inherited fields update while the override stays fixed. Explicit profile Auto must override forced Defaults. Reset to defaults restores all three fields to inheritance after Save.
5. Change each field and Discard. Verify saved values return. Close Settings with unsaved edits; no edits should apply. Restart a test window and verify both slots and the mode persist.
6. Check solid and acrylic panes, selected text, and block/bar/underline cursors for every variant. Keyboard navigation must reach all scheme and palette selectors and the Save and Discard buttons.

Automated coverage checks slot resolution against system appearance, forced modes, scheme reports, live recoloring, ANSI and truecolor resolution, selection retention, history and alternate-screen retention, OSC overrides/resets, preference persistence/migration/inheritance, and actual renderer pixels for every built-in palette and selection. Theme-specific snapshots are produced by the renderer smoke suite. Native Settings selector interaction and real Windows appearance switching remain manual checks.


## Prompt UX Round 1 acceptance

Implementation changes, not new runtime evidence. Run with terra-high in an isolated app. Do not close the existing user's app.

1. Start a fresh supported PowerShell pane. Wait beyond readiness. Smart must show one composer, no fresh `PS>` cue, and one bordered Smart mode button. Run `Write-Output 'round-1-shell'`. The echo must contain the submitted command without `__AgentTermStart;`. Run `Write-Output ('__AgentTerm' + 'Start; user-owned-output')`. Its output must remain visible. Run `cmd /c exit 7` and inspect failure status through the ConPTY suite.
2. Type a Smart draft and press Escape. Only the draft clears. Use the mode button to enter Legacy. Its native prompt and full-height input remain. Type a native draft, switch to Smart, execute a command, and return to Legacy. The native draft and shell PID must survive. Historical output is never removed to hide a new prompt.
3. Enter `/agent` with a unique harmless request. Verify pre-submit routing guidance says ask agent. During work, a quiet task has a progress ring and Working status. Follow-up input is read-only and Send and send/newline hints are absent. Esc returns to terminal without switching mode or stopping work. The task card reopens the task. A free-text question must still accept input.
4. Complete, stop, and safely fail separate fixture turns. The surface and history card must distinguish Done, Stopped, and Failed. A follow-up clears the previous outcome while running. Preserve streamed/final answer reconciliation and tool detail expansion. The title must stay horizontally fixed when Stop becomes unavailable. The completed back tooltip must not claim that work continues.
5. Capture narrow and split panes, keyboard focus, fresh Legacy `/agent`, terminal return, cancellation and safe error. Legacy glyph investigation remains open. Compare actual terminal cell code points with the user's configured private-use glyphs and a native Windows Terminal prompt before changing fonts or shell encoding. Do not treat a font preference as the cause without that evidence.

Regression suites are written for shell echo/prompt fallback/history/output preservation, status/capture/first-statement syntax, retained task outcomes, and native timeline progress/outcome rendering. Round 1 implementation did not execute them. Earlier verification entries above remain historical evidence only.

## Prompt UX Round 2 acceptance

Resize input implementation follow-up. Smart submissions now use a session-local UTF-8 mailbox and send only the execution chord through ConPTY, so PSReadLine never renders encoded transport. The focused and full ConPTY routes include long-input scrollback, short-command adjacency at 72/40/120 columns, intentional blank output, Unicode/multiline capture and Legacy history regressions. Compilation only is established for this change. Terra-high must run both routes as specified in [the resize handoff](notes/prompt-ux-round-2-resize.md). Refresh the launch snapshot's terminal source and DLL before UI acceptance. Its old diagnostic DLL contains relative probe writes.

Implementation and compile evidence only. Terra-high owns test execution and computer use. Preserve user app PID 37216. Use `artifacts/prompt-ux-review/round-2/app/AgentTerm.App.exe`, not the normal build or round-1 output.

1. In Legacy, submit a fresh unique `/agent` request. The pane header must still say Legacy on the agent surface. Escape after completion must restore an actual native prompt with no app composer. Reopen its history card and verify the same response. Repeat Escape during Starting/Working. Work continues. When no command owns input, native shell input must work. A later agent command temporarily acquires managed input without changing the preference. While it owns input, normal typing must not reach it; Type into command remains the explicit opt-in. After completion/cancellation, native input returns. Test a native shell command during background agent thinking. An overlapping agent command must report Busy, not mix input.
2. At compact and split widths, inspect the pane header mode control and the agent header. Return and Stop retain their slots. The title starts at its beginning, ends with an ellipsis if needed, and has a full tooltip. The mode control must not leave a permanent extra row. Test keyboard mode activation, Stop activation, Ctrl+C, Escape from details, and card reopening. Check inactive panes keep their drafts and preference.
3. Bare `/agent` must say Ask the agent. A submitted turn must show a muted regular request and primary readable answer. During work, show one "Agent replies paused until this turn finishes" message without send hints or the input glyph. Stop during startup and during a running turn. Each must show one Stopped outcome; tool cancellation/error details must stay visible. Follow-up submission must remove the settled status while retaining prior outcomes in history.
4. Submit `Write-Error 'round-2-focus'` and immediately type `echo round-2-recovery`, with no screenshot viewer or other window activated between actions. Log actual focused element before submit, during execution, and after readiness. Repeat with Send, then intentional Tab away during execution. Do not call a later click-to-focus a pass for automatic focus return. Code inspection alone has not reproduced the round-1 lost entry.
5. Run the focused prompt-mode suite. It checks the command/output adjacency before and after ConPTY resize. If it fails, save `ReadText()` and `ReadFrame()` rows before and after resize, plus raw ConPTY redraw bytes. Compare libghostty-only resize with the delayed ConPTY repaint before changing either owner. Never delete blank historical rows as a correction.
6. For Legacy question marks, save actual `TerminalCell.Text` rune values alongside the profile's configured separator values. Compare the same profile in native Windows Terminal. Literal U+003F in both outputs is not a font-renderer defect. Preserved private-use codepoints with missing glyphs require checking rendering/fallback. Do not change the host font, shell encoding, or profile based on the JPEG alone.

Run the isolated ConPTY executable with `--prompt-mode-only`, then the full ConPTY suite. Run Agents smoke and AgentUi smoke (`--timeline-only` isolates timeline checks from the known baseline details-navigation failure). No runtime checks were executed by round-2 implementation. Preserve the repaired namespace-dependent execution assertions after Write-Error; a standalone using directive legitimately retains prior status.

Round 2 focus ownership repair and exact current-build live checks are in [the focus handoff](notes/prompt-ux-round-2-focus.md). Compilation only is established. Terra-high must execute policy regressions and verify actual focused elements, Enter, Tab, keyboard mode activation and inactive-pane isolation against the new focus-app binary.


## Prompt UX Round 3 acceptance

Use the isolated Round 3 binary and commands in [the Round 3 handoff](notes/prompt-ux-round-3.md). Round 2 paths above are historical. Terra-high owns test execution and live acceptance. Preserve PID 37216.

The mode button still uses standard Tab, Space and Enter. Explicit activation now hands focus to the selected input, superseding only the round-2 instruction that focus stay on the mode button. Passive readiness must still preserve intentional navigation and inactive-pane focus.

Capture Legacy idle immediately after switching, native typing without a click, a fresh unique native task, and Escape after completion and cancellation. Include a restored nonempty native draft before the native task to exercise cursor-offset consumption. Native prompt and cursor must appear below, never under, task cards. Escape while an agent command owns the shell must not opt into its input. Type into command remains the separate explicit opt-in.

Also verify Starting and Working mode exclusion, functional startup Stop, pointer Stop and Ctrl+C, no-reply and partial-reply outcomes, elapsed time after three seconds, reduced motion, stable composer baseline/hint slot, Smart one-line unique `/agent` routing, error recovery, split ownership and compact active Stop. Preserve complete transcript context. No round-3 runtime passes or visual success are established.

Prompt mode keyboard path. Press F6, then Space or Enter to activate the focused mode control. This path still requires live keyboard and focus-ring verification in the current build.

## Integrated Smart terminal acceptance

This supersedes every composer, prompt-mode handshake and Smart draft step above. Those sections are historical. The shell's own prompt is the only command input in Smart and Legacy. The design is in [the design note](notes/integrated-smart-terminal-design.md) and the app handoff is in [the Opus handoff](notes/integrated-opus-handoff.md).

Implementation and compile evidence only. Terra-high owns test execution and computer use. Preserve user app PID 37216. Use the isolated app at `artifacts/integrated/opus/bin/AgentTerm.App/release_win-x64/AgentTerm.App.exe` only after the parent confirms it was rebuilt against the final Terminal sources. Do not use the default output.

Gate 0 runs first. It is Sol's live edge proof in the Terminal smoke suite. If it fails, stop and report before UI capture.

Then capture these four states in a fresh supported PowerShell pane in Smart, before any further UX judgement.

1. **Launch.** The shell's real prompt and its cursor sit on the bottom row. The rows above are terminal background. No text box, placeholder or hint appears anywhere in the pane. The pane header shows **Smart ⇄**, and no **/agent unavailable** label once the prompt is ready. If the label appears, record its tooltip text.
2. **Tab completion.** Type `Get-ChildIt` and press Tab. It completes to `Get-ChildItem` on the bottom row. Then type a directory prefix and press Tab. It completes the path. Press Ctrl+Spacebar for the menu, then Escape. Record whether the prompt returns to the bottom row.
3. **After a command.** Run three short commands. From top to bottom the order is oldest to newest. Each output directly follows its echo, and the newest prompt is on the bottom row. Press Up and confirm the last command is recalled in the same shell.
4. **Scrollback.** Fill more than one screen, then scroll up. The prompt scrolls away with the output, unlike Warp. Type a key. The view returns to the live edge without passing it.

Further checks, after the four captures are reviewed.

5. Click and drag in the blank rows above a short document. No selection starts there. Drag from a prompt cell up into those rows and copy. The text matches the cells. Click cells near the prompt. Selection starts on the clicked cell.
6. Switch to Legacy during a running `Start-Sleep 5` and again in a full-screen program. The switch is instant, the grid size text does not change, and Legacy draws from the top. Switch back. F6 then Space or Enter switches the focused pane's mode.
7. `Clear-Host` and Ctrl+L with scrollback present. In Smart, the prompt is on the bottom row with history above it, with no blank gap between history and the prompt.
8. Type `/agent` with a unique harmless request at the shell prompt in Smart and in Legacy. The surface opens with focus in its reply field. Escape returns to the terminal with the shell at its own prompt. Up recalls the `/agent` line. The task card sits on two blank rows and covers no prompt text. If the card is missing, record the two rows' text. A hidden card over non-blank rows is the designed behavior, not a pass.
9. While an agent command runs, typing does not reach it. **Type into command** in the pane header hands input over. The mode switch stays usable.
10. Ctrl+Shift+Y in the terminal reopens the latest task. With no task, the chord reaches the shell.
11. Split right and down. Each pane keeps its own mode, and a shell-driven `/agent` open in one pane never moves focus out of another pane.
12. IME composition at the bottom row. The candidate window sits at the cursor in Smart and Legacy.

Regression commands, for Terra only.

```powershell
dotnet run --project tests/AgentTerm.Renderer.Smoke -c Release --artifacts-path artifacts/integrated/terra -- artifacts/integrated/terra/renderer-smoke.png
dotnet run --project tests/AgentTerm.Terminal.Smoke -c Release --artifacts-path artifacts/integrated/terra
dotnet run --project tests/AgentTerm.AgentUi.Smoke -c Release --artifacts-path artifacts/integrated/terra -- --timeline-only
```

The renderer suite now includes `GridGeometryChecks`: first-row inset, sub-cell remainder, clipped rows, caret and IME origin, hit testing at the inset boundary, drag clamping and mouse translation. Its last case needs Sol's live edge model and fails if a fresh bottom-anchored terminal does not report a five-row inset.
