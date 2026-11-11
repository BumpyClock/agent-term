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

## Pane materials

1. Record the existing appearance values. Open a separate test window over contrasting desktop content. Keep Windows transparency and theme settings unchanged.
2. In Settings, choose Acrylic and compare saved opacity values of 0%, 50%, and 100%. Focus the terminal window after each save so inactive-window fallback does not mask the result. Low values should reveal a frosted desktop background; high values should add tint. Text stays opaque. The title bar and outer window keep Mica throughout.
3. Give a second shell profile a different opacity. Create a mixed-shell split and check that each pane retains its own material. Resize, switch tabs, and close one pane. Check that the remaining backdrop still covers its pane without stale rectangles or losing keyboard input.
4. Switch a profile to Solid. Check that its desktop backdrop disappears, the opacity slider disables, and other profiles remain unchanged.
5. Restore the recorded appearance values. Record Windows-controlled fallback behavior separately from the active acrylic result. Do not change Windows configuration just to force the effect.
