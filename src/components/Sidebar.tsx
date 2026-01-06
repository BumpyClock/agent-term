import { useEffect, useRef, useState } from 'react';
import { invoke } from '@tauri-apps/api/core';
import { useTerminalStore, Section, Session, SessionStatus, SessionTool } from '../store/terminalStore';
import './Sidebar.css';

interface SearchResult {
  filePath: string;
  projectName: string;
  messageType: string;
  timestamp: string | null;
  snippet: string;
  matchPositions: [number, number][];
  score: number;
}

function getStatusTitle(status: SessionStatus): string {
  switch (status) {
    case 'running':
      return 'Running';
    case 'waiting':
      return 'Waiting for input';
    case 'idle':
      return 'Idle';
    case 'error':
      return 'Error';
    case 'starting':
      return 'Starting';
    default:
      return 'Unknown';
  }
}

function needsAttention(status: SessionStatus): boolean {
  return status === 'waiting' || status === 'error';
}

function highlightMatches(text: string, matches: [number, number][]): React.ReactNode {
  if (!matches || matches.length === 0) {
    return text;
  }

  const parts: React.ReactNode[] = [];
  let lastIndex = 0;

  matches.forEach(([start, end], idx) => {
    if (start > lastIndex) {
      parts.push(text.slice(lastIndex, start));
    }
    parts.push(
      <mark key={idx} className="search-highlight">
        {text.slice(start, end)}
      </mark>
    );
    lastIndex = end;
  });

  if (lastIndex < text.length) {
    parts.push(text.slice(lastIndex));
  }

  return parts;
}

interface SidebarProps {
  onCreateTerminal: (sectionId: string, tool: SessionTool) => void;
}

