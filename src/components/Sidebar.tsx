import { useState } from 'react';
import { useTerminalStore, Section, Tab } from '../store/terminalStore';
import './Sidebar.css';

interface SidebarProps {
  onCreateTerminal: (sectionId: string) => void;
}

export function Sidebar({ onCreateTerminal }: SidebarProps) {
  const {
    sections,
    tabs,
    activeTabId,
    addSection,
    removeSection,
    updateSection,
    toggleSectionCollapse,
    removeTab,
    setActiveTab,
  } = useTerminalStore();

  const [isAddingSection, setIsAddingSection] = useState(false);
  const [newSectionName, setNewSectionName] = useState('');
  const [newSectionPath, setNewSectionPath] = useState('');
  const [editingSectionId, setEditingSectionId] = useState<string | null>(null);
  const [editingName, setEditingName] = useState('');

  const handleAddSection = () => {
    if (newSectionName.trim() && newSectionPath.trim()) {
      addSection(newSectionName.trim(), newSectionPath.trim());
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

  const getTabsBySection = (sectionId: string): Tab[] => {
    return tabs.filter((t) => t.sectionId === sectionId);
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
        {sections.map((section) => (
          <div key={section.id} className="section">
            <div
              className="section-header"
              onClick={() => toggleSectionCollapse(section.id)}
            >
              <span className="collapse-icon">
                {section.isCollapsed ? '▶' : '▼'}
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
                    onClick={(e) => {
                      e.stopPropagation();
                      if (
                        confirm(
                          `Delete project "${section.name}"? Terminals will be moved to Default.`
                        )
                      ) {
                        removeSection(section.id);
                      }
                    }}
                    title="Delete Project"
                  >
                    ×
                  </button>
                )}
              </div>
            </div>

            {!section.isCollapsed && (
              <div className="tabs-list">
                {getTabsBySection(section.id).map((tab) => (
                  <div
                    key={tab.id}
                    className={`tab ${activeTabId === tab.id ? 'active' : ''}`}
                    onClick={() => setActiveTab(tab.id)}
                  >
                    <span className="tab-icon">⬛</span>
                    <span className="tab-title">{tab.title}</span>
                    <button
                      className="tab-close"
                      onClick={(e) => {
                        e.stopPropagation();
                        removeTab(tab.id);
                      }}
                      title="Close Terminal"
                    >
                      ×
                    </button>
                  </div>
                ))}
                {getTabsBySection(section.id).length === 0 && (
                  <div className="empty-section">No terminals</div>
                )}
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}
