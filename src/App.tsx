import { useEffect, useCallback, useRef, useState } from 'react';
import { invoke } from '@tauri-apps/api/core';
import { Sidebar } from './components/Sidebar';
import { Terminal } from './components/Terminal';
import { useTerminalStore } from './store/terminalStore';
import './App.css';

interface TerminalInstance {
  id: string;
  sectionId: string;
  cwd: string;
}

function App() {
  const { sections, tabs, activeTabId, addTab, updateSection, getDefaultSection } =
    useTerminalStore();
  const [terminals, setTerminals] = useState<TerminalInstance[]>([]);
  const initializedRef = useRef(false);

  // Initialize default section with home directory
  useEffect(() => {
    const initDefaultSection = async () => {
      const defaultSection = getDefaultSection();
      if (defaultSection && !defaultSection.path) {
        try {
          const homeDir = await invoke<string | null>('get_home_dir');
          if (homeDir) {
            updateSection(defaultSection.id, { path: homeDir });
          }
        } catch (err) {
          console.error('Failed to get home dir:', err);
        }
      }
    };
    initDefaultSection();
  }, [getDefaultSection, updateSection]);

  // Create initial terminal on first load
  useEffect(() => {
    if (!initializedRef.current && tabs.length === 0) {
      initializedRef.current = true;
      const defaultSection = getDefaultSection();
      if (defaultSection) {
        handleCreateTerminal(defaultSection.id);
      }
    }
  }, [tabs.length, getDefaultSection]);

  const handleCreateTerminal = useCallback(
    (sectionId: string) => {
      const section = sections.find((s) => s.id === sectionId);
      if (!section) return;

      const tab = addTab(sectionId);
      const terminal: TerminalInstance = {
        id: tab.id,
        sectionId,
        cwd: section.path,
      };
      setTerminals((prev) => [...prev, terminal]);
    },
    [sections, addTab]
  );

  // Clean up terminals when tabs are removed
  useEffect(() => {
    setTerminals((prev) =>
      prev.filter((t) => tabs.some((tab) => tab.id === t.id))
    );
  }, [tabs]);

  return (
    <div className="app">
      <Sidebar onCreateTerminal={handleCreateTerminal} />
      <div className="terminal-container">
        {terminals.length === 0 ? (
          <div className="no-terminals">
            <p>No terminals open</p>
            <p>Click + on a project to create a new terminal</p>
          </div>
        ) : (
          terminals.map((terminal) => (
            <Terminal
              key={terminal.id}
              id={terminal.id}
              cwd={terminal.cwd}
              isActive={activeTabId === terminal.id}
            />
          ))
        )}
      </div>
    </div>
  );
}

export default App;
