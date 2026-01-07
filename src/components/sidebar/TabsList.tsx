// ABOUTME: Renders the list of terminal tabs within a sidebar section.
// ABOUTME: Wires per-tab handlers for selection, editing, menus, and closing.

import type { MouseEvent } from 'react';
import type { Session } from '../../store/terminalStore';
import { SessionRow } from './SessionRow';
import { SortableContext, verticalListSortingStrategy } from '@dnd-kit/sortable';
import { SortableSessionRow } from './dnd';

interface TabsListProps {
  sessions: Session[];
  activeSessionId: string | null;
  editingSessionId: string | null;
  editingSessionTitle: string;
  sectionId: string;
  showEmpty?: boolean;
  onEditingTitleChange: (value: string) => void;
  onSaveSessionEdit: (session: Session) => void;
  onCancelSessionEdit: () => void;
  onStartSessionEdit: (session: Session) => void;
  onSelectSession: (session: Session) => void;
  onSessionContextMenu: (session: Session, event: MouseEvent<HTMLDivElement>) => void;
  onMenuClick: (session: Session, event: MouseEvent<HTMLButtonElement>) => void;
  onCloseSession: (session: Session, event: MouseEvent<HTMLButtonElement>) => void;
}

export function TabsList({
  sessions,
  activeSessionId,
  editingSessionId,
  editingSessionTitle,
  sectionId,
  showEmpty = true,
  onEditingTitleChange,
  onSaveSessionEdit,
  onCancelSessionEdit,
  onStartSessionEdit,
  onSelectSession,
  onSessionContextMenu,
  onMenuClick,
  onCloseSession,
}: TabsListProps) {
  const platformInfo =
    typeof navigator !== 'undefined'
      ? navigator.userAgent ?? 'unknown-platform'
      : 'unknown-platform';

  if (sessions.length === 0 && showEmpty) {
    return <div className="empty-section">No terminals</div>;
  }

  if (sessions.length === 0) {
    return null;
  }

  return (
    <SortableContext items={sessions.map((s) => s.id)} strategy={verticalListSortingStrategy}>
      {sessions.map((session) => (
        <SortableSessionRow
          key={session.id}
          sessionId={session.id}
          sectionId={sectionId}
          disabled={editingSessionId === session.id}
        >
          <SessionRow
            session={session}
            isActive={activeSessionId === session.id}
            isEditing={editingSessionId === session.id}
            editingTitle={editingSessionTitle}
            onEditingTitleChange={onEditingTitleChange}
            onEditingTitleCommit={() => onSaveSessionEdit(session)}
            onEditingTitleCancel={onCancelSessionEdit}
            onSelect={() => onSelectSession(session)}
            onContextMenu={(event) => onSessionContextMenu(session, event)}
            onMenuClick={(event) => onMenuClick(session, event)}
            onClose={(event) => {
              console.debug('[tab-close][ui] click', {
                sessionId: session.id,
                sectionId: session.sectionId,
                platform: platformInfo,
              });
              onCloseSession(session, event);
            }}
            onStartEdit={(event) => {
              event.stopPropagation();
              onStartSessionEdit(session);
            }}
          />
        </SortableSessionRow>
      ))}
    </SortableContext>
  );
}
