import { useState } from 'react';
import { useTerminalStore, Section, Session, SessionStatus } from '../store/terminalStore';
import './Sidebar.css';

function getStatusIcon(status: SessionStatus): string {
  switch (status) {
    case 'running':
      return '🟢';
    case 'waiting':
      return '🟡';
    case 'idle':
      return '⚪';
    case 'error':
      return '🔴';
    case 'starting':
      return '🔵';
    default:
      return '⬛';
  }
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

interface SidebarProps {
  onCreateTerminal: (sectionId: string) => void;
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
  } = useTerminalStore();

  const [isAddingSection, setIsAddingSection] = useState(false);
  const [newSectionName, setNewSectionName] = useState('');
  const [newSectionPath, setNewSectionPath] = useState('');
  const [editingSectionId, setEditingSectionId] = useState<string | null>(null);
  const [editingName, setEditingName] = useState('');

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

  const getSessionsBySection = (sectionId: string): Session[] => {
    return sessions.filter((s) => s.sectionId === sectionId);
  };

  return (
    <div className="sidebar">
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
        {sections.map((section) => {
          const isDefault = !!section.isDefault;
          const isCollapsed = isDefault ? false : section.collapsed;
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
                  title={section.path || 'Home Directory'}
                >
                  {section.name}
                  {section.isDefault && (
                    <span className="default-badge">default</span>
                  )}
                </span>
              )}
              <div className="section-actions">
                <button
                  className="action-btn"
                  onClick={(e) => {
                    e.stopPropagation();
                    onCreateTerminal(section.id);
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

            {!isCollapsed && (
              <div className="tabs-list">
                {getSessionsBySection(section.id).map((session) => (
                  <div
                    key={session.id}
                    className={`tab ${activeSessionId === session.id ? 'active' : ''} status-${session.status}`}
                    onClick={() => setActiveSession(session.id)}
                  >
                    <span className="tab-icon" title={getStatusTitle(session.status)}>
                      {getStatusIcon(session.status)}
                    </span>
                    <span className="tab-title">{session.title}</span>
                    <button
                      className="tab-close"
                      onClick={async (e) => {
                        e.stopPropagation();
                        await removeSession(session.id);
                      }}
                      title="Close Terminal"
                    >
                      ×
                    </button>
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
      </div>
    </div>
  );
}
