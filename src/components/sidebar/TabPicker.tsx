import { useRef, useEffect, useState } from 'react';
import { invoke } from '@tauri-apps/api/core';
import type { SessionTool } from '../../store/terminalStore';
import { useOutsideClick } from '../hooks/useOutsideClick';
import type { PopoverPosition } from './types';
import type { ToolInfo } from '@/types/tools';

interface TabPickerProps {
  position: PopoverPosition;
  onSelect: (tool: SessionTool) => void;
  onClose: () => void;
}

// Built-in tool IDs that map to SessionTool strings (as const for type safety)
const BUILTIN_TOOL_IDS = ['claude', 'gemini', 'codex', 'openCode'] as const;
type BuiltinToolId = (typeof BUILTIN_TOOL_IDS)[number];

// Type guard to safely narrow tool.id to a valid builtin tool
const isBuiltinToolId = (id: string): id is BuiltinToolId => {
  return (BUILTIN_TOOL_IDS as readonly string[]).includes(id);
};

export function TabPicker({ position, onSelect, onClose }: TabPickerProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const [tools, setTools] = useState<ToolInfo[]>([]);
  const [isLoading, setIsLoading] = useState(true);

  useOutsideClick(containerRef, () => onClose(), true);

  useEffect(() => {
    const loadTools = async () => {
      try {
        const toolsList = await invoke<ToolInfo[]>('tools_list');
        // Filter to only enabled tools
        setTools(toolsList.filter((t) => t.enabled));
      } catch (err) {
        console.error('Failed to load tools:', err);
        // Fallback to empty list (shell will always be available)
        setTools([]);
      } finally {
        setIsLoading(false);
      }
    };

    loadTools();
  }, []);

  const handleSelect = (tool: ToolInfo) => {
    // Map to SessionTool type using type guard for safety
    if (tool.isBuiltin && isBuiltinToolId(tool.id)) {
      onSelect(tool.id);
    } else {
      onSelect({ custom: tool.id });
    }
  };

  // Separate built-in AI tools and custom tools
  const builtinTools = tools.filter((t) => t.isBuiltin);
  const customTools = tools.filter((t) => !t.isBuiltin);

  return (
    <div
      className="tab-picker"
      ref={containerRef}
      style={{ left: position.x, top: position.y }}
    >
      <div className="tab-picker-title">Create tab</div>

      {isLoading ? (
        <div className="tab-picker-loading">Loading...</div>
      ) : (
        <>
          {/* Shell option always first */}
          <button className="tab-picker-option" onClick={() => onSelect('shell')}>
            <span className="tab-picker-shell">S</span>
            <span className="tab-picker-label">Shell</span>
          </button>

          {/* Built-in AI tools */}
          {builtinTools.map((tool) => (
            <button
              key={tool.id}
              className="tab-picker-option"
              onClick={() => handleSelect(tool)}
            >
              {tool.icon ? (
                <img className="tab-picker-icon" src={tool.icon} alt={tool.name} />
              ) : (
                <span className="tab-picker-shell">{tool.name.slice(0, 1)}</span>
              )}
              <span className="tab-picker-label">{tool.name}</span>
            </button>
          ))}

          {/* Custom tools separator */}
          {customTools.length > 0 && <div className="tab-picker-separator" />}

          {/* Custom tools */}
          {customTools.map((tool) => (
            <button
              key={tool.id}
              className="tab-picker-option"
              onClick={() => handleSelect(tool)}
            >
              {tool.icon ? (
                <img className="tab-picker-icon" src={tool.icon} alt={tool.name} />
              ) : (
                <span className="tab-picker-shell">{tool.name.slice(0, 1)}</span>
              )}
              <span className="tab-picker-label">{tool.name}</span>
            </button>
          ))}
        </>
      )}
    </div>
  );
}
