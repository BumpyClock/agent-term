# Agent Term

Program titles omit directional controls and shell executable paths before applying the display length limit. Unicode grapheme joiners remain intact. Program status marks follow high contrast changes even without new reports. Nested task reports without visible parents retain their full IDs so equal task names remain distinguishable.

A Windows terminal built with WinUI 3 on Windows App SDK 2.6.5, .NET 10, Win2D, Windows ConPTY, and libghostty-vt 1.0.0.

Each tab or pane owns an independent shell, terminal state, and scrollback. The profile menu discovers installed PowerShell versions, Command Prompt, and individual WSL distributions. PowerShell loads your normal shell profile. Closing a pane releases its shell; switching tabs preserves it. Stopped and exited sessions keep their history available until restarted or closed.

Tabs read icons directly from the installed shell executable, avoiding generic Explorer thumbnails. Store-installed PowerShell launch aliases resolve to their executable before icon extraction. Pill headers retain internal padding while their outer left edge aligns with the pane gutter. Mixed-shell tabs follow the first remaining pane for their icon and title order. Pane headers show the shell, current directory, and per-pane Git context alongside split and close controls.

Child shells receive `TERM=xterm-256color`, `COLORTERM=truecolor`, and `TERM_PROGRAM=AgentTerm`. The app removes inherited `NO_COLOR` from the child environment so launching it from automation does not suppress terminal colors. Other inherited variables remain available and the parent environment is unchanged.

The renderer supports ANSI, indexed, and RGB colors, Unicode cells, text styles, cursor shapes, and alternate screens. History, search, selection, and keyboard encoding use Ghostty's native APIs. Selection pins the viewport while output continues. Typing or pasting returns to live output.

Terminal drawing reuses dirty cells, clipped glyph layouts, and a bitmap of painted content. Box-drawing (U+2500 to U+257F) and block element (U+2580 to U+259F) characters are drawn as pixel-snapped shapes in the foreground color instead of font glyphs, so borders join across cells in every font and at every display scale. Idle panes stop requesting frames, and hidden panes release cached GPU content. The [native performance fixture](tests/AgentTerm.Renderer.Perf/README.md) checks simulated ConPTY output, final pixels, scheduling and GC pauses.

Terminal colors default to Auto, following the Windows light or dark appearance. Settings offers independent light and dark theme choices and Auto, Light, or Dark appearance, shared or per profile. Saving recolors open terminals without restarting shells. Switching themes preserves output, history, selection, and application-defined RGB or OSC colors.

Colored cell backgrounds and selection join without grid seams at fractional cell widths or display scaling. Text retains normal antialiasing.

