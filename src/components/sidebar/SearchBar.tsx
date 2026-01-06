import { useRef } from 'react';
import { useOutsideClick } from '../hooks/useOutsideClick';
import type { SearchResult } from './types';
import { highlightMatches } from './utils';

interface SearchBarProps {
  query: string;
  results: SearchResult[];
  isSearching: boolean;
  onQueryChange: (value: string) => void;
  onSelectResult: (result: SearchResult) => void;
  onClear: () => void;
}

export function SearchBar({
  query,
  results,
  isSearching,
  onQueryChange,
  onSelectResult,
  onClear,
}: SearchBarProps) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const hasResults = results.length > 0;

  useOutsideClick(containerRef, () => onClear(), hasResults);

  return (
    <div className="search-container" ref={containerRef}>
      <input
        type="text"
        className="search-input"
        placeholder="Search messages..."
        value={query}
        onChange={(event) => onQueryChange(event.target.value)}
      />
      {query && hasResults && (
        <div className="search-results">
          {results.map((result, idx) => (
            <div
              key={idx}
              className="search-result-item"
              onClick={() => onSelectResult(result)}
            >
              <div className="search-result-meta">
                <span className="search-result-project">{result.projectName}</span>
                <span className="search-result-type">{result.messageType}</span>
              </div>
              <div className="search-result-snippet">
                {highlightMatches(result.snippet, result.matchPositions)}
              </div>
            </div>
          ))}
        </div>
      )}
      {query && !isSearching && results.length === 0 && (
        <div className="search-results">
          <div className="search-no-results">No results</div>
        </div>
      )}
    </div>
  );
}
