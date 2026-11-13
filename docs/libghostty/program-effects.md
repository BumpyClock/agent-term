# Program effects: clipboard, bell, notifications, progress, window resize

What a program running in a pane can ask AgentTerm to do, how the request travels, and where the user controls it.
Settings live in Settings, **Program requests**. They save to `settings.json` under `Effects` and apply to open panes when saved.

## For the user

| Request | Sequence | Default | Where it shows |
| --- | --- | --- | --- |
| Clipboard write | OSC 52, Kitty OSC 5522, iTerm2 OSC 1337 Copy | Allow | A short card in the pane says what was copied and how much. |
| Clipboard read | OSC 52 `?`, OSC 5522 read | **Block** | A card says a read was blocked, or served when allowed. |
| Bell | BEL | Flash the pane | Pane flash, optional Windows sound, taskbar button flash when the window is in the background. |
| Notification | OSC 9, OSC 777 `notify` | In AgentTerm and Windows | Card in the pane. A Windows notification too when AgentTerm is in the background. |
| Progress | OSC 9;4 | On | Strip across the pane top, a percentage or marker on the tab, and the taskbar button. |
| Window resize | CSI 8;rows;columns t | **Off** | Resizes the window by the difference in cells. |

Clipboard write choices are Allow, Ask first, and Block. Ask first holds the text and shows a card with **Copy** and **Don't copy**.
The program is told the write was denied, because its request cannot wait on you. Approving still copies the text. The size limit
(64 KiB to 8 MiB, default 1 MiB) applies to reads and writes. Pasting into a program is never affected by these settings.

Reads are off because they expose whatever you last copied. With reads on, every read leaves a visible card.

### Tab markers

A tab title ends with the pane's progress (`42%`, `working`, `paused`, `failed`) or a dot when a bell, notification, or clipboard
question arrived while you were not looking. Looking at the pane (focus, pointer, or selecting its tab in the active window) clears the dot.
The window title carries the same text, so the taskbar hover shows it.

### Taskbar progress with several panes

One taskbar button per window. An error outranks a pause, a pause outranks determinate progress, and determinate progress
outranks indeterminate. The most recent report wins inside a tier. Closing a pane, or the shell exiting, clears its progress.

## What the terminal does

`GhosttyTerminal.Effects` is a `TerminalEffects`. Native callbacks registered in `GhosttyTerminal.Effects.cs`
(`BELL`, `DESKTOP_NOTIFICATION`, `PROGRESS_REPORT`, `CLIPBOARD_WRITE`, `CLIPBOARD_READ`, `RESIZE_WINDOW`) copy their borrowed
payload, apply policy, queue an immutable `TerminalEffect`, and return. They never call UI code, never wait on another thread,
and never let an exception cross the ABI. A failing callback answers as a refusal and increments `Effects.Faults`.

`TerminalSession` drains the queue after it releases its terminal lock and raises `EffectsAvailable` on the reader thread.
The app subscribes in `MainPage.Effects.cs`, copies batches into a bounded `EffectInbox`, and drains it on the UI thread
with one dispatch at a time. Nothing runs while a pane is idle. The only timers are one-shot and exist while a card or bell flash shows.

### Bounds and coalescing

| Effect | Rule |
| --- | --- |
| Bell | Consecutive bells in one drain merge into one effect with a count, capped at 1000. The host rings at most once per 300 ms per pane. |
| Notification | Identical requests merge and count. At most 16 pending. Extra requests add to the last one's count. Windows notifications are limited to one per 1.5 s per pane. |
| Progress | Only the latest undelivered state is kept. Removing progress that is not showing queues nothing. |
| Clipboard notices | Merged by kind and program name. At most 8 pending. |
| Held writes (Ask) | At most 4. The oldest is dropped, so a spamming program cannot fill memory. |
| Resize | Latest request wins. |
| Text | Titles clip at 256 characters, bodies at 1024, program names at 64. Control and bidirectional override characters become spaces. |
| Native transaction | OSC 5522 writes are bounded at the 8 MiB maximum. The terminal's built-in default is 64 MiB. |

### Clipboard semantics

- Writes use the first `text/plain` representation, decoded as strict UTF-8. Other MIME types answer *unsupported*, invalid UTF-8 answers *invalid data*.
- Only the standard clipboard exists. Selection and primary locations answer *unsupported* because Windows has no equivalent.
- OSC 52 with an empty payload asks to clear the clipboard. A Kitty write with an explicit empty text entry is a no-op, as the API specifies.
- Reads answer an empty clipboard (OSC 52) or `EPERM` (OSC 5522) when blocked. Over the size limit, or while Windows holds the clipboard, they answer empty or an I/O error.
- A Kitty "list targets" read with no MIME list reveals nothing while reads are blocked.
- The terminal never remembers a Kitty password grant (`remember` is always false). Settings stay the single source of truth.
- Windows access (`WindowsTerminalClipboard`) uses the Win32 clipboard, never the UI thread. Reads open it ownerless with a short retry. Writes run on one dedicated message-pump thread that owns a message-only window, because only an owner window can set clipboard data. Each open gives up as *busy* rather than blocking the reader. A write that timed out is cancelled so it cannot land after the program was told *busy*.
- Tests inject `ITerminalClipboard`. No test touches the real clipboard.

### Kitty paste events

Installing the read callback lets a program enable mode 5522. `GhosttyTerminal.EncodeClipboardPaste` (used by `TerminalSession.Paste`)
then sends the program a paste event, and the program's follow-up read arrives with `granted` set. It is served from the text you
just pasted, valid for 30 seconds, even when general reads are blocked, because you chose to share it. The Kitty protocol only grants when the
read carries a `name`, which is why `name=` appears in the checks. Without mode 5522 a paste is the plain or bracketed paste as before.

### Reset and exit

A full reset (RIS) clears pending cards, held clipboard approvals, paste grants and visible progress at the native reset callback boundary.
A queued reset effect withdraws notices already displayed by the host. `TerminalEffects.Reset()` also runs when the shell exits
or the page releases its session.

## Host integration

- **Capability reports.** Host configuration reads `TerminalEffects.AdvertisesClipboardAccess` when answering DA1. Preference changes preserve image storage. Effects callbacks are registered once by the terminal constructor; size reports belong to the host geometry adapter.
- **Windows notifications.** `WindowsEffectHost` registers with `AppNotificationManager` on the first notification, tags each toast, withdraws them on pane close and at process exit, and sets them to expire after ten minutes and on reboot. Clicking one selects the tab and pane and brings the window forward (`TerminalTab.RevealRequested`, `MainWindow.AddTab`). If registration fails or the system does not support it, the in-app card is the notification. OS toast activation remains outside the automated UI fixture.
- **Window resize.** Honored by `MainPage.ResizeWindowFor` only when the option is on, the window is in its restored state, and the pane is visible. A split pane resizes the window by the cell delta of that pane, which is approximate.

## Tests

- `EffectsChecks` (Terminal.Smoke, `--effects-only`): native callbacks against a fake clipboard. Policy transitions, size limits, order, coalescing, faults, Kitty read and write, paste events, resize, RIS.
- `EffectsHostChecks` (Terminal.Smoke): bell throttling, notification lifecycle and activation, progress aggregation across panes and windows, card actions, expiry and bounds, settings round trip and tolerance of a bad `Effects` section.
- `EffectsSessionChecks` (ConPty.Smoke, `--effects-only`): through a real ConPTY shell, delivery outside the terminal lock, ordering, a throwing subscriber, and progress removal on exit.

The UI inbox bounds repeated approval notices as well as ordinary effects. See [integration verification](README.md) for platform and UI coverage.
