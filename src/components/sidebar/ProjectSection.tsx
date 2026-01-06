import type { MouseEvent } from 'react';
import type { Section, Session } from '../../store/terminalStore';
import { resolveSectionIcon } from './utils';
import { SectionHeader } from './SectionHeader';
import { TabsList } from './TabsList';

interface ProjectSectionProps {
  section: Section;
  sessions: Session[];
  activeSessionId: string | null;
  isCollapsed: boolean;
  isEditing: boolean;
  editingName: string;
  editingSessionId: string | null;
  editingSessionTitle: string;
  onEditingNameChange: (value: string) => void;
  onSaveSectionEdit: () => void;
  onCancelSectionEdit: () => void;
  onStartSectionEdit: () => void;
  onToggleCollapse: () => void;
  onOpenSectionMenu: (event: MouseEvent<HTMLButtonElement>) => void;
  onOpenTabPicker: (event: MouseEvent<HTMLButtonElement>) => void;
  onDeleteSection: (event: MouseEvent<HTMLButtonElement>) => void;
  onSectionContextMenu: (event: MouseEvent<HTMLSpanElement>) => void;
  onSelectSession: (session: Session) => void;
  onSessionContextMenu: (session: Session, event: MouseEvent<HTMLDivElement>) => void;
  onSessionMenuClick: (session: Session, event: MouseEvent<HTMLButtonElement>) => void;
  onCloseSession: (session: Session, event: MouseEvent<HTMLButtonElement>) => void;
  onEditingTitleChange: (value: string) => void;
  onSaveSessionEdit: (session: Session) => void;
  onCancelSessionEdit: () => void;
  onStartSessionEdit: (session: Session) => void;
}

export function ProjectSection({
  section,
  sessions,
  activeSessionId,
  isCollapsed,
  isEditing,
  editingName,
  editingSessionId,
  editingSessionTitle,
  onEditingNameChange,
  onSaveSectionEdit,
  onCancelSectionEdit,
  onStartSectionEdit,
  onToggleCollapse,
  onOpenSectionMenu,
  onOpenTabPicker,
  onDeleteSection,
  onSectionContextMenu,
  onSelectSession,
  onSessionContextMenu,
  onSessionMenuClick,
  onCloseSession,
  onEditingTitleChange,
  onSaveSessionEdit,
  onCancelSessionEdit,
  onStartSessionEdit,
}: ProjectSectionProps) {
  return (
    <div className="section">
      <SectionHeader
        section={section}
        isCollapsed={isCollapsed}
        isEditing={isEditing}
        editingName={editingName}
        icon={resolveSectionIcon(section)}
        onToggleCollapse={onToggleCollapse}
        onEditingNameChange={onEditingNameChange}
        onSaveName={onSaveSectionEdit}
        onCancelEdit={onCancelSectionEdit}
        onStartEdit={onStartSectionEdit}
        onContextMenu={onSectionContextMenu}
        onMenuClick={onOpenSectionMenu}
        onAddTab={onOpenTabPicker}
        onDelete={onDeleteSection}
      />
      {!isCollapsed && (
        <div className="tabs-list">
          <TabsList
            sessions={sessions}
            activeSessionId={activeSessionId}
            editingSessionId={editingSessionId}
            editingSessionTitle={editingSessionTitle}
            onEditingTitleChange={onEditingTitleChange}
            onSaveSessionEdit={onSaveSessionEdit}
            onCancelSessionEdit={onCancelSessionEdit}
            onStartSessionEdit={onStartSessionEdit}
            onSelectSession={onSelectSession}
            onSessionContextMenu={onSessionContextMenu}
            onMenuClick={onSessionMenuClick}
            onCloseSession={onCloseSession}
          />
        </div>
      )}
    </div>
  );
}
