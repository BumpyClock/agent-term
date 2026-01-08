// ABOUTME: Renders the sidebar with project sections, terminal tabs, and related menus.
// ABOUTME: Manages session selection, creation, and closure interactions for the UI.

import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { invoke } from '@tauri-apps/api/core';
import { Search, Settings } from 'lucide-react';
import { useTerminalStore, type Section, type Session, type SessionTool } from '../../store/terminalStore';
import { DndContext, DragOverlay, closestCenter, PointerSensor, KeyboardSensor, useSensor, useSensors, type DragStartEvent, type DragEndEvent, type DragCancelEvent } from '@dnd-kit/core';
import { SortableContext, sortableKeyboardCoordinates, verticalListSortingStrategy } from '@dnd-kit/sortable';
import { SortableSection, DragOverlayContent, type DragData, type DragItemType } from './dnd';
import { CommandBar } from './CommandBar';
import { EditProjectDialog } from './EditProjectDialog';
import { EditTabDialog } from './EditTabDialog';
import { MCPDialog } from './MCPDialog';
import { McpManagerDialog } from './McpManagerDialog';
import { MenuPopover } from './MenuPopover';
import { ProjectSection } from './ProjectSection';
import { SettingsDialog } from './SettingsDialog';
import { TabPicker } from './TabPicker';
import { TabsList } from './TabsList';
import { UpdateNotification } from './UpdateNotification';
import type { PopoverPosition, SearchResult } from './types';
import './Sidebar.css';

