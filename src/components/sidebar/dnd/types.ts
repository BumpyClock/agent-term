// ABOUTME: Type definitions for drag-and-drop operations in the sidebar.
// ABOUTME: Defines drag data structures for sessions and sections.

export type DragItemType = 'session' | 'section';

/** Drag data attached to session items for identification during drag operations */
export interface SessionDragData {
  type: 'session';
  sessionId: string;
  sectionId: string;
}

/** Drag data attached to section items for identification during drag operations */
export interface SectionDragData {
  type: 'section';
  sectionId: string;
}

export type DragData = SessionDragData | SectionDragData;
