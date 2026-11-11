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
