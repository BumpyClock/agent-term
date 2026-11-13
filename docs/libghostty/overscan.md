# Overscan capture

`GhosttyTerminal.SetOverscanEnabled(true)` requests one additional native row on each available side of the viewport. The visible `TerminalFrame.Cells` grid retains its existing dimensions. `OverscanAbove` and `OverscanBelow` contain separately owned rows with signed viewport coordinates. A frame remains stable after later captures. Capture is opt-in and does not itself change scrolling or draw the extra rows.

A host must translate and clip all terminal layers together when implementing fractional scrolling. It must also map pointer and caret coordinates through the same offset and retain application mouse reporting. Those surface and input changes are not yet implemented.
