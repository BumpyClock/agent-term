# Branded terminal theme sources

AgentTerm ships two third-party palette families. Each color in their four entries in `src/AgentTerm.Terminal/TerminalTheme.cs` is copied verbatim from the upstream Ghostty theme file named below. Do not tune these colors for contrast or taste. Change them only by re-fetching upstream, updating the pinned fixtures in `tests/AgentTerm.Terminal.Smoke/BrandedThemeChecks.cs`, and updating this file.

Upstream sources were fetched on 2026-11-11.

## Mapping

Ghostty keys map to the catalog constructor as follows:

| Ghostty key | `TerminalTheme` |
| --- | --- |
| `background` | `Background` |
| `foreground` | `Foreground` |
| `cursor-color` | `Cursor` |
| `cursor-text` | `CursorText` (explicit only when it differs from `background`; otherwise follow the effective native background, including OSC overrides) |
| `selection-background` | `SelectionBackground` |
| `selection-foreground` | `SelectionForeground` |
| `palette = 0..15` | `Ansi[0..15]` |

Indices 16 to 255 keep Ghostty's default xterm cube and ramp. Catppuccin's `split-divider-color` has no AgentTerm equivalent and is not mapped.

## Flexoki

| ID | Appearance | Upstream file |
| --- | --- | --- |
| `flexoki-light` | Light | `ghostty/Flexoki Light` |
| `flexoki-dark` | Dark | `ghostty/Flexoki Dark` |

- **Author.** Steph Ango. The palette page https://stephango.com/flexoki lists "Ghostty (built-in)" as the Ghostty port.
- **Source.** Ghostty's built-in themes come from https://github.com/mbadolato/iTerm2-Color-Schemes. Ghostty `main` (`ghostty-org/ghostty@c6174c8`) pins `ghostty-themes-release-20261102-150615-3d8a6a1.tgz` in `build.zig.zon`. The Flexoki files in that archive are byte-identical to `mbadolato/iTerm2-Color-Schemes@a854824` (HEAD on fetch date).
- **File revision.** Last change to both files is `mbadolato/iTerm2-Color-Schemes@5528a1a9cf685721550a76dd594e3939356ef5af` ("Fix Flexoki Light/Dark selection color", 2025-09-16).
- **Licenses.** Flexoki is MIT, Copyright (c) 2023 Steph Ango ([copy](theme-licenses/flexoki-LICENSE.txt), from `kepano/flexoki@8d723ba`). The Ghostty-format collection is MIT, Copyright (c) 2011 to Present Mark Badolato ([copy](theme-licenses/iterm2-color-schemes-LICENSE.txt)).
- **Known divergence.** The author's own repo `kepano/flexoki@8d723ba` ships other terminal ports that disagree with the Ghostty file. `kitty/flexoki_light.conf` swaps normal and bright ANSI 1 to 6 and uses different 7, 8, and 15. `windows-terminal/*.json` uses other values again (for example yellow `C19C00`, purple `5E409D`). AgentTerm follows the Ghostty built-in because the author's page points Ghostty users there.

## Catppuccin

| ID | Appearance | Upstream file |
| --- | --- | --- |
| `catppuccin-latte` | Light | `themes/catppuccin-latte.conf` |
| `catppuccin-mocha` | Dark | `themes/catppuccin-mocha.conf` |

- **Author.** Catppuccin Org.
- **Source.** The official port https://github.com/catppuccin/ghostty at `b0b03ccee7ae8f16b13bd4fdfe267616defdb2b7` (HEAD on fetch date). Last change to both files is `cb8a578322f7f20418c79255b9b347f716522be5` (2025-10-20).
- **License.** MIT, Copyright (c) 2021 Catppuccin ([copy](theme-licenses/catppuccin-ghostty-LICENSE.txt)).
- **Known divergence.** The repo README says Ghostty's bundled `Catppuccin Latte` and `Catppuccin Mocha` "may differ from the themes provided in this repository". They do. The bundled files in `ghostty-themes-release-20261102` use generated bright colors (for example Mocha ANSI 9 `f7aec2`), reorder Latte ANSI 0, 7, 8, and 15 (bundled 0 is `bcc0cc`, official is `5c5f77`), use `f5e0dc`/`dc8a78` selection backgrounds, and set Mocha `cursor-text` to the background. AgentTerm uses the official repo files, including Mocha `cursor-text = 11111b`.

## Contrast

These palettes are brand-exact, so they do not meet the 4.5:1 ANSI contrast target used by AgentTerm's own light palettes. Measured WCAG ratios against each theme's background, ANSI indices below 4.5:1:

| Theme | Foreground | ANSI below 4.5:1 |
| --- | --- | --- |
| `flexoki-light` | 18.62 | 2, 3, 6, 8, 9, 10, 11, 12, 13, 14, 15 (lowest 15 at 1.55) |
| `flexoki-dark` | 11.98 | 0, 1, 8, 9, 10, 12, 13, 14 (lowest 0 at 1.00) |
| `catppuccin-latte` | 7.06 | 2 to 15 except 9 (lowest 15 at 1.61) |
| `catppuccin-mocha` | 11.34 | 0, 8 (lowest 0 at 1.80) |

Blanket contrast checks in `ThemeChecks` must exclude these IDs. `BrandedThemeChecks` pins exact upstream values instead.
