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
