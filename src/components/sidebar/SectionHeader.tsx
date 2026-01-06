import type { MouseEvent } from 'react';
import type { Section } from '../../store/terminalStore';
import type { IconDescriptor } from './types';
import { LucideIcon } from './LucideIcon';

interface SectionHeaderProps {
  section: Section;
  isCollapsed: boolean;
  isEditing: boolean;
  editingName: string;
  icon: IconDescriptor | null;
  onToggleCollapse: () => void;
  onEditingNameChange: (value: string) => void;
  onSaveName: () => void;
  onCancelEdit: () => void;
  onStartEdit: () => void;
  onContextMenu: (event: MouseEvent<HTMLSpanElement>) => void;
  onMenuClick: (event: MouseEvent<HTMLButtonElement>) => void;
  onAddTab: (event: MouseEvent<HTMLButtonElement>) => void;
  onDelete?: (event: MouseEvent<HTMLButtonElement>) => void;
}

export function SectionHeader({
  section,
  isCollapsed,
  isEditing,
  editingName,
  icon,
  onToggleCollapse,
  onEditingNameChange,
  onSaveName,
  onCancelEdit,
  onStartEdit,
  onContextMenu,
  onMenuClick,
  onAddTab,
  onDelete,
}: SectionHeaderProps) {
  return (
    <div className="section-header" onClick={onToggleCollapse}>
      <span className="collapse-icon">{isCollapsed ? '▶' : '▼'}</span>
      {icon?.kind === 'lucide' ? (
        <LucideIcon
          id={icon.id}
          className="section-icon section-icon--lucide"
          title="Project icon"
        />
      ) : icon?.kind === 'img' ? (
        <img className="section-icon" src={icon.src} alt={section.name} title={section.name} />
      ) : null}
      {isEditing ? (
        <input
          type="text"
          value={editingName}
          onChange={(event) => onEditingNameChange(event.target.value)}
          onBlur={onSaveName}
          onKeyDown={(event) => {
            if (event.key === 'Enter') onSaveName();
            if (event.key === 'Escape') onCancelEdit();
          }}
          onClick={(event) => event.stopPropagation()}
          autoFocus
        />
      ) : (
        <span
          className="section-name"
          onDoubleClick={(event) => {
            event.stopPropagation();
            onStartEdit();
          }}
          onContextMenu={onContextMenu}
          title={section.path || 'Home Directory'}
        >
          {section.name}
        </span>
      )}
      <div className="section-actions">
        <button className="action-btn" onClick={onMenuClick} title="Project menu">
          ⋯
        </button>
        <button className="action-btn" onClick={onAddTab} title="New Terminal">
          +
        </button>
        {onDelete && (
          <button className="action-btn delete-btn" onClick={onDelete} title="Delete Project">
            ×
          </button>
        )}
      </div>
    </div>
  );
}
