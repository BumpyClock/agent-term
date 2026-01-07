// ABOUTME: Wraps ProjectSection to make it draggable using dnd-kit.
// ABOUTME: Provides drag listeners to be passed to section header for drag handle.

import type { ReactElement } from 'react';
import { cloneElement } from 'react';
import { motion } from 'motion/react';
import { useSortable } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import type { SectionDragData } from './types';

interface SortableSectionProps {
  sectionId: string;
  disabled?: boolean;
  children: ReactElement<{ dragHandleProps?: Record<string, unknown> }>;
}

export function SortableSection({
  sectionId,
  disabled = false,
  children,
}: SortableSectionProps) {
  const {
    attributes,
    listeners,
    setNodeRef,
    transform,
    transition,
    isDragging,
  } = useSortable({
    id: sectionId,
    data: {
      type: 'section',
      sectionId,
    } satisfies SectionDragData,
    disabled,
  });

  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
    opacity: isDragging ? 0.5 : 1,
  };

  const dragHandleProps = disabled ? undefined : { ...attributes, ...listeners };

  return (
    <motion.div
      ref={setNodeRef}
      style={style}
      layout
      transition={{ type: 'spring', stiffness: 300, damping: 30 }}
    >
      {cloneElement(children, { dragHandleProps })}
    </motion.div>
  );
}
