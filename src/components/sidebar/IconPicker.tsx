// ABOUTME: Provides icon selection UI for sidebar projects and sessions.
// ABOUTME: Lets users choose from bundled tool icons or Lucide icons.
import { useState } from 'react';
import { MoreHorizontal } from 'lucide-react';
import { lucideIcons } from './constants';
import { getToolIconOptions, isMonochromeToolIcon } from './useToolIcons';
import { LucideIconSearchModal } from './LucideIconSearchModal';

interface IconPickerProps {
  value: string | null;
  onChange: (nextValue: string | null) => void;
}

export function IconPicker({ value, onChange }: IconPickerProps) {
  const [lucideModalOpen, setLucideModalOpen] = useState(false);
  const toolIconOptions = getToolIconOptions();

  return (
    <div className="dialog-label">
      Icon
      <div className="dialog-icon-grid">
        <button
          className={`dialog-icon-option ${value === null ? 'active' : ''}`}
          onClick={() => onChange(null)}
          type="button"
        >
          Default
        </button>
      </div>
      <div className="dialog-subtitle">Tool icons</div>
      <div className="dialog-icon-grid">
        {toolIconOptions.map((icon) => (
          <button
            key={icon.value}
            className={`dialog-icon-option ${value === icon.value ? 'active' : ''}`}
            onClick={() => onChange(icon.value)}
            type="button"
            title={icon.label}
          >
            <img
              className={`dialog-icon-img ${isMonochromeToolIcon(icon.value) ? 'dialog-icon-img--mono' : ''}`}
              src={icon.value}
              alt={icon.label}
            />
          </button>
        ))}
      </div>
      <div className="dialog-subtitle">Lucide icons</div>
      <div className="dialog-icon-grid">
        {lucideIcons.map((icon) => (
          <button
            key={icon.id}
            className={`dialog-icon-option ${value === `lucide:${icon.id}` ? 'active' : ''}`}
            onClick={() => onChange(`lucide:${icon.id}`)}
            type="button"
            title={icon.label}
          >
            <svg viewBox="0 0 24 24" aria-hidden="true">
              {icon.svg}
            </svg>
          </button>
        ))}

        <button
          className="dialog-icon-option dialog-icon-more"
          onClick={() => setLucideModalOpen(true)}
          type="button"
          title="More icons..."
        >
          <MoreHorizontal size={20} />
        </button>
      </div>
      <LucideIconSearchModal
        open={lucideModalOpen}
        onOpenChange={setLucideModalOpen}
        onSelect={onChange}
        currentValue={value}
      />
    </div>
  );
}
