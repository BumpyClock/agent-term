import type { MouseEvent } from 'react';
import type { Session } from '../../store/terminalStore';
import { SessionRow } from './SessionRow';

interface TabsListProps {
  sessions: Session[];
  activeSessionId: string | null;
  editingSessionId: string | null;
  editingSessionTitle: string;
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
  if (sessions.length === 0 && showEmpty) {
    return <div className="empty-section">No terminals</div>;
  }

  if (sessions.length === 0) {
    return null;
  }

  return (
    <>
      {sessions.map((session) => (
        <SessionRow
          key={session.id}
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
          onClose={(event) => onCloseSession(session, event)}
          onStartEdit={(event) => {
            event.stopPropagation();
            onStartSessionEdit(session);
          }}
        />
      ))}
    </>
  );
}