interface SidebarProps {
  onCreateTerminal: (sectionId: string, tool: SessionTool, options?: { command?: string; icon?: string; title?: string }) => void;
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
    setCustomTitle,
    clearCustomTitle,
    getSessionsBySection,
    reorderSessionsInSection,
    reorderSections,
    moveSessionToSectionAtIndex,
  } = useTerminalStore();
  const platformInfo =
    typeof navigator !== 'undefined'
      ? navigator.userAgent ?? 'unknown-platform'
      : 'unknown-platform';

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
  const [showMCPDialog, setShowMCPDialog] = useState(false);
  const [isCommandBarOpen, setIsCommandBarOpen] = useState(false);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [activeType, setActiveType] = useState<DragItemType | null>(null);
  const [collapsedBeforeDrag, setCollapsedBeforeDrag] = useState<Record<string, boolean>>({});

  useEffect(() => {
    invoke('search_reindex')
      .then(() => {
        console.info('Search index rebuilt');
      })
      .catch((err) => {
        console.error('Failed to reindex search:', err);
      });
  }, []);

  useEffect(() => {
    const handleToggleCommandBar = () => setIsCommandBarOpen((prev) => !prev);
    window.addEventListener('toggle-command-bar', handleToggleCommandBar);
    return () => window.removeEventListener('toggle-command-bar', handleToggleCommandBar);
  }, []);

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 8 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates })
  );

  const handleDragStart = (event: DragStartEvent) => {
    const data = event.active.data.current as DragData;
    setActiveId(event.active.id as string);
    setActiveType(data.type);

    if (data.type === 'section') {
      const states: Record<string, boolean> = {};
      sections.forEach((section) => {
        if (!section.isDefault) {
          states[section.id] = section.collapsed;
          if (!section.collapsed) {
            updateSection(section.id, { collapsed: true });
          }
        }
      });
      setCollapsedBeforeDrag(states);
    }
  };

  const handleDragEnd = (event: DragEndEvent) => {
    const { active, over } = event;
    const activeData = active.data.current as DragData;

    if (activeData.type === 'section' && Object.keys(collapsedBeforeDrag).length > 0) {
      Object.entries(collapsedBeforeDrag).forEach(([sectionId, wasCollapsed]) => {
        if (!wasCollapsed) {
          updateSection(sectionId, { collapsed: false });
        }
      });
      setCollapsedBeforeDrag({});
    }

    setActiveId(null);
    setActiveType(null);

    if (!over || active.id === over.id) return;

    const overData = over.data.current as DragData;

    if (activeData.type === 'section' && overData.type === 'section') {
      reorderSections(active.id as string, over.id as string);
    } else if (activeData.type === 'session' && overData.type === 'session') {
      if (activeData.sectionId === overData.sectionId) {
        reorderSessionsInSection(activeData.sectionId, active.id as string, over.id as string);
      } else {
        const targetSessions = getSessionsBySection(overData.sectionId);
        const overIndex = targetSessions.findIndex((s) => s.id === over.id);
        moveSessionToSectionAtIndex(active.id as string, overData.sectionId, overIndex);
      }
    }
  };

  const handleDragCancel = (_event: DragCancelEvent) => {
    if (Object.keys(collapsedBeforeDrag).length > 0) {
      Object.entries(collapsedBeforeDrag).forEach(([sectionId, wasCollapsed]) => {
        if (!wasCollapsed) {
          updateSection(sectionId, { collapsed: false });
        }
      });
      setCollapsedBeforeDrag({});
    }

    setActiveId(null);
    setActiveType(null);
  };

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
    const session = sessions[editSessionId];
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

  const handleSearchResultClick = async (result: SearchResult) => {
    const match = result.filePath.match(/\/([^/]+)\.jsonl$/);
    const claudeSessionId = match ? match[1] : null;

    if (claudeSessionId) {
      const existingSession = Object.values(sessions).find(
        (s) => s.claudeSessionId === claudeSessionId
      );

      if (existingSession) {
        setActiveSession(existingSession.id);
      } else {
        const defaultSect = sections[0];
        if (defaultSect) {
          try {
            const newSession = await invoke<{ id: string }>('create_session', {
              input: {
                sectionId: defaultSect.id,
                title: result.projectName || 'Claude Session',
                projectPath: defaultSect.path,
                tool: 'claude',
                command: `claude --resume ${claudeSessionId}`,
              },
            });
            if (newSession?.id) {
              setActiveSession(newSession.id);
            }
          } catch (err) {
            console.error('Failed to create resume session:', err);
          }
        }
      }
    }

    setIsCommandBarOpen(false);
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

  const nonDefaultSections = useMemo(
    () => sections.filter((s) => !s.isDefault),
    [sections]
  );
  const nonDefaultSectionIds = useMemo(
    () => nonDefaultSections.map((s) => s.id),
    [nonDefaultSections]
  );
  const defaultSection = useMemo(
    () => sections.find((s) => s.isDefault),
    [sections]
  );

  const menuSession = menuSessionId ? sessions[menuSessionId] : null;
  const menuSection = menuSectionId ? sections.find((s) => s.id === menuSectionId) : null;
  const mcpSession = mcpSessionId ? sessions[mcpSessionId] : null;
  const canManageMcp = (session: Session) => session.tool !== 'shell';

  return (
    <div className="sidebar">
      <DndContext
        sensors={sensors}
        collisionDetection={closestCenter}
        onDragStart={handleDragStart}
        onDragEnd={handleDragEnd}
        onDragCancel={handleDragCancel}
      >
        <div className="sidebar-header">
          <span className="sidebar-header-title">AGENT TERM</span>
          <div className="sidebar-header-actions">
            <button
              className="sidebar-header-btn"
              onClick={() => setIsCommandBarOpen(true)}
              title="Search (⌘K)"
              aria-label="Search"
            >
              <Search size={16} />
            </button>
            <button
              className="sidebar-header-btn"
              onClick={() => setShowMCPDialog(true)}
              title="MCP Servers"
              aria-label="MCP Servers"
            >
              <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor">
                <path d="M13.85 0a4.16 4.16 0 0 0-2.95 1.217L1.456 10.66a.835.835 0 0 0 0 1.18.835.835 0 0 0 1.18 0l9.442-9.442a2.49 2.49 0 0 1 3.541 0 2.49 2.49 0 0 1 0 3.541L8.59 12.97l-.1.1a.835.835 0 0 0 0 1.18.835.835 0 0 0 1.18 0l.1-.098 7.03-7.034a2.49 2.49 0 0 1 3.542 0l.049.05a2.49 2.49 0 0 1 0 3.54l-8.54 8.54a1.96 1.96 0 0 0 0 2.755l1.753 1.753a.835.835 0 0 0 1.18 0 .835.835 0 0 0 0-1.18l-1.753-1.753a.266.266 0 0 1 0-.394l8.54-8.54a4.185 4.185 0 0 0 0-5.9l-.05-.05a4.16 4.16 0 0 0-2.95-1.218c-.2 0-.401.02-.6.048a4.17 4.17 0 0 0-1.17-3.552A4.16 4.16 0 0 0 13.85 0m0 3.333a.84.84 0 0 0-.59.245L6.275 10.56a4.186 4.186 0 0 0 0 5.902 4.186 4.186 0 0 0 5.902 0L19.16 9.48a.835.835 0 0 0 0-1.18.835.835 0 0 0-1.18 0l-6.985 6.984a2.49 2.49 0 0 1-3.54 0 2.49 2.49 0 0 1 0-3.54l6.983-6.985a.835.835 0 0 0 0-1.18.84.84 0 0 0-.59-.245" />
              </svg>
            </button>
            <button
              className="sidebar-header-btn"
              onClick={() => setShowSettings(true)}
              title="Settings"
              aria-label="Settings"
            >
              <Settings size={16} />
            </button>
          </div>
        </div>

        <CommandBar
          isOpen={isCommandBarOpen}
          onClose={() => setIsCommandBarOpen(false)}
          onSelectResult={handleSearchResultClick}
        />

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
        {!isAddingSection && (
          <button
            className="add-project-btn"
            onClick={() => setIsAddingSection(true)}
            title="Add Project"
          >
            + Add Project
          </button>
        )}
        <SortableContext items={nonDefaultSectionIds} strategy={verticalListSortingStrategy}>
          {nonDefaultSections.map((section) => (
            <SortableSection
              key={section.id}
              sectionId={section.id}
              disabled={editingSectionId === section.id}
            >
              <ProjectSection
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
                  console.debug('[tab-close][sidebar] request', {
                    sessionId: session.id,
                    sectionId: section.id,
                    platform: platformInfo,
                  });
                  try {
                    await removeSession(session.id);
                    console.debug('[tab-close][sidebar] removed', {
                      sessionId: session.id,
                      sectionId: section.id,
                      platform: platformInfo,
                    });
                  } catch (err) {
                    console.error('[tab-close][sidebar] removeSession failed', {
                      sessionId: session.id,
                      sectionId: section.id,
                      platform: platformInfo,
                      error: err,
                    });
                  }
                }}
                onEditingTitleChange={setEditingSessionTitle}
                onSaveSessionEdit={handleSaveSessionEdit}
                onCancelSessionEdit={() => {
                  setEditingSessionId(null);
                  setEditingSessionTitle('');
                }}
                onStartSessionEdit={handleStartSessionEdit}
              />
            </SortableSection>
          ))}
        </SortableContext>
        {defaultSection && (
          <div className="section section-default">
            <div className="tabs-list">
              <TabsList
                sessions={getSessionsBySection(defaultSection.id)}
                activeSessionId={activeSessionId}
                editingSessionId={editingSessionId}
                editingSessionTitle={editingSessionTitle}
                sectionId={defaultSection.id}
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
                  console.debug('[tab-close][sidebar] request', {
                    sessionId: session.id,
                    sectionId: defaultSection.id,
                    platform: platformInfo,
                  });
                  removeSession(session.id).then(
                    () => {
                      console.debug('[tab-close][sidebar] removed', {
                        sessionId: session.id,
                        sectionId: defaultSection.id,
                        platform: platformInfo,
                      });
                    },
                    (err) => {
                      console.error('[tab-close][sidebar] removeSession failed', {
                        sessionId: session.id,
                        sectionId: defaultSection.id,
                        platform: platformInfo,
                        error: err,
                      });
                    }
                  );
                }}
              />
            </div>
          </div>
        )}
      </div>

        <UpdateNotification />

        <DragOverlay>
          {activeId && activeType && (
            <DragOverlayContent
              activeId={activeId}
              activeType={activeType}
              sessions={sessions}
              sections={sections}
            />
          )}
        </DragOverlay>
      </DndContext>

      {tabPickerSectionId && tabPickerPosition &&
        createPortal(
          <TabPicker
            position={tabPickerPosition}
            onSelect={(tool, options) => {
              onCreateTerminal(tabPickerSectionId, tool, options);
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
              {
                label: menuSession.isCustomTitle ? 'Use dynamic title' : 'Lock title',
                onSelect: async () => {
                  if (menuSession.isCustomTitle) {
                    await clearCustomTitle(menuSession.id);
                  } else {
                    await setCustomTitle(menuSession.id, menuSession.title, true);
                  }
                  closeMenuPopover();
                },
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
              ...(!menuSection.isDefault
                ? [
                    {
                      label: 'Remove project',
                      onSelect: async () => {
                        closeSectionMenuPopover();
                        await removeSection(menuSection.id);
                      },
                    },
                  ]
                : []),
            ]}
          />,
          document.body
        )}

      <EditTabDialog
        open={!!editSessionId}
        onOpenChange={(open) => !open && closeEditDialog()}
        titleValue={editTitle}
        commandValue={editCommand}
        iconValue={editIcon}
        onTitleChange={setEditTitle}
        onCommandChange={setEditCommand}
        onIconChange={setEditIcon}
        onSave={saveEditDialog}
      />

      <EditProjectDialog
        open={!!editSectionId}
        onOpenChange={(open) => !open && closeSectionEditDialog()}
        nameValue={editSectionName}
        pathValue={editSectionPath}
        iconValue={editSectionIcon}
        onNameChange={setEditSectionName}
        onPathChange={setEditSectionPath}
        onIconChange={setEditSectionIcon}
        onSave={saveSectionEditDialog}
      />

      {mcpSession &&
        createPortal(
          <McpManagerDialog session={mcpSession} onClose={closeMcpDialog} />,
          document.body
        )}

      {showSettings &&
        createPortal(
          <SettingsDialog onClose={() => setShowSettings(false)} />,
          document.body
        )}

      {showMCPDialog &&
        createPortal(
          <MCPDialog onClose={() => setShowMCPDialog(false)} />,
          document.body
        )}
    </div>
  );
}
