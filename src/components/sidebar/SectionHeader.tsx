// ABOUTME: Renders a sidebar section header with icon, title, and actions.
// ABOUTME: Supports editing, collapse toggles, and drag handles for sections.
import type { MouseEvent } from 'react';
import { motion } from 'motion/react';
import type { Section } from '../../store/terminalStore';
import type { IconDescriptor } from './types';
import { LucideIcon } from './LucideIcon';
import { GripVertical } from 'lucide-react';

interface SectionHeaderProps {
  section: Section;
  isCollapsed: boolean;
  isEditing: boolean;
  editingName: string;
  icon: IconDescriptor | null;
  dragHandleProps?: Record<string, unknown>;
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
  dragHandleProps,
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
      {dragHandleProps && !section.isDefault && (
        <span className="drag-handle" {...dragHandleProps} onClick={(e) => e.stopPropagation()}>
          <GripVertical size={16} />
        </span>
      )}
      <motion.span
        className="collapse-icon"
        animate={{ rotate: isCollapsed ? 0 : 90 }}
        transition={{ duration: 0.2 }}
      >
        ▶
      </motion.span>
      {icon?.kind === 'lucide' ? (
        <LucideIcon
          id={icon.id}
          className="section-icon section-icon--lucide"
          title="Project icon"
        />
      ) : icon?.kind === 'img' ? (
        <img
          className={`section-icon ${icon.monochrome ? 'section-icon--mono' : ''}`}
          src={icon.src}
          alt={section.name}
          title={section.name}
        />
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