An avatar button at the right end of the title bar opens a GitHub dashboard with your contribution heatmap, Copilot usage, activity, pull requests, review requests, and repositories. It reads from the GitHub CLI you already signed in to. See [GitHub dashboard](#github-dashboard).

## Controls

| Action | Input |
| --- | --- |
| New default-profile tab | Plus button or Ctrl+Shift+T |
| New profile tab | Profile dropdown |
| Next / previous tab | Ctrl+Tab / Ctrl+Shift+Tab |
| Move focus to the tabs | Ctrl+Shift+E; Escape returns to the terminal |
| Close tab | Close button, middle-click, or Ctrl+F4; the left sidebar also has a context menu |
| Reorder tabs | Drag a tab |
| Close active pane or tab | Ctrl+Shift+W |
| Split active pane | Ctrl+Shift+D, Alt+Shift++ or Alt+Shift+- |
| Focus adjacent pane | Alt+arrow |
| Resize panes | Drag a divider or Alt+Shift+arrow |
| Scroll history | Wheel, scrollbar, Ctrl+Shift+Up/Down or PageUp/PageDown |
| Oldest / latest output | Ctrl+Shift+Home / End |
| Select text | Left drag; double-click word; triple-click line |
| Extend / rectangular selection | Shift+click / Alt+drag |
| Copy selection and dismiss | Ctrl+C, Ctrl+Shift+C, Ctrl+Insert, Enter, or right-click |
| Interrupt without a selection | Ctrl+C |
| Paste | Ctrl+V, Ctrl+Shift+V, Shift+Insert, or right-click without a selection |
| Clear selection | Escape or single click |
| Search history | Ctrl+Shift+F; Enter next; Shift+Enter previous |
| Zoom | Ctrl+wheel, Ctrl++ / Ctrl+-; Ctrl+0 resets |
| Settings | Gear beside the GitHub avatar or Ctrl+, |
| Open GitHub dashboard | Avatar button in the title bar |
| Refresh GitHub dashboard | Refresh button or F5 |
| Switch dashboard section | Left / Right (wraps); Home / End for first / last |
| Inspect a heatmap day | Left / Right moves a week; Up / Down moves a day; Home / End jump to the first / latest day |
| Close dashboard | Escape, click outside, or click the avatar again |

Drag beyond the top or bottom to scroll while selecting. Copy joins soft-wrapped text and preserves explicit line breaks. Paste honors bracketed paste mode and sanitizes terminal control characters. Clipboard errors do not stop the shell. Screen switches clear selection. Search wraps; case-insensitive search currently folds ASCII letters only.

Mouse-enabled terminal applications receive clicks, releases, motion, and wheel events through Ghostty's native encoder. Hold **Shift** to use local selection, copy/paste, and scrollback instead. **Ctrl+wheel** still zooms. Passive mouse movement does not jump the viewport to the latest output.

IME input uses a native WinUI text input host positioned at the terminal cursor. Composition text stays local until committed, and terminal shortcuts defer to the input method during composition. Automated checks cover text routing, composition state, and cursor suppression. Live ordinary text, Unicode, physical key input, and mouse selection pass. Real composition and candidate placement still require verification with an installed IME.

## Appearance and profiles

Settings opens in its own window from the gear immediately left of the GitHub avatar or Ctrl+,. The terminal stays usable while settings is open. **Startup** selects the default shell and **Terminal start position**, Bottom or Top. Bottom remains the default. Saving the start position updates all open panes without restarting their shells and applies to new and restarted sessions. Profile navigation uses each installed shell's executable icon, matching the tabs. **Tabs** places tabs on the top or the left. **Profiles > Defaults** sets the shared color scheme, light and dark themes, font family, font size, pane background, and acrylic opacity. Each discovered shell has its own **Appearance** page under Profiles. Changes stay in a draft across pages until **Save**. **Discard changes** restores saved values; closing the window discards unsaved edits.

Each profile inherits Defaults unless a setting is overridden. Choose **Use default** to restore inheritance for a field, or reset the profile to remove all its overrides. An explicit **Automatic** font uses the app's installed-font selection independently of the font chosen in Defaults. Saving updates existing panes for that profile and applies to future tabs and splits. Overrides for temporarily unavailable profiles remain saved.

**Color scheme** selects **Auto**, **Light**, or **Dark**. Auto follows Windows appearance. **Light theme** and **Dark theme** independently choose the palette used in each mode, like Rex. Both slots remain saved when the mode changes. Each profile can inherit or override the scheme and each slot independently. Agent, Midnight, Ocean, Forest, Paper, and Flexoki each have light and dark variants. Catppuccin uses Latte for light and Mocha for dark. The [branded palette sources and licenses](docs/theme-sources.md) document their exact upstream colors. Pane controls follow the active appearance; window chrome continues to follow Windows. Save recolors existing output and retained scrollback without restarting shells. Each palette defines sRGB background, text, cursor, selection, and 16 ANSI colors. Extended indices 16 to 255 retain the standard xterm cube and grayscale ramp. Truecolor is not quantized. Program-set OSC color overrides remain until the program resets them, then reveal the latest theme defaults. Existing single-theme settings migrate to a forced appearance with their original palette. Theme import, custom palettes, Display P3, and contrast correction are not included.

Settings save the default profile, tab position, shared appearance defaults, and profile overrides to `%LOCALAPPDATA%\AgentTerm\settings.json`. The window uses Windows Mica with native caption controls and rounded tabs. Each terminal sits in a rounded card with quiet header actions, 4-DIP outer spacing and gutters, and a subtle native shadow. Every pane has a faint theme-aware boundary, light in dark mode and dark in light mode. Split panes add a restrained active-pane outline. Pane actions brighten on hover or keyboard focus. Settings uses the same rounded surfaces. Choose **Solid** for an opaque terminal or **Acrylic** for a separate desktop acrylic backdrop inside the pane. The main window and Settings keep Mica. The **Background opacity** slider adjusts acrylic tint using the terminal's actual background color, independently for each profile or inherited from Defaults. Tint and luminosity follow the automatic acrylic color calculation used by Windows Terminal's UWP brush. The current pane effect omits its 2% grain texture; equal percentage values are not yet verified to produce identical pixels. At 100%, the pane switches to an opaque surface. The slider is disabled for Solid. Text, selection, and differently colored ANSI backgrounds remain opaque. Explicit ANSI backgrounds equal to the default background currently share its transparency behavior.

Automatic font selection prefers an installed CaskaydiaCove Nerd Font, then Cascadia Mono, then Consolas. Settings lists available terminal fonts. No fonts are downloaded or installed. Acrylic panes use `SystemBackdropElement`; at low opacity they reveal a blurred desktop backdrop, not a clear view through the window. Windows controls material fallback when transparency is unavailable or the window is inactive. Mica is an opaque wallpaper-based material, so the window chrome itself does not reveal other windows.

New panes fade in over 200 ms. Closing panes fade out over 140 ms before removal, followed by a 190 ms expansion of the remaining panes. These transitions animate visual transforms without resizing ConPTY every animation frame. Divider dragging moves panes immediately. Turning off Windows animations disables these transitions.

Zoom applies to the current pane; Ctrl+0 restores that profile's font size. A saved font-size change updates panes whose resolved profile size changed. Saving another profile's settings preserves unrelated pane zoom. History is configured for 10 MiB and 10,000 physical lines; Ghostty retains whole pages, so these limits are approximate. WSL discovery is bounded and skips unavailable distributions. WSL itself is not installed by the app.

This is a base terminal experience, not complete Windows Terminal compatibility. Live IME compatibility validation, a terminal UI Automation text provider, advanced profile editing, and custom themes/key bindings remain open.

### Left tabs

Choose **Settings > Tabs > Tab position > Left**, then **Save**, to show tabs in a sidebar. Open tabs, their shells, split panes, and the selected tab stay as they are. The change applies to the open window immediately and persists for future launches. Choose **Top** to return to the original tab strip. Settings without a saved position, or with an unknown value, keep tabs on top.

Both tab layouts show the app icon and **AgentTerm** at the top left of the title bar, rather than repeating the selected shell name. With left tabs, the new-tab button and profile dropdown sit above the first sidebar tab. Settings has its own gear immediately left of the GitHub avatar, outside the profile dropdown.

The sidebar uses the top strip's pill shape and native theme resources for selection, hover, and pressed colors. The selected tab is bold and carries the accent selection indicator used by Windows navigation lists. Standard themes also fill and outline it. In high contrast the indicator uses the system highlight color, and the row outline is reserved for keyboard focus. Long titles end with an ellipsis; hovering a shortened title shows it in full. The title bar keeps the app branding, window drag area, Settings gear, GitHub avatar, and caption buttons. The new-tab button above the list lines up with the sidebar icons. When the window is narrower than 680 DIPs, the sidebar collapses to shell icons. Each icon's tooltip shows the tab title and names the right-click and Shift+F10 tab actions and Ctrl+F4 close shortcut, while the title bar keeps the app name. Many tabs scroll inside the sidebar between the pane card's top and bottom edges. The sidebar scrolls to the selected tab when the selection changes, when Ctrl+Shift+E focuses it, and when Left is applied. Title changes, closing other tabs, and reordering leave the scroll position alone. UI Automation reports the sidebar as a tab control with tab items, the same roles as the top strip.

Ctrl+Shift+E is reserved by the window in both tab positions and is not sent to terminal programs. In the sidebar, it focuses the selected tab. Up and Down move focus without switching. Enter or Space opens the focused tab and returns focus to its terminal. Ctrl+F4 closes the focused tab. Shift+F10 or a right-click opens a close action labeled with the target tab title. Duplicate titles also include the tab position. After a keyboard close, focus returns to the selected tab in the sidebar. Escape returns to the terminal.

## Pane directory and Git status

Each pane's existing top row groups its shell, last shell-reported filesystem directory, and Git status on the left. Split and close controls stay on the right. Windows home paths shorten to `~`. Git repositories add the branch, local upstream ahead/behind counts, tracked-line additions and deletions, and indicators for staged (`●`), unstaged (`±`), untracked (`?`), and conflicted (`!`) changes. Narrow panes hide counts first, then the branch. Hover the header for the full path and Git summary. No bottom row is added.

In narrow panes, split and close controls fold into a **Pane actions** menu. The agent task and **Type into command** buttons shrink to named icons. Shell integration status becomes an information icon with its full explanation in its tooltip and accessible name.

Line totals cover the whole containing worktree and compare working content with HEAD, or the empty tree before the first commit. Staged and unstaged edits are not added together. Untracked files do not contribute line totals. Binary changes have no numeric totals. Conflicts suppress aggregate line counts. Detached HEAD shows a short commit; an unborn branch shows `(new)`. Nested repositories select their own branch.

Recognized direct interactive PowerShell launches install a session-only prompt wrapper after loading your usual profile. It preserves the prompt output and reports directory changes without editing profile files or execution policy. Command Prompt uses a session-only path prompt with directory reporting. Command-payload launches remain unchanged. Existing raw or quoted Windows reports from prompt tools such as Oh My Posh are accepted too. Non-filesystem PowerShell locations clear the directory and Git content.

Git 2.45 or newer must be on `PATH`. Queries run asynchronously at most once every five seconds per visible, running pane. Hidden tabs and stopped panes do not poll. Directory changes cancel obsolete queries and clear old repository content immediately. Queries share a five-second deadline and bounded output. They do not fetch or change repositories, invoke external diff/text-conversion helpers, bypass repository ownership checks, or refresh the index on disk. Missing or older Git and failed queries leave Git content empty; the header tooltip names unavailable or failed queries.

WSL launch commands are unchanged. A WSL shell must already emit OSC 7 file URLs for directory reporting; automatic Bash/Zsh hook installation is not included. Accepted WSL reports retain the named distribution and Git runs inside it, with a Linux-side timeout. Remote-host URLs and unsupported reports clear context rather than querying Windows Git against a remote path. WSL execution has not been verified live.

## GitHub dashboard

The avatar button sits at the right end of the title bar, beside the caption buttons. Clicking it opens a flyout over the title bar, with your name, login, and contribution total beside the avatar. Clicking the avatar again closes it. The flyout shows a contribution heatmap for the last 12 months, Copilot usage, and four lists: **Activity**, **My PRs**, **Reviews**, and **Repos**. Each list shows at most 30 entries. Pull request cards show state, review decision, branch, labels, comment count, and a checks pill that lists up to 100 checks. Clicking a card or title opens it in your default browser, only for `https://github.com/` URLs. The lists cover your whole account, not the repository of the active terminal.

**Requirements.** Install the GitHub CLI so `gh.exe` is on `PATH`, and sign in with `gh auth login --hostname github.com`. Only github.com is supported. The app runs `gh api --hostname github.com` and never reads or stores a token. If `gh` is missing, the flyout shows a "GitHub CLI not found" panel with an install command. Restart AgentTerm after installing so it sees the new `PATH`. If `gh` has no active account or its token is rejected, the flyout shows a "Sign in to GitHub CLI" panel with a copyable command and a **Retry** button. The app never signs in for you. Other failures, such as no network, show a banner with **Retry** and keep earlier data. A section that fails alone keeps its earlier data marked "Stale data."

**Refresh.** The flyout refreshes when it opens if no refresh has succeeded this session or the last one is 5 minutes old or older. It then refreshes every 5 minutes while it stays open. The refresh button and F5 refresh on demand. Automatic refreshes reuse sections that are still fresh (5 minutes for lists and Copilot usage, 15 minutes for the heatmap and repos). Manual refresh reloads everything. Each request times out after 45 seconds. While the flyout is closed nothing refreshes. At startup the app reads its cache and makes one `gh api user` call to show your avatar.

**What leaves the machine.** `gh` sends read-only queries to github.com for your profile, contribution calendar, pull requests, review requests, events, repositories, and Copilot usage. The app itself requests avatar images from `avatars.githubusercontent.com` and rejects any other image host. A failed avatar image leaves your initials in place.

**What is stored.** A snapshot is cached at `%LOCALAPPDATA%\AgentTerm\github-dashboard.json` so the flyout paints at once. It holds your account id, login, name, avatar URL, and the last fetched lists, heatmap, and Copilot usage. That includes pull request titles and repository names, which can be private. It holds no credentials. Each section expires after 7 days, and an unreadable cache is ignored. Delete the file to clear it. Cached data shows as "Saved" until a refresh confirms the account.

**Limits and known gaps.**

- Copilot usage comes from the undocumented `copilot_internal/user` endpoint and may stop working. When it fails, the other sections still load.
- The button hides when the window is too narrow to fit it beside the caption buttons. There is no setting to hide it otherwise.
- With Windows animations off, the flyout entrance, heatmap reveal, tab slide, and loading shimmer are skipped, and the refresh icon dims instead of spinning.
- Automated checks cover parsing, caching, refresh rules, and text and layout rules. They do not exercise the flyout. High contrast, a failed avatar image, and the section-level "Stale data." line have not been verified live.

## Build and run

Use Windows x64, Windows 10 version 2004 or newer, a .NET 10 SDK, and PowerShell. Windows SDK/XAML tools restore through NuGet. Internet access is needed for the first build.

```powershell
./scripts/check.ps1
dotnet run --project src/AgentTerm.App -p:Platform=x64 -c Release
```

The check script downloads checksum-pinned Ghostty and Zig archives, builds the native DLL and app, and runs shell-icon extraction and WinUI decoding checks, native VT/input/history/search tests, GitHub dashboard parsing, cache, refresh, and presentation checks, real shell integration tests, and Win2D pixel checks. The GitHub checks run inside `AgentTerm.Terminal.Smoke` with fake transports and no network. Images are saved to `artifacts/renderer-smoke.png` and `artifacts/selection-smoke.png`. Generated files stay in ignored `.deps`, `artifacts`, `bin`, and `obj` folders. First native builds take several minutes.

The app is unpackaged and self-contained. Windows App SDK 2.6.5 is pinned. Packaging, signing, and additional architectures are future work.

## Code map

- `src/AgentTerm.App/MainWindow` owns the tab strip, profile menu, window backdrop, settings window lifetime, and window shutdown. `MainWindow.TabSidebar.cs` applies the saved tab position and mirrors the TabView into the left sidebar. The TabView remains the only owner of tab order, selection, and terminal content. `SidebarTabItem.cs` renders one sidebar row, and `TabSidebarLayout.cs` holds the compact-width rule and the reorder that never detaches the selected tab.
- `src/AgentTerm.App/SettingsWindow` owns settings navigation, draft edits, validation, and save/discard actions in a separate window.
- `src/AgentTerm.App/TerminalTab.cs` owns nested pane layout, resizing, transitions, and active-pane focus.
- `src/AgentTerm.App/TerminalPane` owns each card's material, directory/Git header, controls, and focus outline.
- `src/AgentTerm.App/PaneStatus/` owns Git query execution, parsing, summaries, and one-query-per-pane scheduling. `ShellDirectory.cs` and `ShellIntegration.cs` in the terminal project own reported filesystem context and session-only Windows prompt integration.
- `src/AgentTerm.App/MainPage` owns one terminal view, input, clipboard, search, and session presentation. A `TrailingThrottle` limits grid sizes sent to the session during window resizes, divider drags and pane layout changes to one per 25 ms, so ConPTY resizes and reflows do not delay program output. The last size always reaches the session. Font and zoom changes, session starts and tab switches send their size at once.
- `src/AgentTerm.App/TerminalSurface.cs` draws cells, selection, and cursors with shared font metrics.
- `src/AgentTerm.App/MainPage.Composer.cs` applies the saved terminal start position and owns native `/agent` intake, the agent surface and its reply field, and task card placement. `TerminalSurface.cs` owns the single cell mapping, `TerminalGridGeometry`, used for drawing, IME, hit testing, mouse and cards. `AgentTimelineView.cs` renders one task's conversation, `AgentTaskCard.cs` renders a card in history, and `AgentStyles.xaml` holds their theme-aware styles.
- `src/AgentTerm.Terminal/GhosttyTerminal.Anchors.cs` owns history anchors: tracked rows that follow output, scrolling and reflow, and expire on trim or clear.
- `src/AgentTerm.Terminal/TerminalSession.cs` serializes Ghostty state and owns one shell connection.
- `src/AgentTerm.Terminal/ConPtyConnection.cs` owns process creation, pipes, output draining, resize, and cleanup.
- `src/AgentTerm.Terminal/GhosttyTerminal.*` owns the pinned native ABI and terminal, render, selection, search, and input handles.
- `src/AgentTerm.Terminal/ShellProfile.cs` discovers installed shell profiles.
- `src/AgentTerm.App/GitHubDashboardButton` owns the title-bar avatar button, its flyout, flyout sizing, and returning focus to the terminal after pointer use.
- `src/AgentTerm.App/MainWindow.GitHub.cs` sizes the spacer that keeps the title-bar content clear of the caption buttons, and hides the avatar button when it would overlap them.
- `src/AgentTerm.App/GitHubDashboardView` owns the snapshot, refresh timers, cache use, sign-in and missing-`gh` panels, and the tab and F5 keys. `GitHubCards.cs` builds the list cards.
- `src/AgentTerm.App/GitHubHeatmap.cs` draws the contribution calendar and handles its keyboard navigation. `GitHubAvatar.cs` shows initials until a picture decodes. `GitHubMotion.cs` owns animations and turns them off with Windows animations.
- `src/AgentTerm.App/GitHub/` holds the WinUI-free logic. `GitHubCli.cs` runs `gh`, `GitHubData.cs` owns queries, parsing, and section refresh, `GitHubCache.cs` owns the on-disk snapshot, `GitHubJson.cs` parses strict JSON, `GitHubPresentation.cs` and `GitHubStrings.cs` own display text and layout rules, and `GitHubModel.cs` owns the shared types.
- `tests` contains real native, shell, and rendered-pixel checks.
- `tests/AgentTerm.Terminal.Smoke/GitHubChecks.cs` checks the GitHub dashboard logic with fake transports and no network.

`StopAsync` releases ConPTY while retaining Ghostty state for inspection. `DisposeAsync` also releases that terminal state. Hidden tabs pause drawing, not their shell output reader. Native Ghostty owns scrollback and tracked selection references; frame snapshots own their cell arrays and consumers must not mutate them.

## CI and verification

Pull requests run the shared pre-commit checks and GitHub Actions workflow lint on `boat-small`, without restoring .NET dependencies or compiling the app or native code. Formatting hooks leave third-party license and notice files unchanged. Pushes to `release` or `release/**` run lint first, then the full Linux and Windows builds and smoke tests. Pushes to other branches and tags do not start CI. New commits cancel older runs for the same pull request or release branch. Jobs have time limits, read-only repository permissions, and pinned action versions.

`scripts/check.ps1` validates Windows. `bash scripts/check-linux.sh` validates the portable wrapper on Linux x64 with .NET 10, Python 3, curl, tar, and xz. Release builds use Boat for portable native checks and `windows-latest` for Windows builds and integration. Hosted CI requires the Boat GitHub App; local Boat runs are separate validation.

### Local commit hooks

Install once per clone with Python 3.11+ and Git Bash on Windows:

```powershell
python -m venv .venv
./.venv/Scripts/python.exe -m pip install -r requirements-dev.txt
./.venv/Scripts/python.exe -m pre_commit install --install-hooks
```

On Linux or macOS, use `python3` to create the environment and `.venv/bin/python` for the remaining commands. Bash must be on `PATH`. First-time setup downloads the pinned hook dependencies.

Commits check staged files only. Hooks validate JSON, YAML, XML (including XAML and MSBuild files), Bash syntax, merge markers, Windows filename compatibility, and a 1 MiB file-size limit. They fix trailing whitespace and final newlines while preserving Markdown hard line breaks and third-party license/notice files. If a hook fixes a file, review and stage that fix, then commit again. Local hooks never build code or download native dependencies. No pre-push hook is installed. Workflow-specific lint runs separately in CI and needs no local Docker installation.

Run all checks manually on Windows:

```powershell
./.venv/Scripts/python.exe -m pre_commit run --all-files --show-diff-on-failure
```

CI runs this same configuration over all tracked files. Local hook environments are reused between commits.

## References and licenses

- [libghostty-vt](https://lib.ghostty.org/vt/).
- [Windows Terminal actions](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/actions).
- [Windows Terminal selection](https://learn.microsoft.com/en-us/windows/terminal/selection).
- [Windows Terminal profile appearance and inherited defaults](https://learn.microsoft.com/en-us/windows/terminal/customize-settings/profile-appearance).
- [Boat CI](https://docs.boat.dev/ci).

Ghostty is MIT licensed. Keep its license and bundled dependency notices with redistributed native binaries. Generated Ghostty binaries are not committed.

The application uses the unmodified Windows Terminal stable icon by Microsoft. Its CC BY-ND 4.0 license and source attribution ship in `Assets/WindowsTerminal.LICENSE.txt` and `Assets/WindowsTerminal.NOTICE.txt`.

The acrylic tint and luminosity calculation is adapted from Microsoft's MIT-licensed WinUI source. Its attribution and license ship in `Assets/AcrylicRecipe.LICENSE.txt`.

## Terminal prompt and agent

Every pane is a real terminal. You type commands at your shell's own prompt at either start position, so Tab completion, history, Ctrl+R, predictions, custom PSReadLine bindings and multiline editing are the shell's own. There is no separate command box. IME composition follows the terminal cursor; live IME verification remains outstanding.

Choose **Settings > Startup > Terminal start position** and **Save**. **Bottom**, the default, starts the prompt and cursor on the bottom row. Commands and output stack upward above the next prompt, oldest at the top. When history fills the pane it looks like a conventional terminal. After a clear that keeps scrollback, earlier history fills the space above the prompt instead of leaving a gap. **Top** draws the same terminal from the top, like a conventional terminal. Saving applies to all panes immediately, including during commands or agent work, without writing to the shell, resizing it, or changing input focus. The choice survives app restart. Missing or unknown stored values use Bottom. There is no Smart/Legacy header toggle or reserved F6 shortcut. Tab stays with the shell. The terminal body shows no hover tooltip, so nothing covers the pane header. Selection, copy and paste keys remain in the terminal's accessible help text and the shortcut table above.

The prompt is terminal content, not a pinned editor. Scrolling up moves it out of view with the rest of the output, and typing returns to the live edge. Warp keeps its input box pinned while you scroll; AgentTerm deliberately does not, because only the shell's own line editor can keep your completions and bindings. When rows below the prompt are erased, for example when a completion menu closes, the content moves down so the prompt returns to the bottom row.

**Asking the agent.** Type `/agent <request>` at the shell prompt and press Enter. The line goes into the shell's history like any other, so Up recalls it. `/agent` alone opens an empty task. Only a leading, lowercase `/agent` token starts a task. `/agentx`, a leading space and `/Agent` stay shell commands. No classifier runs, so ordinary commands never wait on routing. If a turn is already running, `/agent` reopens that task and places your request in its reply field instead of losing it.

`/agent` entry needs integrated PowerShell with PSReadLine. When it is not available the pane header says **/agent unavailable** and its tooltip gives the reason. In Command Prompt, WSL, constrained PowerShell and unsupported startup arguments, the shell has no AgentTerm integration. In PowerShell, AgentTerm does not override an Enter key binding your profile installs, so `/agent` lines run as ordinary commands there. The terminal works normally in every case.

**Agent surface.** A task opens a conversation surface in place of the terminal view. The shell keeps running underneath at the same size. The surface shows the request, the streamed answer, tool calls and outcomes. One call shows as a card with its status and command. Several calls collapse behind a summary such as "Ran 3 commands and read 1 file". A failed call keeps a red mark on that summary and adds an outcome row, so a failure is never hidden by collapsing. Expanding a call shows the full command, up to 12 lines of output with **Show all**, and **Copy output**. The right-click menu copies the command or output. "Thinking" appears only when the harness sends a reasoning summary; otherwise the label is "Working". Activity text uses a moving highlight that stops with Windows animation effects off or high contrast on. Approvals and questions appear directly above the reply field. The approval card is the one place that names its keys: Enter on an empty reply field runs the pending command once, and Ctrl+C rejects it. While an approval waits, **Stop** still ends the turn but drops its Ctrl+C keycap, because Ctrl+C rejects instead. Otherwise, while a turn runs, Ctrl+C or **Stop** interrupts it. A call's row says **pending** until the host reports execution, **awaiting approval** while an approval for that exact call waits, and **running** only after an approved terminal command has started. Escape, Backspace on an empty reply field, or **Esc for terminal** returns to the terminal and does not stop the agent. Follow-ups continue the same task. During a running turn the reply field reads "Agent replies paused until this turn finishes", with Send and its input glyph hidden. It keeps keyboard focus for Enter, Ctrl+C and Escape. Free-text questions remain answerable. A 20-DIP progress ring accompanies quiet work, with a static clock when motion is disabled or high contrast is enabled. The indicator responds to preference changes during quiet turns without waiting for another agent event. Longer turns show elapsed time. Settled turns and history cards show Done, Stopped, or Failed, and replies carry an agent icon. Empty assistant messages that harnesses send around tool calls get no row. Stopping keeps any partial reply and states whether a reply existed.

The reply field exists only inside the agent surface. It is the one place where the app, not the shell, takes your typing.

In narrow panes, approval actions move below the title and stack when needed. Reject and Run remain available at the minimum pane width. If a native `/agent` request cannot start because the shell has no filesystem working directory, its text remains in the reply field for retry.

**Task cards.** Starting a task reserves two blank rows at the shell prompt. The task's collapsed card sits on those rows and scrolls with terminal history. Click the card or press Ctrl+Shift+Y in the terminal to reopen the conversation. The latest task's card and the header status button name Ctrl+Shift+Y in their tooltips. Ctrl+Shift+Y reaches the shell as usual when the pane has no task. The card shows Working, Needs approval, Needs an answer, Done, Stopped, or Failed. The pane header shows background agent status while the surface is closed. A card shows only while its rows are blank. If shell text occupies them, the card stays hidden rather than cover that text, and the task stays reachable with Ctrl+Shift+Y. A card disappears when history trimming or a screen clear removes its rows. Cards hide while a full-screen program runs and never cover the live prompt row. Agent history does not interleave with terminal output inside a card; the conversation lives on the surface.

While an agent command runs, it owns the terminal. Your keystrokes do not reach it unless you choose **Type into command** in the pane header, which hands raw input to that command, for example to answer Read-Host. Agent replies stay paused until the turn finishes. The button stays visible after you choose it; the app does not show that input was already handed over. When the command finishes the shell is back at its own prompt, with any half-typed line you left there restored. Commands run in the existing shell, so variables, cwd, functions and environments persist. Your profile and its prompt stay loaded.

Agent mode uses GitHub Copilot SDK **1.0.31** and its checksum-verified bundled runtime **1.0.109**. The installed VS Code launcher is not used because its observed runtime is 1.0.106-8. Build acquisition is project-local. Sign in separately with Copilot CLI. Dashboard GitHub authentication is not proof of Copilot authentication. No tokens are stored by Agent Term.

Agent commands run in the invoking pane's existing integrated PowerShell through the shared `run_in_terminal` host tool. Shell variables, aliases, cwd, environment and activated venvs persist. The approval shows the exact command. While the agent owns the terminal, competing submissions and raw keys are blocked. **Type into command** deliberately hands raw input to the owned command. **Stop** sends Ctrl+C and waits for readiness, without killing PowerShell. Visible `Command started [a:id]` and `Command ended [a:id]` lines delimit captured output because inbox ConPTY can reorder OSC markers relative to text. They mark shell command boundaries, not the agent turn's outcome; the task card and surface report Done, Stopped or Failed. The short command id stays visible in scrollback. Results contain at most 64,000 characters, with status, cwd and truncation metadata. ANSI and OSC are stripped; cursor redraws are not reconstructed and this is not a raw stdout/stderr pipe. A window resize during an agent command makes ConPTY repaint the screen. Capture keeps only text after the latest replayed start line, so an on-screen replay is removed, but blank repainted rows remain. Once that start line has scrolled off screen, a resize replay still duplicates visible tail lines. No app-cwd fallback exists. WSL agent execution is not supported. Each pane owns one harness session and a bounded structured timeline. Up to 32 tasks are kept per pane. Tasks in a pane share that harness session, so the agent may remember earlier tasks. Permission prompts offer Run once or Reject. Questions accept choices or permitted free text. Interruption retains terminal ownership until shell readiness; a 20-second timeout reports failure and leaves ownership held. Closing the pane disposes its session. Harness-managed policy remains enabled. The SDK `AvailableTools` allow-list contains only `run_in_terminal` and the SDK-documented `ask_user` human-input tool, so independent shell, file and subagent tools are unavailable. File operations must also use this approved tool. This avoids stale native file-tool cwd after a terminal `cd`. The conversation binding changes only on the next submission; the tool result cwd is authoritative within a turn.

`AgentTerm.Agents` owns the WinUI-free contract, routing and shared lifecycle. `AgentTerm.Copilot` alone owns SDK payloads and runtime translation. Codex and Pi are future adapters, not implementations in this release.

Run deterministic behavior checks with `dotnet run --project tests/AgentTerm.Agents.Smoke -c Release`. Windows `scripts/check.ps1` includes these and real PowerShell checks. An optional authenticated smoke turn sets a unique shell-only variable/alias/cwd, verifies the actual custom tool callback and unchanged PID, then interrupts an owned command and checks shell recovery. It approves only two exact benign fixture commands and denies all others. Run `dotnet run --project tests/AgentTerm.Copilot.Smoke -c Release`. This is not part of offline CI.

Agent activity uses compact inline rows. Consecutive calls share a collapsed summary such as **Ran 2 commands**. An in-flight row names the latest operation: **Running** after reported execution, **Pending** before it, and **Awaiting approval for** while that call's approval waits. Click or keyboard-toggle the row to inspect commands. Expand **Output** separately to select and copy bounded output. Expansion survives streaming and pane switches. Failures and pending approvals remain visible outside collapsed groups. Counts come from typed tool categories, not guesses about shell syntax. The current adapter counts terminal commands only.

A moving text highlight marks current activity. Windows animation preferences and high contrast disable it. Inactive panes and completed, interrupted, or waiting turns stop motion. **Working** is the fallback while no tool is active. **Thinking** requires a harness-provided summary. Copilot reasoning payloads are not exposed. Tool protocol envelopes and schemas are not displayed. Retention is bounded to 128 timeline entries, 256 calls, 64 calls per group, and 8,192 characters per message or output. Earlier activity is marked when trimmed. Scrolling older activity disables automatic following.

## Program status in tabs

Top tabs and Left sidebar tabs show status reported by terminal programs through
[OSC 7501](https://www.superlogical.com/rex/docs/build/program-status.md).
Hover a tab in either layout, including compact sidebar rows, to see a live status
card with a native acrylic background. Windows high contrast and transparency
preferences provide the solid fallback. Move into the card to read, scroll, select, and copy text. It stays open
while you use it and closes when you leave. Escape dismisses it without moving
terminal focus. **Keep open**, or **Alt+Down** on a focused tab, opens the same
card for keyboard use. Home and End reach the first and last reports. Status opens
automatically, including when a report arrives while the pointer is already over
the tab. There is no **Program status** context-menu action.

The card groups split panes and nested tasks, labels states and progress, and
shows messages and report ages such as **just now**, **4m ago**, or **3h ago**.
Ages refresh every 30 seconds while the card is open. Tab tooltips and screen-reader
details omit update times so they do not become stale. Each row leads with the agent's mark or a state
symbol. A working session or subagent row shows the tab's dot sweep in place of
that symbol. Known agents appear by product name, such as Claude Code for
`claude-code`. Nested tasks do not repeat their parent's app. Split-pane headings
use the pane's program title and add the shell name when the title replaced it.
Urgent panes appear first. Every report remains
reachable by scrolling. Live updates preserve the reading position. Reopening
starts at the top. Clearing all reports restores the shell icon. Returning to a
shell prompt or exiting the shell also restores its icon, while retained reports
remain available in the card.

Working tabs replace the program icon with a compact 5-by-5 dot sweep
animation. A fine comet travels along the top edge of each visible working pane.
This applies to reported terminal-program activity and built-in agent turns, not
process-name or terminal-output guesses. Waiting, failed, completed, and idle
work stops the corresponding animation. The pane header keeps its task action
but does not repeat an animated **Agent working** label. Programs with a matching
local PATH executable retain its icon when idle or done.
Coding agents use built-in marks because script-based CLI installs need not
contain an executable icon. The reported `app` value selects the mark, ignoring
case. Pi (`pi`) uses π. Claude Code (`claude`, `claude-code`), Codex (`codex`),
Gemini CLI (`gemini`), GitHub Copilot CLI (`copilot`), Cursor Agent (`cursor`,
`cursor-agent`), OpenCode (`opencode`), Qwen Code (`qwen`, `qwen-code`), and
Cline (`cline`) use marks from Simple Icons (CC0). Claude, Gemini, and Qwen
use their brand colors. Black-and-white brands follow the tab theme. High
contrast uses the system foreground for every mark. Built-in marks take
priority over PATH executable icons. Other unresolved commands keep the shell
icon. No executable is
launched to obtain its icon. Blocked and error reports use distinct attention
symbols while their command is active. With Windows animations off or high
contrast on, working tabs keep the normal full-size app icon. They do not show
a static pixel grid or ring. The pane edge stays static. The status card and
accessible details retain the state and exact reported percentage. Background
tabs continue updating; hidden panes and offscreen or unloaded controls stop
animation.

No hooks, log scanning, or process-name guessing are used. Programs must emit the
protocol themselves. AgentTerm answers capability queries and supports all five
states and permission, question, and auth blocking reasons. Each pane owns its
records. New shell prompts and shell exit remove working and blocked reports;
idle, done, and error remain as inactive details until cleared, reset, or the
session is replaced. Retained reports do not override a running program in
another pane. Foreground
process owner metadata and individual child-process exit detection are not
implemented. Native PowerShell prompt markers and OSC 133 prompt markers both
trigger cleanup.

Windows builds bundle Microsoft's `Microsoft.Windows.Console.ConPTY` package.
The older system ConPTY dropped OSC capability replies in the integration test;
the bundled host passes the reply before the following device-attributes reply.
This is app-local and does not update Windows. Windows executables consuming the
terminal library must build with a Windows RID (the app and ConPTY smoke harness
use `win-x64`) so the DLL and its console host resolve from the shipping layout.

For a quick PowerShell check, run this command. The blocked report remains during
the pause; the done report survives the prompt that follows.

```powershell
[Console]::Write("`e]7501;state=blocked:kind=permission:app=demo:msg=QXBwcm92YWwgbmVlZGVk`a"); Start-Sleep 15; [Console]::Write("`e]7501;state=done:app=demo:msg=Q29tcGxldGU=`a")
```

Protocol regression checks live in `ProgramStatusChecks` in the terminal smoke
suite. They cover chunked input, detection, validation, replacement, inheritance,
clearing, reset, lifecycle, and record eviction.

Run native status and timeline checks with `dotnet run --project
tests/AgentTerm.AgentUi.Smoke -c Release -p:Platform=x64` on an unlocked Windows
desktop. Keep the smoke window in the foreground. Activity assertions sample
displayed pixels so they include compositor-owned motion.
The sampler rejects intersecting visible foreign window bounds above the smoke
window before and after each capture, including disabled and nonactivating
overlays. Same-process popups owned by the smoke window remain valid occlusion
fixtures. Run the overlay regression alone with `-- --capture-overlays`.
Bounds checks are conservative for shaped or transparent windows. They cannot
exclude overlays that appear and disappear entirely between scans, or effects
outside reported window bounds. Capture is not atomic with desktop composition.