export function Sidebar({ onCreateTerminal }: SidebarProps) {
  const {
    sections,
    sessions,
    activeSessionId,
    addSection,
    removeSection,
    updateSection,
    toggleSectionCollapse,
    removeSession,
    setActiveSession,
    updateSessionTitle,
    updateSessionCommand,
    updateSessionIcon,
  } = useTerminalStore();

  const [isAddingSection, setIsAddingSection] = useState(false);
  const [newSectionName, setNewSectionName] = useState('');
  const [newSectionPath, setNewSectionPath] = useState('');
  const [editingSectionId, setEditingSectionId] = useState<string | null>(null);
  const [editingName, setEditingName] = useState('');
  const [editingSessionId, setEditingSessionId] = useState<string | null>(null);
  const [editingSessionTitle, setEditingSessionTitle] = useState('');
  const [tabPickerSectionId, setTabPickerSectionId] = useState<string | null>(null);
  const tabPickerRef = useRef<HTMLDivElement | null>(null);
  const [menuSessionId, setMenuSessionId] = useState<string | null>(null);
  const [menuPosition, setMenuPosition] = useState<{ x: number; y: number } | null>(null);
  const menuRef = useRef<HTMLDivElement | null>(null);
  const [menuSectionId, setMenuSectionId] = useState<string | null>(null);
  const [menuSectionPosition, setMenuSectionPosition] = useState<{ x: number; y: number } | null>(null);
  const sectionMenuRef = useRef<HTMLDivElement | null>(null);
  const [editSessionId, setEditSessionId] = useState<string | null>(null);
  const [editTitle, setEditTitle] = useState('');
  const [editCommand, setEditCommand] = useState('');
  const [editIcon, setEditIcon] = useState<string | null>(null);
  const [editSectionId, setEditSectionId] = useState<string | null>(null);
  const [editSectionName, setEditSectionName] = useState('');
  const [editSectionPath, setEditSectionPath] = useState('');
  const [editSectionIcon, setEditSectionIcon] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [searchResults, setSearchResults] = useState<SearchResult[]>([]);
  const [isSearching, setIsSearching] = useState(false);
  const searchRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    invoke('search_reindex').catch((err) => {
      console.error('Failed to reindex search:', err);
    });
  }, []);

  useEffect(() => {
    if (!searchQuery.trim()) {
      setSearchResults([]);
      return;
    }

    setIsSearching(true);
    const timeoutId = setTimeout(() => {
      invoke<SearchResult[]>('search_query', { query: searchQuery, limit: 10 })
        .then((results) => {
          setSearchResults(results);
        })
        .catch((err) => {
          console.error('Search failed:', err);
          setSearchResults([]);
        })
        .finally(() => {
          setIsSearching(false);
        });
    }, 300);

    return () => {
      clearTimeout(timeoutId);
      setIsSearching(false);
    };
  }, [searchQuery]);

  useEffect(() => {
    if (searchResults.length === 0) return;
    const handleMouseDown = (event: MouseEvent) => {
      if (!searchRef.current) return;
      if (!searchRef.current.contains(event.target as Node)) {
        setSearchResults([]);
        setSearchQuery('');
      }
    };
    window.addEventListener('mousedown', handleMouseDown);
    return () => {
      window.removeEventListener('mousedown', handleMouseDown);
    };
  }, [searchResults]);

  useEffect(() => {
    if (!tabPickerSectionId) return;
    const handleMouseDown = (event: MouseEvent) => {
      if (!tabPickerRef.current) return;
      if (!tabPickerRef.current.contains(event.target as Node)) {
        setTabPickerSectionId(null);
      }
    };
    window.addEventListener('mousedown', handleMouseDown);
    return () => {
      window.removeEventListener('mousedown', handleMouseDown);
    };
  }, [tabPickerSectionId]);

  useEffect(() => {
    if (!menuSessionId) return;
    const handleMouseDown = (event: MouseEvent) => {
      if (!menuRef.current) return;
      if (!menuRef.current.contains(event.target as Node)) {
        setMenuSessionId(null);
        setMenuPosition(null);
      }
    };
    window.addEventListener('mousedown', handleMouseDown);
    return () => {
      window.removeEventListener('mousedown', handleMouseDown);
    };
  }, [menuSessionId]);

  useEffect(() => {
    if (!menuSectionId) return;
    const handleMouseDown = (event: MouseEvent) => {
      if (!sectionMenuRef.current) return;
      if (!sectionMenuRef.current.contains(event.target as Node)) {
        setMenuSectionId(null);
        setMenuSectionPosition(null);
      }
    };
    window.addEventListener('mousedown', handleMouseDown);
    return () => {
      window.removeEventListener('mousedown', handleMouseDown);
    };
  }, [menuSectionId]);

  const handleAddSection = async () => {
    if (newSectionName.trim() && newSectionPath.trim()) {
      await addSection(newSectionName.trim(), newSectionPath.trim());
      setNewSectionName('');
      setNewSectionPath('');
      setIsAddingSection(false);
    }
  };

  const handleStartEdit = (section: Section) => {
    if (section.isDefault) return;
    setEditingSectionId(section.id);
    setEditingName(section.name);
  };

  const handleSaveEdit = (sectionId: string) => {
    if (editingName.trim()) {
      updateSection(sectionId, { name: editingName.trim() });
    }
    setEditingSectionId(null);
    setEditingName('');
  };

  const handleStartSessionEdit = (session: Session) => {
    setEditingSessionId(session.id);
    setEditingSessionTitle(session.title);
  };

  const handleSaveSessionEdit = async (sessionId: string) => {
    if (editingSessionTitle.trim()) {
      await updateSessionTitle(sessionId, editingSessionTitle.trim());
    }
    setEditingSessionId(null);
    setEditingSessionTitle('');
  };

  const getSessionsBySection = (sectionId: string): Session[] => {
    return sessions.filter((s) => s.sectionId === sectionId);
  };

  const toolOptions: Array<{
    tool: SessionTool;
    title: string;
    icon?: string;
  }> = [
    { tool: 'shell', title: 'Shell' },
    { tool: 'claude', title: 'Claude Code', icon: '/tool-icons/claude-logo.svg' },
    { tool: 'codex', title: 'Codex', icon: '/tool-icons/OpenAI.png' },
    { tool: 'openCode', title: 'OpenCode', icon: '/tool-icons/Visual_Studio_Code_1.35_icon.svg' },
    { tool: 'gemini', title: 'Gemini', icon: '/tool-icons/google-logo.svg' },
  ];

  const toolIconOptions = [
    { label: 'Anthropic', value: '/tool-icons/anthropic-logo.svg' },
    { label: 'Claude', value: '/tool-icons/claude-logo.svg' },
    { label: 'Cursor', value: '/tool-icons/cursor.svg' },
    { label: 'FastAPI', value: '/tool-icons/fastapi-seeklogo.svg' },
    { label: 'Google', value: '/tool-icons/google-logo.svg' },
    { label: 'Grok', value: '/tool-icons/Grok.png' },
    { label: 'MCP', value: '/tool-icons/mcp.svg' },
    { label: 'Ollama', value: '/tool-icons/Ollama.png' },
    { label: 'OpenAI', value: '/tool-icons/OpenAI.png' },
    { label: 'OpenRouter', value: '/tool-icons/OpenRouter.png' },
    { label: 'Python', value: '/tool-icons/Python-logo-notext.svg' },
    { label: 'React', value: '/tool-icons/React-icon.svg' },
    { label: 'VS Code', value: '/tool-icons/Visual_Studio_Code_1.35_icon.svg' },
    { label: 'Windsurf', value: '/tool-icons/windsurf-white-symbol.svg' },
  ];

  const lucideIcons = [
    {
      id: 'terminal',
      label: 'Terminal',
      svg: (
        <>
          <polyline points="4 17 10 11 4 5" />
          <line x1="12" y1="19" x2="20" y2="19" />
        </>
      ),
    },
    {
      id: 'code',
      label: 'Code',
      svg: (
        <>
          <polyline points="16 18 22 12 16 6" />
          <polyline points="8 6 2 12 8 18" />
        </>
      ),
    },
    {
      id: 'sparkles',
      label: 'Sparkles',
      svg: (
        <>
          <path d="M12 2l1.6 4.4L18 8l-4.4 1.6L12 14l-1.6-4.4L6 8l4.4-1.6L12 2z" />
          <path d="M5 16l0.8 2.2L8 19l-2.2 0.8L5 22l-0.8-2.2L2 19l2.2-0.8L5 16z" />
        </>
      ),
    },
    {
      id: 'bot',
      label: 'Bot',
      svg: (
        <>
          <rect x="3" y="6" width="18" height="12" rx="3" />
          <line x1="12" y1="3" x2="12" y2="6" />
          <circle cx="9" cy="12" r="1" />
          <circle cx="15" cy="12" r="1" />
        </>
      ),
    },
    {
      id: 'cpu',
      label: 'CPU',
      svg: (
        <>
          <rect x="7" y="7" width="10" height="10" rx="2" />
          <line x1="7" y1="1" x2="7" y2="5" />
          <line x1="17" y1="1" x2="17" y2="5" />
          <line x1="7" y1="19" x2="7" y2="23" />
          <line x1="17" y1="19" x2="17" y2="23" />
          <line x1="1" y1="7" x2="5" y2="7" />
          <line x1="1" y1="17" x2="5" y2="17" />
          <line x1="19" y1="7" x2="23" y2="7" />
          <line x1="19" y1="17" x2="23" y2="17" />
        </>
      ),
    },
    {
      id: 'zap',
      label: 'Zap',
      svg: (
        <path d="M13 2L3 14h7l-1 8 10-12h-7l1-8z" />
      ),
    },
  ];

  const getToolIcon = (tool: SessionTool): string | null => {
    if (typeof tool !== 'string') return null;
    const match = toolOptions.find((option) => option.tool === tool);
    return match?.icon ?? null;
  };

  const getToolTitle = (tool: SessionTool): string => {
    if (typeof tool !== 'string') return tool.custom;
    switch (tool) {
      case 'shell':
        return 'Shell';
      case 'claude':
        return 'Claude Code';
      case 'codex':
        return 'Codex';
      case 'openCode':
        return 'OpenCode';
      case 'gemini':
        return 'Gemini';
      default:
        return tool;
    }
  };

  const resolveSessionIcon = (session: Session) => {
    if (session.icon) {
      if (session.icon.startsWith('lucide:')) {
        return { kind: 'lucide', id: session.icon.slice('lucide:'.length) };
      }
      return { kind: 'img', src: session.icon };
    }
    const fallback = getToolIcon(session.tool);
    if (fallback) {
      return { kind: 'img', src: fallback };
    }
    return null;
  };

  const resolveSectionIcon = (section: Section) => {
    if (!section.icon) return null;
    if (section.icon.startsWith('lucide:')) {
      return { kind: 'lucide', id: section.icon.slice('lucide:'.length) };
    }
    return { kind: 'img', src: section.icon };
  };

  const handleRestartSession = async (session: Session) => {
    try {
      await invoke('restart_session', { id: session.id, rows: null, cols: null });
    } catch (err) {
      console.error('Failed to restart session:', err);
    } finally {
      setMenuSessionId(null);
      setMenuPosition(null);
    }
  };

  const openEditDialog = (session: Session) => {
    setEditSessionId(session.id);
    setEditTitle(session.title);
    setEditCommand(session.command);
    setEditIcon(session.icon ?? null);
    setMenuSessionId(null);
    setMenuPosition(null);
  };

  const closeEditDialog = () => {
    setEditSessionId(null);
    setEditTitle('');
    setEditCommand('');
    setEditIcon(null);
  };

  const saveEditDialog = async () => {
    if (!editSessionId) return;
    const session = sessions.find((s) => s.id === editSessionId);
    if (!session) {
      closeEditDialog();
      return;
    }
    const nextTitle = editTitle.trim();
    const nextCommand = editCommand.trim();
    if (nextTitle && nextTitle !== session.title) {
      await updateSessionTitle(session.id, nextTitle);
    }
    if (nextCommand && nextCommand !== session.command) {
      await updateSessionCommand(session.id, nextCommand);
    }
    if (editIcon !== session.icon) {
      await updateSessionIcon(session.id, editIcon);
    }
    closeEditDialog();
  };

  const openSectionEditDialog = (section: Section) => {
    setEditSectionId(section.id);
    setEditSectionName(section.name);
    setEditSectionPath(section.path);
    setEditSectionIcon(section.icon ?? null);
    setMenuSectionId(null);
    setMenuSectionPosition(null);
  };

  const closeSectionEditDialog = () => {
    setEditSectionId(null);
    setEditSectionName('');
    setEditSectionPath('');
    setEditSectionIcon(null);
  };

  const saveSectionEditDialog = () => {
    if (!editSectionId) return;
    const section = sections.find((s) => s.id === editSectionId);
    if (!section) {
      closeSectionEditDialog();
      return;
    }
    const nextName = editSectionName.trim();
    const nextPath = editSectionPath.trim();
    const updates: Partial<Section> = {};
    if (nextName && nextName !== section.name) {
      updates.name = nextName;
    }
    if (nextPath !== section.path) {
      updates.path = nextPath;
    }
    if (editSectionIcon !== section.icon) {
      updates.icon = editSectionIcon;
    }
    if (Object.keys(updates).length > 0) {
      updateSection(section.id, updates);
    }
    closeSectionEditDialog();
  };

  const openMenuAt = (sessionId: string, x: number, y: number) => {
    setMenuSectionId(null);
    setMenuSectionPosition(null);
    setMenuSessionId(sessionId);
    setMenuPosition({ x, y });
  };

  const openSectionMenuAt = (sectionId: string, x: number, y: number) => {
    setMenuSessionId(null);
    setMenuPosition(null);
    setMenuSectionId(sectionId);
    setMenuSectionPosition({ x, y });
  };

  const handleSearchResultClick = (result: SearchResult) => {
    console.log('Selected file:', result.filePath);
    setSearchResults([]);
    setSearchQuery('');
  };

  const defaultSection = sections.find((section) => section.isDefault);
  const nonDefaultSections = sections.filter((section) => !section.isDefault);

  return (
    <div className="sidebar">
      <div className="search-container" ref={searchRef}>
        <input
          type="text"
          className="search-input"
          placeholder="Search messages..."
          value={searchQuery}
          onChange={(e) => setSearchQuery(e.target.value)}
        />
        {searchQuery && searchResults.length > 0 && (
          <div className="search-results">
            {searchResults.map((result, idx) => (
              <div
                key={idx}
                className="search-result-item"
                onClick={() => handleSearchResultClick(result)}
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
        {searchQuery && !isSearching && searchResults.length === 0 && (
          <div className="search-results">
            <div className="search-no-results">No results</div>
          </div>
        )}
      </div>

      <div className="sidebar-header">
        <h2>Projects</h2>
        <button
          className="add-section-btn"
          onClick={() => setIsAddingSection(true)}
          title="Add Project"
        >
          +
        </button>
      </div>

      {isAddingSection && (
        <div className="add-section-form">
          <input
            type="text"
            placeholder="Project Name"
            value={newSectionName}
            onChange={(e) => setNewSectionName(e.target.value)}
            autoFocus
          />
          <input
            type="text"
            placeholder="Path (e.g., /home/user/project)"
            value={newSectionPath}
            onChange={(e) => setNewSectionPath(e.target.value)}
          />
          <div className="form-buttons">
            <button onClick={handleAddSection}>Add</button>
            <button onClick={() => setIsAddingSection(false)}>Cancel</button>
          </div>
        </div>
      )}

      <div className="sections-list">
        {nonDefaultSections.map((section) => {
          const isDefault = false;
          const isCollapsed = section.collapsed;
          return (
            <div key={section.id} className="section">
            <div
              className={`section-header ${isDefault ? 'default' : ''}`}
              onClick={() => {
                if (!isDefault) {
                  toggleSectionCollapse(section.id);
                }
              }}
            >
              <span className={`collapse-icon ${isDefault ? 'placeholder' : ''}`}>
                {isDefault ? '' : isCollapsed ? '▶' : '▼'}
              </span>
              {(() => {
                const icon = resolveSectionIcon(section);
                if (!icon) return null;
                if (icon.kind === 'lucide') {
                  const svg = lucideIcons.find((item) => item.id === icon.id)?.svg;
                  if (!svg) return null;
                  return (
                    <span className="section-icon section-icon--lucide" title="Project icon">
                      <svg viewBox="0 0 24 24" aria-hidden="true">
                        {svg}
                      </svg>
                    </span>
                  );
                }
                return (
                  <img
                    className="section-icon"
                    src={icon.src}
                    alt={section.name}
                    title={section.name}
                  />
                );
              })()}
              {editingSectionId === section.id ? (
                <input
                  type="text"
                  value={editingName}
                  onChange={(e) => setEditingName(e.target.value)}
                  onBlur={() => handleSaveEdit(section.id)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') handleSaveEdit(section.id);
                    if (e.key === 'Escape') setEditingSectionId(null);
                  }}
                  onClick={(e) => e.stopPropagation()}
                  autoFocus
                />
              ) : (
                <span
                  className="section-name"
                  onDoubleClick={() => handleStartEdit(section)}
                  onContextMenu={(event) => {
                    event.preventDefault();
                    event.stopPropagation();
                    openSectionMenuAt(section.id, event.clientX, event.clientY);
                  }}
                  title={section.path || 'Home Directory'}
                >
                  {section.name}
                </span>
              )}
              <div className="section-actions">
                <button
                  className="action-btn"
                  onClick={(e) => {
                    e.stopPropagation();
                    const rect = (e.currentTarget as HTMLButtonElement).getBoundingClientRect();
                    openSectionMenuAt(section.id, Math.round(rect.right), Math.round(rect.bottom));
                  }}
                  title="Project menu"
                >
                  ⋯
                </button>
                <button
                  className="action-btn"
                  onClick={(e) => {
                    e.stopPropagation();
                    setTabPickerSectionId((prev) => (prev === section.id ? null : section.id));
                  }}
                  title="New Terminal"
                >
                  +
                </button>
                {!section.isDefault && (
                  <button
                    className="action-btn delete-btn"
                    onClick={async (e) => {
                      e.stopPropagation();
                      if (
                        confirm(
                          `Delete project "${section.name}"? Terminals will be moved to Default.`
                        )
                      ) {
                        await removeSection(section.id);
                      }
                    }}
                    title="Delete Project"
                  >
                    ×
                  </button>
                )}
              </div>
            </div>

            {menuSectionId === section.id && menuSectionPosition && (
              <div
                className="tab-menu-popover"
                ref={sectionMenuRef}
                style={{ left: menuSectionPosition.x, top: menuSectionPosition.y }}
              >
                <button
                  className="tab-menu-item"
                  onClick={(e) => {
                    e.stopPropagation();
                    openSectionEditDialog(section);
                  }}
                >
                  Edit
                </button>
              </div>
            )}

            {tabPickerSectionId === section.id && (
              <div className="tab-picker" ref={tabPickerRef}>
                <div className="tab-picker-title">Create tab</div>
                {toolOptions.map((option) => (
                  <button
                    key={typeof option.tool === 'string' ? option.tool : option.tool.custom}
                    className="tab-picker-option"
                    onClick={() => {
                      onCreateTerminal(section.id, option.tool);
                      setTabPickerSectionId(null);
                    }}
                  >
                    {option.icon ? (
                      <img
                        className="tab-picker-icon"
                        src={option.icon}
                        alt={option.title}
                      />
                    ) : (
                      <span className="tab-picker-shell">{option.title.slice(0, 1)}</span>
                    )}
                    <span className="tab-picker-label">{option.title}</span>
                  </button>
                ))}
              </div>
            )}

            {!isCollapsed && (
              <div className="tabs-list">
                {getSessionsBySection(section.id).map((session) => (
                  <div
                    key={session.id}
                    className={`tab ${activeSessionId === session.id ? 'active' : ''} status-${session.status}`}
                    onClick={() => setActiveSession(session.id)}
                    onContextMenu={(event) => {
                      event.preventDefault();
                      openMenuAt(session.id, event.clientX, event.clientY);
                    }}
                  >
                    {(() => {
                      const icon = resolveSessionIcon(session);
                      if (!icon) return null;
                      if (icon.kind === 'lucide') {
                        const svg = lucideIcons.find((item) => item.id === icon.id)?.svg;
                        if (!svg) return null;
                        return (
                          <span className="tab-tool-icon tab-tool-icon--lucide" title="Custom icon">
                            <svg viewBox="0 0 24 24" aria-hidden="true">
                              {svg}
                            </svg>
                          </span>
                        );
                      }
                      return (
                        <img
                          className="tab-tool-icon"
                          src={icon.src}
                          alt={getToolTitle(session.tool)}
                          title={getToolTitle(session.tool)}
                        />
                      );
                    })()}
                    {editingSessionId === session.id ? (
                      <input
                        className="tab-title-input"
                        type="text"
                        value={editingSessionTitle}
                        onChange={(e) => setEditingSessionTitle(e.target.value)}
                        onBlur={() => handleSaveSessionEdit(session.id)}
                        onKeyDown={(e) => {
                          if (e.key === 'Enter') handleSaveSessionEdit(session.id);
                          if (e.key === 'Escape') {
                            setEditingSessionId(null);
                            setEditingSessionTitle('');
                          }
                        }}
                        onClick={(e) => e.stopPropagation()}
                        autoFocus
                      />
                    ) : (
                      <span className="tab-title-wrap">
                        <span
                          className="tab-title"
                          onDoubleClick={(e) => {
                            e.stopPropagation();
                            handleStartSessionEdit(session);
                          }}
                          title={session.title}
                        >
                          {session.title}
                        </span>
                        {needsAttention(session.status) && (
                          <span
                            className={`tab-status-dot status-${session.status}`}
                            title={getStatusTitle(session.status)}
                          />
                        )}
                      </span>
                    )}
                    <div className="tab-actions">
                      <button
                        className="tab-menu"
                        onClick={(e) => {
                          e.stopPropagation();
                          const rect = (e.currentTarget as HTMLButtonElement).getBoundingClientRect();
                          openMenuAt(session.id, Math.round(rect.right), Math.round(rect.bottom));
                        }}
                        title="Tab menu"
                        aria-label="Tab menu"
                      >
                        ⋯
                      </button>
                      <button
                        className="tab-close"
                        onClick={async (e) => {
                          e.stopPropagation();
                          await removeSession(session.id);
                        }}
                        title="Close Terminal"
                        aria-label="Close Terminal"
                      >
                        ×
                      </button>
                    </div>
                    {menuSessionId === session.id && menuPosition && (
                      <div
                        className="tab-menu-popover"
                        ref={menuRef}
                        style={{ left: menuPosition.x, top: menuPosition.y }}
                      >
                        <button
                          className="tab-menu-item"
                          onClick={(e) => {
                            e.stopPropagation();
                            handleRestartSession(session);
                          }}
                        >
                          Restart session
                        </button>
                        <button
                          className="tab-menu-item"
                          onClick={(e) => {
                            e.stopPropagation();
                            openEditDialog(session);
                          }}
                        >
                          Edit
                        </button>
                      </div>
                    )}
                  </div>
                ))}
                {getSessionsBySection(section.id).length === 0 && (
                  <div className="empty-section">No terminals</div>
                )}
              </div>
            )}
          </div>
          );
        })}
        {defaultSection && (
          <div className="default-tabs">
            <div className="tabs-list">
              {getSessionsBySection(defaultSection.id).map((session) => (
                <div
                  key={session.id}
                  className={`tab ${activeSessionId === session.id ? 'active' : ''} status-${session.status}`}
                  onClick={() => setActiveSession(session.id)}
                  onContextMenu={(event) => {
                    event.preventDefault();
                    openMenuAt(session.id, event.clientX, event.clientY);
                  }}
                >
                  {(() => {
                    const icon = resolveSessionIcon(session);
                    if (!icon) return null;
                    if (icon.kind === 'lucide') {
                      const svg = lucideIcons.find((item) => item.id === icon.id)?.svg;
                      if (!svg) return null;
                      return (
                        <span className="tab-tool-icon tab-tool-icon--lucide" title="Custom icon">
                          <svg viewBox="0 0 24 24" aria-hidden="true">
                            {svg}
                          </svg>
                        </span>
                      );
                    }
                    return (
                      <img
                        className="tab-tool-icon"
                        src={icon.src}
                        alt={getToolTitle(session.tool)}
                        title={getToolTitle(session.tool)}
                      />
                    );
                  })()}
                  {editingSessionId === session.id ? (
                    <input
                      className="tab-title-input"
                      type="text"
                      value={editingSessionTitle}
                      onChange={(e) => setEditingSessionTitle(e.target.value)}
                      onBlur={() => handleSaveSessionEdit(session.id)}
                      onKeyDown={(e) => {
                        if (e.key === 'Enter') handleSaveSessionEdit(session.id);
                        if (e.key === 'Escape') {
                          setEditingSessionId(null);
                          setEditingSessionTitle('');
                        }
                      }}
                      onClick={(e) => e.stopPropagation()}
                      autoFocus
                    />
                  ) : (
                    <span className="tab-title-wrap">
                      <span
                        className="tab-title"
                        onDoubleClick={(e) => {
                          e.stopPropagation();
                          handleStartSessionEdit(session);
                        }}
                        title={session.title}
                      >
                        {session.title}
                      </span>
                      {needsAttention(session.status) && (
                        <span
                          className={`tab-status-dot status-${session.status}`}
                          title={getStatusTitle(session.status)}
                        />
                      )}
                    </span>
                  )}
                  <div className="tab-actions">
                    <button
                      className="tab-menu"
                      onClick={(e) => {
                        e.stopPropagation();
                        const rect = (e.currentTarget as HTMLButtonElement).getBoundingClientRect();
                        openMenuAt(session.id, Math.round(rect.right), Math.round(rect.bottom));
                      }}
                      title="Tab menu"
                      aria-label="Tab menu"
                    >
                      ⋯
                    </button>
                    <button
                      className="tab-close"
                      onClick={(e) => {
                        e.stopPropagation();
                        removeSession(session.id);
                      }}
                      title="Close Terminal"
                      aria-label="Close Terminal"
                    >
                      ×
                    </button>
                  </div>
                  {menuSessionId === session.id && menuPosition && (
                    <div
                      className="tab-menu-popover"
                      ref={menuRef}
                      style={{ left: menuPosition.x, top: menuPosition.y }}
                    >
                      <button
                        className="tab-menu-item"
                        onClick={(e) => {
                          e.stopPropagation();
                          handleRestartSession(session);
                        }}
                      >
                        Restart session
                      </button>
                      <button
                        className="tab-menu-item"
                        onClick={(e) => {
                          e.stopPropagation();
                          openEditDialog(session);
                        }}
                      >
                        Edit
                      </button>
                    </div>
                  )}
                </div>
              ))}
            </div>
          </div>
        )}
      </div>
      {editSessionId && (
        <div className="dialog-overlay" onClick={closeEditDialog}>
          <div className="dialog" onClick={(e) => e.stopPropagation()}>
            <div className="dialog-title">Edit tab</div>
            <label className="dialog-label">
              Name
              <input
                type="text"
                value={editTitle}
                onChange={(e) => setEditTitle(e.target.value)}
                autoFocus
              />
            </label>
            <label className="dialog-label">
              Command
              <input
                type="text"
                value={editCommand}
                onChange={(e) => setEditCommand(e.target.value)}
                placeholder="e.g. /bin/zsh or claude"
              />
            </label>
            <div className="dialog-label">
              Icon
              <div className="dialog-icon-grid">
                <button
                  className={`dialog-icon-option ${editIcon === null ? 'active' : ''}`}
                  onClick={() => setEditIcon(null)}
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
                    className={`dialog-icon-option ${editIcon === icon.value ? 'active' : ''}`}
                    onClick={() => setEditIcon(icon.value)}
                    type="button"
                    title={icon.label}
                  >
                    <img src={icon.value} alt={icon.label} />
                  </button>
                ))}
              </div>
              <div className="dialog-subtitle">Lucide icons</div>
              <div className="dialog-icon-grid">
                {lucideIcons.map((icon) => (
                  <button
                    key={icon.id}
                    className={`dialog-icon-option ${editIcon === `lucide:${icon.id}` ? 'active' : ''}`}
                    onClick={() => setEditIcon(`lucide:${icon.id}`)}
                    type="button"
                    title={icon.label}
                  >
                    <svg viewBox="0 0 24 24" aria-hidden="true">
                      {icon.svg}
                    </svg>
                  </button>
                ))}
              </div>
            </div>
            <div className="dialog-actions">
              <button className="dialog-secondary" onClick={closeEditDialog}>
                Cancel
              </button>
              <button className="dialog-primary" onClick={saveEditDialog}>
                Save
              </button>
            </div>
          </div>
        </div>
      )}
      {editSectionId && (
        <div className="dialog-overlay" onClick={closeSectionEditDialog}>
          <div className="dialog" onClick={(e) => e.stopPropagation()}>
            <div className="dialog-title">Edit project</div>
            <label className="dialog-label">
              Name
              <input
                type="text"
                value={editSectionName}
                onChange={(e) => setEditSectionName(e.target.value)}
                autoFocus
              />
            </label>
            <label className="dialog-label">
              Working directory
              <input
                type="text"
                value={editSectionPath}
                onChange={(e) => setEditSectionPath(e.target.value)}
                placeholder="e.g. /Users/you/project"
              />
            </label>
            <div className="dialog-label">
              Icon
              <div className="dialog-icon-grid">
                <button
                  className={`dialog-icon-option ${editSectionIcon === null ? 'active' : ''}`}
                  onClick={() => setEditSectionIcon(null)}
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
                    className={`dialog-icon-option ${editSectionIcon === icon.value ? 'active' : ''}`}
                    onClick={() => setEditSectionIcon(icon.value)}
                    type="button"
                    title={icon.label}
                  >
                    <img src={icon.value} alt={icon.label} />
                  </button>
                ))}
              </div>
              <div className="dialog-subtitle">Lucide icons</div>
              <div className="dialog-icon-grid">
                {lucideIcons.map((icon) => (
                  <button
                    key={icon.id}
                    className={`dialog-icon-option ${editSectionIcon === `lucide:${icon.id}` ? 'active' : ''}`}
                    onClick={() => setEditSectionIcon(`lucide:${icon.id}`)}
                    type="button"
                    title={icon.label}
                  >
                    <svg viewBox="0 0 24 24" aria-hidden="true">
                      {icon.svg}
                    </svg>
                  </button>
                ))}
              </div>
            </div>
            <div className="dialog-actions">
              <button className="dialog-secondary" onClick={closeSectionEditDialog}>
                Cancel
              </button>
              <button className="dialog-primary" onClick={saveSectionEditDialog}>
                Save
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
