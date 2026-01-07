// ABOUTME: Renders a single sidebar session tab with title, status, and actions.
// ABOUTME: Provides UI events for selecting, editing, and closing a terminal session.

import type { MouseEvent } from 'react';
import type { Session } from '../../store/terminalStore';
import { getStatusTitle, getToolTitle, needsAttention, resolveSessionIcon } from './utils';
import { LucideIcon } from './LucideIcon';

interface SessionRowProps {
  session: Session;
  isActive: boolean;
  isEditing: boolean;
  editingTitle: string;
  onEditingTitleChange: (value: string) => void;
  onEditingTitleCommit: () => void;
  onEditingTitleCancel: () => void;
  onSelect: () => void;
  onContextMenu: (event: MouseEvent<HTMLDivElement>) => void;
  onMenuClick: (event: MouseEvent<HTMLButtonElement>) => void;
  onClose: (event: MouseEvent<HTMLButtonElement>) => void;
  onStartEdit: (event: MouseEvent<HTMLSpanElement>) => void;
}

export function SessionRow({
  session,
  isActive,
  isEditing,
  editingTitle,
  onEditingTitleChange,
  onEditingTitleCommit,
  onEditingTitleCancel,
  onSelect,
  onContextMenu,
  onMenuClick,
  onClose,
  onStartEdit,
}: SessionRowProps) {
  const icon = resolveSessionIcon(session);
  const toolTitle = getToolTitle(session.tool);
  const isMonochromeIcon = icon?.kind === 'img' && icon.monochrome;

  return (
    <div
      className={`tab ${isActive ? 'active' : ''} status-${session.status}`}
      onClick={onSelect}
      onContextMenu={onContextMenu}
    >
      {icon?.kind === 'lucide' ? (
        <LucideIcon
          id={icon.id}
          className="tab-tool-icon tab-tool-icon--lucide"
          title="Custom icon"
        />
      ) : icon?.kind === 'img' ? (
        <img
          className={`tab-tool-icon ${isMonochromeIcon ? 'tab-tool-icon--mono' : ''}`}
          src={icon.src}
          alt={toolTitle}
          title={toolTitle}
        />
      ) : null}
      {isEditing ? (
        <input
          className="tab-title-input"
          type="text"
          value={editingTitle}
          onChange={(event) => onEditingTitleChange(event.target.value)}
          onBlur={onEditingTitleCommit}
          onKeyDown={(event) => {
            if (event.key === 'Enter') onEditingTitleCommit();
            if (event.key === 'Escape') onEditingTitleCancel();
          }}
          onClick={(event) => event.stopPropagation()}
          autoFocus
        />
      ) : (
        <span className="tab-title-wrap">
          <span className="tab-title" onDoubleClick={onStartEdit} title={session.title}>
            {session.title}
          </span>
          {needsAttention(session.status) && (
            <span
              className={`tab-status-dot status-${session.status}`}
              title={getStatusTitle(session.status)}
            />
          )}
        </span>
      )}
      <div className="tab-actions">
        <button
          className="tab-menu"
          onClick={onMenuClick}
          title="Tab menu"
          aria-label="Tab menu"
        >
          ⋯
        </button>
        <button
          className="tab-close"
          onClick={onClose}
          title="Close Terminal"
          aria-label="Close Terminal"
        >
          ×
        </button>
      </div>
    </div>
  );
}
