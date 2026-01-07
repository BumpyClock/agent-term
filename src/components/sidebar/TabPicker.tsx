import { useRef, useEffect, useState, useCallback } from 'react';
import { invoke } from '@tauri-apps/api/core';
import { ChevronRight, Star } from 'lucide-react';
import type { SessionTool } from '../../store/terminalStore';
import { useOutsideClick } from '../hooks/useOutsideClick';
import type { PopoverPosition } from './types';
import type { ToolInfo } from '@/types/tools';
import type { ShellInfo } from '@/types/shells';

interface TabPickerProps {
  position: PopoverPosition;
  onSelect: (tool: SessionTool, options?: { command?: string; args?: string[]; icon?: string; title?: string }) => void;
  onClose: () => void;
}

const BUILTIN_TOOL_IDS = ['claude', 'gemini', 'codex', 'openCode'] as const;
type BuiltinToolId = (typeof BUILTIN_TOOL_IDS)[number];

const isBuiltinToolId = (id: string): id is BuiltinToolId => {
  return (BUILTIN_TOOL_IDS as readonly string[]).includes(id);
};

export function TabPicker({ position, onSelect, onClose }: TabPickerProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const submenuRef = useRef<HTMLDivElement>(null);
  const shellTriggerRef = useRef<HTMLButtonElement>(null);
  const [tools, setTools] = useState<ToolInfo[]>([]);
  const [shells, setShells] = useState<ShellInfo[]>([]);
  const [pinnedIds, setPinnedIds] = useState<string[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [showSubmenu, setShowSubmenu] = useState(false);
  const [submenuPosition, setSubmenuPosition] = useState<PopoverPosition | null>(null);

  useOutsideClick(containerRef, (event) => {
    if (submenuRef.current?.contains(event.target as Node)) return;
    onClose();
  }, true);

  useEffect(() => {
    const loadData = async () => {
      try {
        const [toolsList, shellsList, pinned] = await Promise.all([
          invoke<ToolInfo[]>('tools_list'),
          invoke<ShellInfo[]>('available_shells'),
          invoke<string[]>('get_pinned_shells'),
        ]);
        setTools(toolsList.filter((t) => t.enabled));
        setShells(shellsList);
        setPinnedIds(pinned);
      } catch (err) {
        console.error('Failed to load tools/shells:', err);
        setTools([]);
        setShells([]);
        setPinnedIds([]);
      } finally {
        setIsLoading(false);
      }
    };

    loadData();
  }, []);

  const handleSelectTool = (tool: ToolInfo) => {
    if (tool.isBuiltin && isBuiltinToolId(tool.id)) {
      onSelect(tool.id);
    } else {
      onSelect({ custom: tool.id }, {
        command: tool.command,
        args: tool.args,
        icon: tool.icon,
        title: tool.name,
      });
    }
  };

  const handleSelectShell = (shell: ShellInfo) => {
    onSelect('shell', {
      command: shell.command,
      args: shell.args,
      icon: shell.icon,
      title: shell.name,
    });
  };

  const handleTogglePin = useCallback(async (shellId: string) => {
    try {
      const newPinned = await invoke<string[]>('toggle_pin_shell', { shellId });
      setPinnedIds(newPinned);
    } catch (err) {
      console.error('Failed to toggle pin:', err);
    }
  }, []);

  const handlePinClick = (e: React.MouseEvent, shellId: string) => {
    e.stopPropagation();
    handleTogglePin(shellId);
  };

  const openSubmenu = () => {
    if (shellTriggerRef.current) {
      const rect = shellTriggerRef.current.getBoundingClientRect();
      setSubmenuPosition({ x: rect.right + 4, y: rect.top });
      setShowSubmenu(true);
    }
  };

  const closeSubmenu = () => {
    setShowSubmenu(false);
    setSubmenuPosition(null);
  };

  const builtinTools = tools.filter((t) => t.isBuiltin);
  const customTools = tools.filter((t) => !t.isBuiltin);
  const pinnedShells = shells.filter((s) => pinnedIds.includes(s.id));
  const nativeShells = shells.filter((s) => s.shellType === 'native');
  const wslShells = shells.filter((s) => s.shellType === 'wsl');

  return (
    <>
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
            {/* Pinned shells at top level */}
            {pinnedShells.map((shell) => (
              <button
                key={shell.id}
                className="tab-picker-option"
                onClick={() => handleSelectShell(shell)}
              >
                <img className="tab-picker-icon" src={shell.icon} alt={shell.name} />
                <span className="tab-picker-label">{shell.name}</span>
                <button
                  className="shell-pin-btn pinned"
                  onClick={(e) => handlePinClick(e, shell.id)}
                  title="Unpin from menu"
                >
                  <Star size={12} fill="currentColor" />
                </button>
              </button>
            ))}

            {pinnedShells.length > 0 && <div className="tab-picker-separator" />}

            {/* Shell submenu trigger */}
            <button
              ref={shellTriggerRef}
              className="tab-picker-option tab-picker-submenu-trigger"
              onMouseEnter={openSubmenu}
              onClick={openSubmenu}
            >
              <span className="tab-picker-shell">S</span>
              <span className="tab-picker-label">Shell</span>
              <ChevronRight size={14} className="tab-picker-chevron" />
            </button>

            {/* Built-in AI tools */}
            {builtinTools.map((tool) => (
              <button
                key={tool.id}
                className="tab-picker-option"
                onClick={() => handleSelectTool(tool)}
                onMouseEnter={closeSubmenu}
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
                onClick={() => handleSelectTool(tool)}
                onMouseEnter={closeSubmenu}
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

      {/* Shell Submenu */}
      {showSubmenu && submenuPosition && (
        <div
          className="shell-submenu"
          ref={submenuRef}
          style={{ left: submenuPosition.x, top: submenuPosition.y }}
          onMouseLeave={closeSubmenu}
        >
          {nativeShells.length > 0 && (
            <>
              <div className="shell-section-header">Native Shells</div>
              {nativeShells.map((shell) => (
                <button
                  key={shell.id}
                  className="shell-option"
                  onClick={() => handleSelectShell(shell)}
                >
                  <img
                    className="shell-option-icon"
                    src={shell.icon}
                    alt={shell.name}
                  />
                  <span className="shell-option-label">
                    {shell.name}
                    {shell.isDefault && (
                      <span className="shell-default-badge">default</span>
                    )}
                  </span>
                  <button
                    className={`shell-pin-btn ${pinnedIds.includes(shell.id) ? 'pinned' : ''}`}
                    onClick={(e) => handlePinClick(e, shell.id)}
                    title={pinnedIds.includes(shell.id) ? 'Unpin from menu' : 'Pin to menu'}
                  >
                    <Star size={12} fill={pinnedIds.includes(shell.id) ? 'currentColor' : 'none'} />
                  </button>
                </button>
              ))}
            </>
          )}

          {wslShells.length > 0 && (
            <>
              {nativeShells.length > 0 && <div className="shell-submenu-separator" />}
              <div className="shell-section-header">WSL Distributions</div>
              {wslShells.map((shell) => (
                <button
                  key={shell.id}
                  className="shell-option"
                  onClick={() => handleSelectShell(shell)}
                >
                  <img
                    className="shell-option-icon"
                    src={shell.icon}
                    alt={shell.name}
                  />
                  <span className="shell-option-label">{shell.name}</span>
                  <button
                    className={`shell-pin-btn ${pinnedIds.includes(shell.id) ? 'pinned' : ''}`}
                    onClick={(e) => handlePinClick(e, shell.id)}
                    title={pinnedIds.includes(shell.id) ? 'Unpin from menu' : 'Pin to menu'}
                  >
                    <Star size={12} fill={pinnedIds.includes(shell.id) ? 'currentColor' : 'none'} />
                  </button>
                </button>
              ))}
            </>
          )}

          {shells.length === 0 && (
            <div className="shell-submenu-empty">No shells detected</div>
          )}

          <div className="shell-submenu-hint">
            <Star size={10} /> Pin shells for quick access
          </div>
        </div>
      )}
    </>
  );
}
