// ABOUTME: Renders styled drag preview for sessions and sections during drag.
// ABOUTME: Shows a simplified visual representation of the dragged item.

import type { Section, Session } from '../../../store/terminalStore';
import type { DragItemType } from './types';
import { resolveSessionIcon, resolveSectionIcon } from '../utils';
import { LucideIcon } from '../LucideIcon';
import { getToolTitle } from '../utils';

interface DragOverlayContentProps {
  activeId: string;
  activeType: DragItemType;
  sessions: Record<string, Session>;
  sections: Section[];
}

export function DragOverlayContent({
  activeId,
  activeType,
  sessions,
  sections,
}: DragOverlayContentProps) {
  if (activeType === 'session') {
    const session = sessions[activeId];
    if (!session) return null;

    const icon = resolveSessionIcon(session);
    const toolTitle = getToolTitle(session.tool);
    const isMonochromeIcon = icon?.kind === 'img' && icon.monochrome;

    return (
      <div className="drag-overlay tab active">
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
        <span className="tab-title">{session.title}</span>
      </div>
    );
  }

  if (activeType === 'section') {
    const section = sections.find((s) => s.id === activeId);
    if (!section) return null;

    const icon = resolveSectionIcon(section);
    const isMonochromeIcon = icon?.kind === 'img' && icon.monochrome;

    return (
      <div className="drag-overlay section-header">
        {icon?.kind === 'lucide' ? (
          <LucideIcon
            id={icon.id}
            className="section-icon section-icon--lucide"
            title="Project icon"
          />
        ) : icon?.kind === 'img' ? (
          <img
            className={`section-icon ${isMonochromeIcon ? 'section-icon--mono' : ''}`}
            src={icon.src}
            alt={section.name}
            title={section.name}
          />
        ) : null}
        <span className="section-name">{section.name}</span>
      </div>
    );
  }

  return null;
}
