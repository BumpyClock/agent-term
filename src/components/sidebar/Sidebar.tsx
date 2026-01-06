import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { invoke } from '@tauri-apps/api/core';
import { useTerminalStore, type Section, type Session, type SessionTool } from '../../store/terminalStore';
import { EditProjectDialog } from './EditProjectDialog';
import { EditTabDialog } from './EditTabDialog';
import { McpManagerDialog } from './McpManagerDialog';
import { MenuPopover } from './MenuPopover';
import { ProjectSection } from './ProjectSection';
import { SearchBar } from './SearchBar';
import { SettingsDialog } from './SettingsDialog';
import { TabPicker } from './TabPicker';
import { TabsList } from './TabsList';
import type { PopoverPosition, SearchResult } from './types';
import './Sidebar.css';

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
  const [tabPickerPosition, setTabPickerPosition] = useState<PopoverPosition | null>(null);
  const [menuSessionId, setMenuSessionId] = useState<string | null>(null);
  const [menuPosition, setMenuPosition] = useState<PopoverPosition | null>(null);
  const [menuSectionId, setMenuSectionId] = useState<string | null>(null);
  const [menuSectionPosition, setMenuSectionPosition] = useState<PopoverPosition | null>(null);
  const [editSessionId, setEditSessionId] = useState<string | null>(null);
  const [editTitle, setEditTitle] = useState('');
  const [editCommand, setEditCommand] = useState('');
  const [editIcon, setEditIcon] = useState<string | null>(null);
  const [mcpSessionId, setMcpSessionId] = useState<string | null>(null);
  const [editSectionId, setEditSectionId] = useState<string | null>(null);
  const [editSectionName, setEditSectionName] = useState('');
  const [editSectionPath, setEditSectionPath] = useState('');
  const [editSectionIcon, setEditSectionIcon] = useState<string | null>(null);
  const [showSettings, setShowSettings] = useState(false);
  const [searchQuery, setSearchQuery] = useState('');
  const [searchResults, setSearchResults] = useState<SearchResult[]>([]);
  const [isSearching, setIsSearching] = useState(false);

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

  const handleSaveSessionEdit = async (session: Session) => {
    if (editingSessionTitle.trim()) {
      await updateSessionTitle(session.id, editingSessionTitle.trim());
    }
    setEditingSessionId(null);
    setEditingSessionTitle('');
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

  const openMcpDialog = (session: Session) => {
    setMcpSessionId(session.id);
    setMenuSessionId(null);
    setMenuPosition(null);
  };

  const closeMcpDialog = () => {
    setMcpSessionId(null);
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

  const getSessionsBySection = (sectionId: string): Session[] => {
    return sessions.filter((s) => s.sectionId === sectionId);
  };

  const openMenuAt = (sessionId: string, position: PopoverPosition) => {
    setMenuSectionId(null);
    setMenuSectionPosition(null);
    setMenuSessionId(sessionId);
    setMenuPosition(position);
  };

  const openSectionMenuAt = (sectionId: string, position: PopoverPosition) => {
    setMenuSessionId(null);
    setMenuPosition(null);
    setMenuSectionId(sectionId);
    setMenuSectionPosition(position);
  };

  const handleSearchResultClick = (result: SearchResult) => {
    console.log('Selected file:', result.filePath);
    setSearchResults([]);
    setSearchQuery('');
  };

  const closeTabPicker = () => {
    setTabPickerSectionId(null);
    setTabPickerPosition(null);
  };

  const closeMenuPopover = () => {
    setMenuSessionId(null);
    setMenuPosition(null);
  };

  const closeSectionMenuPopover = () => {
    setMenuSectionId(null);
    setMenuSectionPosition(null);
  };

  const defaultSection = sections.find((section) => section.isDefault);
  const nonDefaultSections = useMemo(
    () => sections.filter((section) => !section.isDefault),
    [sections]
  );

  const menuSession = menuSessionId ? sessions.find((s) => s.id === menuSessionId) : null;
  const menuSection = menuSectionId ? sections.find((s) => s.id === menuSectionId) : null;
  const mcpSession = mcpSessionId ? sessions.find((s) => s.id === mcpSessionId) : null;
  const canManageMcp = (session: Session) => session.tool !== 'shell';

  return (
    <div className="sidebar">
      <SearchBar
        query={searchQuery}
        results={searchResults}
        isSearching={isSearching}
        onQueryChange={setSearchQuery}
        onSelectResult={handleSearchResultClick}
        onClear={() => {
          setSearchResults([]);
          setSearchQuery('');
        }}
      />

      <div className="sidebar-header">
        <h2>Projects</h2>
        <div className="sidebar-header-actions">
          <button
            className="settings-btn"
            onClick={() => setShowSettings(true)}
            title="Settings"
            aria-label="Settings"
          >
            ⚙
          </button>
          <button
            className="add-section-btn"
            onClick={() => setIsAddingSection(true)}
            title="Add Project"
          >
            +
          </button>
        </div>
      </div>

      {isAddingSection && (
        <div className="add-section-form">
          <input
            type="text"
            placeholder="Project Name"
            value={newSectionName}
            onChange={(event) => setNewSectionName(event.target.value)}
            autoFocus
          />
          <input
            type="text"
            placeholder="Path (e.g., /home/user/project)"
            value={newSectionPath}
            onChange={(event) => setNewSectionPath(event.target.value)}
          />
          <div className="form-buttons">
            <button onClick={handleAddSection}>Add</button>
            <button onClick={() => setIsAddingSection(false)}>Cancel</button>
          </div>
        </div>
      )}

      <div className="sections-list">
        {nonDefaultSections.map((section) => (
          <ProjectSection
            key={section.id}
            section={section}
            sessions={getSessionsBySection(section.id)}
            activeSessionId={activeSessionId}
            isCollapsed={section.collapsed}
            isEditing={editingSectionId === section.id}
            editingName={editingName}
            editingSessionId={editingSessionId}
            editingSessionTitle={editingSessionTitle}
            onEditingNameChange={setEditingName}
            onSaveSectionEdit={() => handleSaveEdit(section.id)}
            onCancelSectionEdit={() => {
              setEditingSectionId(null);
              setEditingName('');
            }}
            onStartSectionEdit={() => handleStartEdit(section)}
            onToggleCollapse={() => toggleSectionCollapse(section.id)}
            onOpenSectionMenu={(event) => {
              event.stopPropagation();
              const rect = event.currentTarget.getBoundingClientRect();
              openSectionMenuAt(section.id, { x: Math.round(rect.right), y: Math.round(rect.bottom) });
            }}
            onOpenTabPicker={(event) => {
              event.stopPropagation();
              const rect = event.currentTarget.getBoundingClientRect();
              setTabPickerPosition({ x: Math.round(rect.left), y: Math.round(rect.bottom + 6) });
              setTabPickerSectionId((prev) => (prev === section.id ? null : section.id));
            }}
            onDeleteSection={async (event) => {
              event.stopPropagation();
              if (
                confirm(`Delete project "${section.name}"? Terminals will be moved to Default.`)
              ) {
                await removeSection(section.id);
              }
            }}
            onSectionContextMenu={(event) => {
              event.preventDefault();
              event.stopPropagation();
              openSectionMenuAt(section.id, { x: event.clientX, y: event.clientY });
            }}
            onSelectSession={(session) => setActiveSession(session.id)}
            onSessionContextMenu={(session, event) => {
              event.preventDefault();
              openMenuAt(session.id, { x: event.clientX, y: event.clientY });
            }}
            onSessionMenuClick={(session, event) => {
              event.stopPropagation();
              const rect = event.currentTarget.getBoundingClientRect();
              openMenuAt(session.id, { x: Math.round(rect.right), y: Math.round(rect.bottom) });
            }}
            onCloseSession={async (session, event) => {
              event.stopPropagation();
              await removeSession(session.id);
            }}
            onEditingTitleChange={setEditingSessionTitle}
            onSaveSessionEdit={handleSaveSessionEdit}
            onCancelSessionEdit={() => {
              setEditingSessionId(null);
              setEditingSessionTitle('');
            }}
            onStartSessionEdit={handleStartSessionEdit}
          />
        ))}
        {defaultSection && (
          <div className="section section-default">
            <div className="tabs-list">
              <TabsList
                sessions={getSessionsBySection(defaultSection.id)}
                activeSessionId={activeSessionId}
                editingSessionId={editingSessionId}
                editingSessionTitle={editingSessionTitle}
                showEmpty={false}
                onEditingTitleChange={setEditingSessionTitle}
                onSaveSessionEdit={handleSaveSessionEdit}
                onCancelSessionEdit={() => {
                  setEditingSessionId(null);
                  setEditingSessionTitle('');
                }}
                onStartSessionEdit={handleStartSessionEdit}
                onSelectSession={(session) => setActiveSession(session.id)}
                onSessionContextMenu={(session, event) => {
                  event.preventDefault();
                  openMenuAt(session.id, { x: event.clientX, y: event.clientY });
                }}
                onMenuClick={(session, event) => {
                  event.stopPropagation();
                  const rect = event.currentTarget.getBoundingClientRect();
                  openMenuAt(session.id, { x: Math.round(rect.right), y: Math.round(rect.bottom) });
                }}
                onCloseSession={(session, event) => {
                  event.stopPropagation();
                  removeSession(session.id);
                }}
              />
            </div>
          </div>
        )}
      </div>

      {tabPickerSectionId && tabPickerPosition &&
        createPortal(
          <TabPicker
            position={tabPickerPosition}
            onSelect={(tool) => {
              onCreateTerminal(tabPickerSectionId, tool);
              closeTabPicker();
            }}
            onClose={closeTabPicker}
          />,
          document.body
        )}

      {menuSession && menuPosition &&
        createPortal(
          <MenuPopover
            position={menuPosition}
            onClose={closeMenuPopover}
            items={[
              ...(canManageMcp(menuSession)
                ? [
                    {
                      label: 'MCP Manager',
                      onSelect: () => openMcpDialog(menuSession),
                    },
                  ]
                : []),
              {
                label: 'Restart session',
                onSelect: () => handleRestartSession(menuSession),
              },
              {
                label: 'Edit',
                onSelect: () => openEditDialog(menuSession),
              },
            ]}
          />,
          document.body
        )}

      {menuSection && menuSectionPosition &&
        createPortal(
          <MenuPopover
            position={menuSectionPosition}
            onClose={closeSectionMenuPopover}
            items={[
              {
                label: 'Edit',
                onSelect: () => openSectionEditDialog(menuSection),
              },
            ]}
          />,
          document.body
        )}

      {editSessionId && (
        <EditTabDialog
          titleValue={editTitle}
          commandValue={editCommand}
          iconValue={editIcon}
          onTitleChange={setEditTitle}
          onCommandChange={setEditCommand}
          onIconChange={setEditIcon}
          onClose={closeEditDialog}
          onSave={saveEditDialog}
        />
      )}

      {editSectionId && (
        <EditProjectDialog
          nameValue={editSectionName}
          pathValue={editSectionPath}
          iconValue={editSectionIcon}
          onNameChange={setEditSectionName}
          onPathChange={setEditSectionPath}
          onIconChange={setEditSectionIcon}
          onClose={closeSectionEditDialog}
          onSave={saveSectionEditDialog}
        />
      )}

      {mcpSession && <McpManagerDialog session={mcpSession} onClose={closeMcpDialog} />}

      {showSettings &&
        createPortal(
          <SettingsDialog onClose={() => setShowSettings(false)} />,
          document.body
        )}
    </div>
  );
}
