import { useRef } from 'react';
import { useOutsideClick } from '../hooks/useOutsideClick';
import type { PopoverPosition } from './types';

interface MenuItem {
  label: string;
  onSelect: () => void;
}

interface MenuPopoverProps {
  position: PopoverPosition;
  items: MenuItem[];
  onClose: () => void;
}

export function MenuPopover({ position, items, onClose }: MenuPopoverProps) {
  const menuRef = useRef<HTMLDivElement | null>(null);
  useOutsideClick(menuRef, () => onClose(), true);

  return (
    <div
      className="tab-menu-popover"
      ref={menuRef}
      style={{ left: position.x, top: position.y }}
    >
      {items.map((item) => (
        <button
          key={item.label}
          className="tab-menu-item"
          onClick={() => {
            item.onSelect();
            onClose();
          }}
        >
          {item.label}
        </button>
      ))}
    </div>
  );
}
