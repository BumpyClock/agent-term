// ABOUTME: Visual overlay shown when dragging a session tab between windows.
// ABOUTME: Indicates the drop target and provides feedback for move vs mirror modes.

import { useCrossWindowDrop } from '../hooks/useCrossWindowDrop';
import './WindowDropZone.css';

/**
 * Renders a full-window overlay when a session is being dragged over from another window.
 * Shows different instructions based on whether the user is holding Ctrl/Cmd (mirror) or not (move).
 *
 * Example:
 * ```typescript
 * // In App.tsx root
 * <WindowDropZone />
 * ```
 */
export function WindowDropZone() {
  const { isDraggingOver } = useCrossWindowDrop();

  if (!isDraggingOver) return null;

  return (
    <div className="window-drop-zone">
      <div className="window-drop-zone-content">
        <span className="window-drop-zone-icon">+</span>
        <span className="window-drop-zone-text">Drop here to move session</span>
        <span className="window-drop-zone-hint">Hold Ctrl/Cmd to mirror instead</span>
      </div>
    </div>
  );
}
