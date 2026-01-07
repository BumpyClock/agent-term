// ABOUTME: Floating command bar for searching Claude messages and resuming sessions.
// ABOUTME: Supports keyboard navigation, Motion animations, and session resume via click.

import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { invoke } from '@tauri-apps/api/core';
import { AnimatePresence, motion } from 'motion/react';
import { Search } from 'lucide-react';
import type { SearchResult } from './types';
import { highlightMatches } from './utils';

interface CommandBarProps {
  isOpen: boolean;
  onClose: () => void;
  onSelectResult: (result: SearchResult) => void;
}

export function CommandBar({ isOpen, onClose, onSelectResult }: CommandBarProps) {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<SearchResult[]>([]);
  const [isSearching, setIsSearching] = useState(false);
  const [selectedIndex, setSelectedIndex] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const resultsRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (isOpen && inputRef.current) {
      inputRef.current.focus();
    }
    if (!isOpen) {
      setQuery('');
      setResults([]);
      setSelectedIndex(0);
    }
  }, [isOpen]);

  useEffect(() => {
    if (!query.trim()) {
      setResults([]);
      return;
    }

    setIsSearching(true);
    const timeoutId = setTimeout(() => {
      invoke<SearchResult[]>('search_query', { query, limit: 10 })
        .then((res) => {
          setResults(res);
          setSelectedIndex(0);
        })
        .catch((err) => {
          console.error('Command bar search failed:', err);
          setResults([]);
        })
        .finally(() => {
          setIsSearching(false);
        });
    }, 300);

    return () => {
      clearTimeout(timeoutId);
      setIsSearching(false);
    };
  }, [query]);

  useEffect(() => {
    if (resultsRef.current && results.length > 0) {
      const selectedEl = resultsRef.current.children[selectedIndex] as HTMLElement;
      if (selectedEl) {
        selectedEl.scrollIntoView({ block: 'nearest' });
      }
    }
  }, [selectedIndex, results.length]);

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setSelectedIndex((prev) => Math.min(prev + 1, results.length - 1));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setSelectedIndex((prev) => Math.max(prev - 1, 0));
    } else if (e.key === 'Enter' && results[selectedIndex]) {
      e.preventDefault();
      onSelectResult(results[selectedIndex]);
    } else if (e.key === 'Escape') {
      e.preventDefault();
      onClose();
    }
  };

  const handleBackdropClick = (e: React.MouseEvent) => {
    if (e.target === e.currentTarget) {
      onClose();
    }
  };

  if (!isOpen) return null;

  return createPortal(
    <AnimatePresence>
      {isOpen && (
        <motion.div
          className="command-bar-overlay"
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={{ opacity: 0 }}
          transition={{ duration: 0.15 }}
          onClick={handleBackdropClick}
        >
          <motion.div
            className="command-bar"
            initial={{ opacity: 0, scale: 0.95, y: -10 }}
            animate={{ opacity: 1, scale: 1, y: 0 }}
            exit={{ opacity: 0, scale: 0.95, y: -10 }}
            transition={{ type: 'spring', stiffness: 300, damping: 30 }}
          >
            <div className="command-bar-input-wrapper">
              <Search size={18} className="command-bar-search-icon" />
              <input
                ref={inputRef}
                type="text"
                className="command-bar-input"
                placeholder="Search messages..."
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                onKeyDown={handleKeyDown}
              />
            </div>

            <div className="command-bar-results" ref={resultsRef}>
              {results.map((result, idx) => (
                <motion.div
                  key={`${result.filePath}-${idx}`}
                  className={`command-bar-item ${idx === selectedIndex ? 'selected' : ''}`}
                  initial={{ opacity: 0, x: -10 }}
                  animate={{ opacity: 1, x: 0 }}
                  transition={{ delay: idx * 0.03 }}
                  onClick={() => onSelectResult(result)}
                >
                  <div className="command-bar-item-meta">
                    <span className="command-bar-item-project">{result.projectName}</span>
                    <span className="command-bar-item-type">{result.messageType}</span>
                  </div>
                  <div className="command-bar-item-snippet">
                    {highlightMatches(result.snippet, result.matchPositions)}
                  </div>
                </motion.div>
              ))}

              {query && !isSearching && results.length === 0 && (
                <div className="command-bar-empty">No results found</div>
              )}

              {!query && (
                <div className="command-bar-empty">Type to search Claude messages...</div>
              )}
            </div>

            <div className="command-bar-hint">
              <span><kbd>↑</kbd><kbd>↓</kbd> navigate</span>
              <span><kbd>Enter</kbd> select</span>
              <span><kbd>Esc</kbd> close</span>
            </div>
          </motion.div>
        </motion.div>
      )}
    </AnimatePresence>,
    document.body
  );
}
