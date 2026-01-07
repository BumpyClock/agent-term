/**
 * Searchable modal for browsing and selecting Lucide icons.
 * Features search filtering and infinite scroll for performance.
 */

import { useState, useMemo, useCallback } from 'react';
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { DynamicLucideIcon } from './DynamicLucideIcon';
import { searchLucideIcons } from './lucideIconList';

interface LucideIconSearchModalProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onSelect: (iconId: string) => void;
  currentValue?: string | null;
}

const ICONS_PER_PAGE = 100;

export function LucideIconSearchModal({
  open,
  onOpenChange,
  onSelect,
  currentValue,
}: LucideIconSearchModalProps) {
  const [search, setSearch] = useState('');
  const [visibleCount, setVisibleCount] = useState(ICONS_PER_PAGE);

  const filteredIcons = useMemo(() => {
    return searchLucideIcons(search);
  }, [search]);

  const visibleIcons = useMemo(() => {
    return filteredIcons.slice(0, visibleCount);
  }, [filteredIcons, visibleCount]);

  const handleScroll = useCallback(
    (e: React.UIEvent<HTMLDivElement>) => {
      const { scrollTop, scrollHeight, clientHeight } = e.currentTarget;
      if (scrollHeight - scrollTop - clientHeight < 200) {
        setVisibleCount((prev) =>
          Math.min(prev + ICONS_PER_PAGE, filteredIcons.length)
        );
      }
    },
    [filteredIcons.length]
  );

  const handleSelect = (iconName: string) => {
    onSelect(`lucide:${iconName}`);
    onOpenChange(false);
    setSearch('');
    setVisibleCount(ICONS_PER_PAGE);
  };

  const handleOpenChange = (nextOpen: boolean) => {
    onOpenChange(nextOpen);
    if (!nextOpen) {
      setSearch('');
      setVisibleCount(ICONS_PER_PAGE);
    }
  };

  const currentIconName = currentValue?.startsWith('lucide:')
    ? currentValue.slice('lucide:'.length)
    : null;

  return (
    <Dialog open={open} onOpenChange={handleOpenChange}>
      <DialogContent className="lucide-search-modal">
        <DialogHeader>
          <DialogTitle>Choose Lucide Icon</DialogTitle>
        </DialogHeader>

        <input
          type="text"
          placeholder="Search icons..."
          value={search}
          onChange={(e) => {
            setSearch(e.target.value);
            setVisibleCount(ICONS_PER_PAGE);
          }}
          className="lucide-search-input"
          autoFocus
        />

        <div className="lucide-icon-grid-container" onScroll={handleScroll}>
          <div className="dialog-icon-grid">
            {visibleIcons.map((icon) => (
              <button
                key={icon.name}
                className={`dialog-icon-option ${
                  currentIconName === icon.name ? 'active' : ''
                }`}
                onClick={() => handleSelect(icon.name)}
                type="button"
                title={icon.displayName}
              >
                <DynamicLucideIcon name={icon.name} size={20} strokeWidth={2} />
              </button>
            ))}
          </div>

          {visibleIcons.length < filteredIcons.length && (
            <div className="lucide-icon-loading">Loading more icons...</div>
          )}

          {filteredIcons.length === 0 && (
            <div className="lucide-icon-empty">
              No icons found for "{search}"
            </div>
          )}
        </div>

        <div className="lucide-search-count">
          {filteredIcons.length.toLocaleString()} icons available
        </div>
      </DialogContent>
    </Dialog>
  );
}
