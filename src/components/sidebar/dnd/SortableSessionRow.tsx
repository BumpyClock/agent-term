// ABOUTME: Wraps SessionRow to make it draggable using dnd-kit.
// ABOUTME: Applies transform/transition styles and passes drag data for identification.

import type { ReactNode } from 'react';
import { motion } from 'motion/react';
import { useSortable } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
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

  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
    opacity: isDragging ? 0.5 : 1,
    position: 'relative' as const,
    zIndex: isDragging ? 1 : 0,
  };

  return (
    <motion.div
      ref={setNodeRef}
      style={style}
      layout
      transition={{ type: 'spring', stiffness: 300, damping: 30 }}
      {...attributes}
      {...listeners}
    >
      {children}
    </motion.div>
  );
}
