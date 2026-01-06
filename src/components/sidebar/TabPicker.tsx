import { useRef } from 'react';
import type { SessionTool } from '../../store/terminalStore';
import { useOutsideClick } from '../hooks/useOutsideClick';
import { toolOptions } from './constants';
import type { PopoverPosition } from './types';

interface TabPickerProps {
  position: PopoverPosition;
  onSelect: (tool: SessionTool) => void;
  onClose: () => void;
}

export function TabPicker({ position, onSelect, onClose }: TabPickerProps) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  useOutsideClick(containerRef, () => onClose(), true);

  return (
    <div
      className="tab-picker"
      ref={containerRef}
      style={{ left: position.x, top: position.y }}
    >
      <div className="tab-picker-title">Create tab</div>
      {toolOptions.map((option) => (
        <button
          key={typeof option.tool === 'string' ? option.tool : option.tool.custom}
          className="tab-picker-option"
          onClick={() => onSelect(option.tool)}
        >
          {option.icon ? (
            <img className="tab-picker-icon" src={option.icon} alt={option.title} />
          ) : (
            <span className="tab-picker-shell">{option.title.slice(0, 1)}</span>
          )}
          <span className="tab-picker-label">{option.title}</span>
        </button>
      ))}
    </div>
  );
}
