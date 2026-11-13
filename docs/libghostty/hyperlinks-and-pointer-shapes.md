# Hyperlinks and application pointer shapes

Scope: OSC 8 hyperlinks and OSC 22 pointer shapes. Native source is libghostty-vt 1.0.0 public API.

## What users get

- **Open a link.** Hold Ctrl and left-click a link. The open happens on release, and only if the pointer is still on a link with the same destination. Nothing opens because output arrived.
- **See where it goes.** Hovering a link underlines it across every row it covers and shows the destination and a hint in a small chip at the pane corner. Holding Ctrl makes the underline thicker and the pointer a hand. A blocked link shows the no-entry pointer and the reason.
- **Link menu.** Right-click a link, or press the Menu key (or Shift+F10) with the pointer on a link or with the text cursor on one. The menu has Open link, Copy link address, Copy and Paste. Items are disabled with a tooltip reason when the policy refuses them. Right-click off a link keeps the old behavior (copy selection, else paste).
- **Program pointer shapes.** A program that sends OSC 22 sets the pointer over the pane. The default is the I-beam, because libghostty reports the text shape until a program asks for another. Programs restore their chosen shape with OSC 22. Prompt callbacks do not inject synthetic output into the parser.
- **Selection.** A plain drag selects as before. Hover feedback is hidden during a selection drag. Ctrl+drag that starts on a link does not select. Shift keeps its selection override.

## Mouse tracking

When a program tracks the mouse it keeps every plain event, including hover. The only override is the Ctrl gesture: Ctrl+left on a link is taken for the link. Ctrl+left off a link goes to the program unchanged, and so does every other button. Link hover feedback in a tracking program appears only while Ctrl is held. Shift+right-click reaches the link menu in a tracking program, the same bypass the pane already uses for paste.

The routing is pure code in `TerminalLinkGesture` (`src/AgentTerm.Terminal/TerminalLink.cs`) so tests cover it without a window.

## URI policy

Confirmation shows the complete validated URI in a selectable, scrollable field. Only the hover chip truncates long targets. A long user-information component cannot conceal the destination host behind the hover ellipsis during approval.

`TerminalLinkPolicy.Evaluate` decides from the raw bytes. It never starts a program.

| Result | Meaning | Examples |
| --- | --- | --- |
| Allow | opens on Ctrl+Click | `http`, `https` (no user name), `mailto` |
| Confirm | a dialog shows the reason and full address first | `file:///C:/docs/a.txt`, `https://user@host`, any other well formed scheme such as `vscode:` or `ssh:` |
| Block | never opens | `javascript:`, `data:`, `vbscript:`, `ms-msdt:`, `search-ms:`, `ms-appinstaller:` and similar, file links to another machine, to programs or scripts, to alternate data streams or trailing dot names, relative or drive-letter paths, empty or oversized links (over 2083 bytes) |

Blocked as unsafe to copy as well: invalid UTF-8, control characters, spaces and invisible characters (bidi marks, zero width). Those show escaped symbols in the chip, and Copy link address is disabled. libghostty's parser already drops C0 controls inside an OSC string.

## Lookup

- `GhosttyTerminal.ReadLink(column, row)` resolves a fresh public `ghostty_terminal_grid_ref` for the viewport cell, reads the cell flag, then copies the URI bytes with `ghostty_grid_ref_hyperlink_uri`. No grid reference is kept. A tracked reference is never stored for links.
- The link range comes from comparing the URI bytes of neighboring cells. The public API exposes no link id, so two adjacent links with identical URIs and different ids read as one run. This is a contract limit, not a regex guess.
- A tail half of a wide character resolves to its first half. A wrapped wide character's spacer joins the link of the character that wrapped.
- A link joins rows only across a soft wrap (the row's wrap flag). A hard line break starts a new run.
- `TerminalSession.ReadLinkAt(column, row, displayedFrame)` takes `_renderGate` then `_gate`, and checks both viewport and captured content revision. Output or resize after that frame invalidates the lookup, including overwritten text at unchanged coordinates. The click path re-reads at press and again at release.
- `TerminalSession.ReadMouseShape()` and `ReadPointerMode()` take only `_gate`. OSC 22 reaches the session through ConPTY, which a session test confirms.

## Where it lives

| File | Role |
| --- | --- |
| `src/AgentTerm.Terminal/GhosttyTerminal.Hyperlinks.cs` | native lookup, `LinkNative` imports |
| `src/AgentTerm.Terminal/TerminalSession.Hyperlinks.cs` | scoped getters and stale-frame rejection |
| `src/AgentTerm.Terminal/TerminalLink.cs` | link types, URI policy, gesture routing |
| `src/AgentTerm.Terminal/TerminalMouseShape.cs` | shape enum, nearest Windows pointer |
| `src/AgentTerm.App/MainPage.Links.cs` | hover, overlay, click, menu, confirm dialog |
| `src/AgentTerm.App/TerminalSurface.Pointer.cs` | OS pointer through `ProtectedCursor` |

The OS pointer is separate from the text cursor, which `TerminalSurface` draws on its own canvas.

## Wiring in shared files

- `TerminalSession` and `TerminalSurface` became `partial`.
- `MainPage.xaml.cs`: `InitializeLinks()` in the constructor, `HandleLinkPress` and the `TryShowLinkMenu` right-click call in the pointer press handler, a `_linkPress` early return in the move handler, `HandleLinkRelease` in the release handler, `CancelLinkPress` on capture lost, `ResetLinkInteraction` in `CancelSelectionGesture`, `RefreshLinkFeedback` after each presented frame, and `TryShowLinkMenuFromKeyboard` in `Terminal_KeyDown`.
- Geometry: all hit testing uses `Terminal.Geometry.TryCellAt` and `RowTop`. If scroll geometry (overscan, smooth scroll) adds a pixel offset, `TryCellAt` and `RowTop` must include it. The link overlay uses `RowTop` for underlines.

## Pointer mapping

Windows has no vertical text, zoom, copy, alias or grab pointer. They map to the nearest useful one (see `TerminalMouseShapes.ToPointer`). A shape this build does not know falls back to the arrow.

## Limits

- Touch input shows no hover feedback and cannot Ctrl+click. A link menu from touch is not provided.
- Selection of link text by keyboard is not linked to this feature.
- The menu key path needs the text cursor on a link cell or a hovering pointer.
- Explicit `id=` grouping of OSC 8 links is not visible through the public API.
- `file:` links open through `Windows.System.Launcher` after confirmation. A UI pass should confirm Windows accepts them. If not, only the final launch call in `OpenLinkAsync` changes.

## Checks

- `tests/AgentTerm.Terminal.Smoke/HyperlinkChecks.cs`: policy matrix, native lookup (split writes, ST and BEL, reset, adjacent links, wrap, wide glyphs, spacer heads, reflow, scroll, alternate screen, ownership, disposal), pointer mapping, click routing.
- `tests/AgentTerm.ConPty.Smoke/HyperlinkSessionChecks.cs`: a real shell prints OSC 8 and OSC 22, stale frame rejection, explicit pointer reset, disposal. Run alone with `--hyperlink-only`.
