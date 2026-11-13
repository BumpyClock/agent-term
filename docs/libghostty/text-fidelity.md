# Text and cursor fidelity

Terminal frames retain underline style and resolved underline color, overline, blink, hollow cursor shape, wide-tail position, and password-input metadata. Password-input metadata is informational and does not establish whether terminal text is secret. The renderer uses the active terminal palette when capturing underline colors. Text blink follows the existing animation preference and timer. Hidden text does not start the timer.

Blink phase changes repaint affected cells through the retained raster damage path. Static cells retain their raster content. Disabling animations leaves text visible, and hidden panes stop the shared blink timer.

The renderer smoke suite checks distinct underline patterns and colors, overline, selection color overrides, hidden and blinking Unicode text, and hollow wide cursors. Its Windows pixel checks passed during integration. Native smoke tests cover style extraction and palette changes. Retained-surface lifecycle checks remain part of the integrated app verification.

A focused block cursor redraws underline, overline, and strikethrough in its text color. The raster fixture checks each decoration while the cursor covers a decorated cell.
