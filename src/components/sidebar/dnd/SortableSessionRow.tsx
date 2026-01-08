// ABOUTME: Wraps SessionRow to make it draggable using dnd-kit.
// ABOUTME: Applies transform/transition styles and passes drag data for identification.
// ABOUTME: Also sets native HTML5 drag data for cross-window drag support.

import type { ReactNode } from 'react';
import { useEffect, useRef } from 'react';
import { motion } from 'motion/react';
import { useSortable } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { getCurrentWindowId } from '@/lib/windowContext';
import { CROSS_WINDOW_MIME_TYPE } from '@/hooks/useCrossWindowDrop';
import type { SessionDragData } from './types';

interface SortableSessionRowProps {
  sessionId: string;
  sectionId: string;
  disabled?: boolean;
  children: ReactNode;
}

export function SortableSessionRow({
  sessionId,
  sectionId,
  disabled = false,
  children,
}: SortableSessionRowProps) {
  const nodeRef = useRef<HTMLDivElement>(null);
  const {
    attributes,
    listeners,
    setNodeRef,
    transform,
    transition,
    isDragging,
  } = useSortable({
    id: sessionId,
    data: {
      type: 'session',
      sessionId,
      sectionId,
    } satisfies SessionDragData,
    disabled,
  });

  // Attach native dragstart handler for cross-window support
  // We use useEffect since motion.div's onDragStart conflicts with native drag events
  useEffect(() => {
    const node = nodeRef.current;
    if (!node) return;

    const handleNativeDragStart = (e: DragEvent) => {
      const dragData = JSON.stringify({
        sessionId,
        sourceWindowId: getCurrentWindowId(),
      });
      e.dataTransfer?.setData(CROSS_WINDOW_MIME_TYPE, dragData);
      if (e.dataTransfer) {
        e.dataTransfer.effectAllowed = 'copyMove';
      }
    };

    node.addEventListener('dragstart', handleNativeDragStart);
    return () => {
      node.removeEventListener('dragstart', handleNativeDragStart);
    };
  }, [sessionId]);

  // Combine refs for both dnd-kit and our native event handler
  const combinedRef = (el: HTMLDivElement | null) => {
    setNodeRef(el);
    (nodeRef as React.MutableRefObject<HTMLDivElement | null>).current = el;
  };

  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
    opacity: isDragging ? 0.5 : 1,
    position: 'relative' as const,
    zIndex: isDragging ? 1 : 0,
  };

  return (
    <motion.div
      ref={combinedRef}
      style={style}
      layout
      transition={{ type: 'spring', stiffness: 300, damping: 30 }}
      draggable
      {...attributes}
      {...listeners}
    >
      {children}
    </motion.div>
  );
}
