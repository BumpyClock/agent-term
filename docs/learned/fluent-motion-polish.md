# Fluent Motion + Surface Token Rollout (2026-02-10)

## Sources used
- `/Users/adityasharma/Projects/fluent-tokens/tokens/animation.md`
- `/Users/adityasharma/Projects/fluent-tokens/tokens/elevation.md`
- `/Users/adityasharma/Projects/fluent-tokens/tokens/layering-and-material.md`
- `/Users/adityasharma/Projects/fluent-tokens/tokens/color-light.md`
- `/Users/adityasharma/Projects/fluent-tokens/tokens/color-dark.md`

## What was implemented
- Added first-class shared theme domains in `gpui-component`:
  - `ThemeMotion`
  - `ThemeElevation`
  - `ThemeMaterial`
- Added matching schema domains:
  - `ThemeMotionConfig`
  - `ThemeElevationConfig`
  - `ThemeMaterialConfig`
- Added curated token defaults module:
  - `vendor/gpui-component/crates/ui/src/theme/fluent_tokens.rs`
- Migrated surface presets to consume theme material/elevation defaults (with explicit override escape hatches).
- Migrated component animation timings/easing to `cx.theme().motion`:
  - `dialog`, `sheet`, `notification`, `command_palette`, `switch`, `checkbox`, `progress`, `progress_circle`
- Enforced reduced-motion hard behavior for non-essential movement animations in those components.
- Added app-level reduced motion setting and wiring:
  - `src/settings.rs` (`reduce_motion`)
  - `src/settings_dialog.rs` (Appearance toggle)
  - `src/app/mod.rs` (`WindowShell::reduced_motion(...)`)
- Updated app theme composition to explicitly provide Fluent-aligned motion/elevation/material config and Fluent-leaning color palette values in `src/theme.rs`.

## Validation snapshot
- `cargo check` (workspace): pass
- `cargo check -p gpui-component`: pass
- `cargo test -p gpui-component --no-run`: pass
- `cargo test -p gpui-component theme -- --nocapture`: pass

## Follow-ups
- Repeated cubic-bezier parsing helpers now exist in multiple components; a shared helper can reduce duplication.
- Consider adding targeted unit tests for easing parse helpers and reduced-motion branch behavior.
