# CI runners

The current GPUI branch keeps its Clippy matrix job names, including `Clippy (ubuntu-latest)`, but maps that Linux entry to Boat using a separate `matrix.runner`. macOS and Windows entries remain native. Linux setup and Unix Clippy selection use `runner.os`, so both still run on Boat. Format Check also uses Boat.

No architecture choice, release workflow activation, Rust policy or cache change is included. Tauri main and the GPUI branch remain distinct delivery lines. Existing compiler/format failures require diagnosis on the chosen line; runner selection does not fix them.

Owner-only public Boat execution is approved for BumpyClock. Outside contributors and bots remain blocked by account policy. No CI dispatch or native acceptance is claimed. Existing Windows/macOS validation must remain.
