# Fluent Motion + Surface Token Rollout (2026-02-10)

Updated for the gpui-kit 0.7.0 migration. The earlier fork-only `ThemeMotion`, `ThemeElevation`, and `ThemeMaterial` domains no longer exist.

## Sources used
- `/Users/adityasharma/Projects/fluent-tokens/tokens/animation.md`
- `/Users/adityasharma/Projects/fluent-tokens/tokens/elevation.md`
- `/Users/adityasharma/Projects/fluent-tokens/tokens/layering-and-material.md`
- `/Users/adityasharma/Projects/fluent-tokens/tokens/color-light.md`
- `/Users/adityasharma/Projects/fluent-tokens/tokens/color-dark.md`

## Current state
- The app depends on `gpui-kit` 0.7.0 from crates.io. Components come from `gpui_kit::component`.
- The app-level `reduce_motion` setting lives in `src/settings.rs` and has an Appearance toggle in `src/settings_dialog.rs`.
- `src/theme.rs` applies the setting with `cx.set_reduce_motion(...)`. Components read it from the app context.
- `src/theme.rs` also composes the Fluent-leaning color palette.
- In-app `backdrop_blur` is not available with gpui-kit 0.7.0.

## Follow-ups
- Add unit tests for the reduced-motion branch behavior.
