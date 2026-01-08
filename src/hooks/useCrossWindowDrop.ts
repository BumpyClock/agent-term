// ABOUTME: Hook for handling cross-window drag-and-drop of sessions.
// ABOUTME: Uses native HTML5 drag API with custom MIME type for inter-window communication.

import { useEffect, useState, useCallback } from 'react';
import { invoke } from '@tauri-apps/api/core';
import { getCurrentWindowId } from '../lib/windowContext';

/** Data transferred during a cross-window session drag operation */
interface CrossWindowDragData {
  sessionId: string;
  sourceWindowId: string;
}

/** MIME type used to identify Agent Term session drags */
export const CROSS_WINDOW_MIME_TYPE = 'application/agent-term-session';

/**
 * Hook that manages cross-window drag-and-drop for sessions.
 * Returns state indicating if a drag is over the current window.
 *
 * Example:
 * ```typescript
 * const { isDraggingOver } = useCrossWindowDrop();
 * if (isDraggingOver) {
 *   // Show drop zone overlay
 * }
 * ```
 */
export function useCrossWindowDrop() {
  const [isDraggingOver, setIsDraggingOver] = useState(false);

  const handleDragOver = useCallback((e: DragEvent) => {
    if (!e.dataTransfer?.types.includes(CROSS_WINDOW_MIME_TYPE)) {
      return;
    }
    e.preventDefault();
    e.dataTransfer.dropEffect = e.ctrlKey || e.metaKey ? 'copy' : 'move';
    setIsDraggingOver(true);
  }, []);

  const handleDragLeave = useCallback((e: DragEvent) => {
    // Only trigger if leaving the window, not entering a child element
    if (e.relatedTarget === null) {
      setIsDraggingOver(false);
    }
  }, []);

  const handleDrop = useCallback(async (e: DragEvent) => {
    setIsDraggingOver(false);

    const data = e.dataTransfer?.getData(CROSS_WINDOW_MIME_TYPE);
    if (!data) return;

    e.preventDefault();

    let dragData: CrossWindowDragData;
    try {
      dragData = JSON.parse(data) as CrossWindowDragData;
    } catch {
      console.error('[cross-window-drop] Failed to parse drag data');
      return;
    }

    const { sessionId, sourceWindowId } = dragData;
    const targetWindowId = getCurrentWindowId();

    if (sourceWindowId === targetWindowId) {
      // Same window - let existing dnd-kit handle it
      return;
    }

    const isMirror = e.ctrlKey || e.metaKey;

    try {
      if (isMirror) {
        // Mirror mode: subscribe target window to session
        await invoke('subscribe_to_session', {
          sessionId,
          windowLabel: targetWindowId,
        });
      } else {
        // Move mode: move session from source to target
        await invoke('move_session_to_window', {
          sessionId,
          sourceWindowId,
          targetWindowId,
        });

        // Notify source window to update UI
        await invoke('relay_window_ipc', {
          targetWindowLabel: sourceWindowId,
          eventName: 'session-moved',
          payload: { sessionId, targetWindowId },
        });
      }

      // Emit local event to update UI
      window.dispatchEvent(
        new CustomEvent('session-dropped', {
          detail: { sessionId, isMirror },
        })
      );
    } catch (err) {
      console.error('[cross-window-drop] Failed to process drop:', err);
    }
  }, []);

  useEffect(() => {
    window.addEventListener('dragover', handleDragOver);
    window.addEventListener('dragleave', handleDragLeave);
    window.addEventListener('drop', handleDrop);

    return () => {
      window.removeEventListener('dragover', handleDragOver);
      window.removeEventListener('dragleave', handleDragLeave);
      window.removeEventListener('drop', handleDrop);
    };
  }, [handleDragOver, handleDragLeave, handleDrop]);

  return { isDraggingOver };
}
