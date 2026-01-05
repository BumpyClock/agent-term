import { useEffect, useCallback, useRef, useState, type MouseEvent as ReactMouseEvent } from 'react';
import { invoke } from '@tauri-apps/api/core';
import { Sidebar } from './components/Sidebar';
import { Terminal } from './components/Terminal';
import { useTerminalStore } from './store/terminalStore';
import './App.css';

function App() {
  const { sections, tabs, activeTabId, addTab, updateSection, getDefaultSection } =
    useTerminalStore();
  const initializedRef = useRef(false);
  const [sidebarWidth, setSidebarWidth] = useState(250);
  const sidebarWidthRef = useRef(250);
  const isResizingRef = useRef(false);
  const resizeStartXRef = useRef(0);
  const resizeStartWidthRef = useRef(250);
  const minSidebarWidth = 200;
  const maxSidebarWidth = 420;

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

  useEffect(() => {
    sidebarWidthRef.current = sidebarWidth;
  }, [sidebarWidth]);

  useEffect(() => {
    const handleMouseMove = (event: MouseEvent) => {
      if (!isResizingRef.current) return;
      const delta = event.clientX - resizeStartXRef.current;
      const nextWidth = Math.min(
        maxSidebarWidth,
        Math.max(minSidebarWidth, resizeStartWidthRef.current + delta)
      );
      setSidebarWidth(nextWidth);
    };

    const handleMouseUp = () => {
      if (!isResizingRef.current) return;
      isResizingRef.current = false;
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };

    window.addEventListener('mousemove', handleMouseMove);
    window.addEventListener('mouseup', handleMouseUp);
    return () => {
      window.removeEventListener('mousemove', handleMouseMove);
      window.removeEventListener('mouseup', handleMouseUp);
    };
  }, []);

  const handleCreateTerminal = useCallback(
    (sectionId: string) => {
      const section = sections.find((s) => s.id === sectionId);
      if (!section) return;

      addTab(sectionId);
    },
    [sections, addTab]
  );

  const handleResizeStart = useCallback(
    (event: ReactMouseEvent<HTMLDivElement>) => {
      isResizingRef.current = true;
      resizeStartXRef.current = event.clientX;
      resizeStartWidthRef.current = sidebarWidthRef.current;
      document.body.style.cursor = 'col-resize';
      document.body.style.userSelect = 'none';
    },
    []
  );

  return (
    <div className="app">
      <div className="sidebar-wrapper" style={{ width: sidebarWidth }}>
        <Sidebar onCreateTerminal={handleCreateTerminal} />
      </div>
      <div className="sidebar-resizer" onMouseDown={handleResizeStart} />
      <div className="terminal-container">
        {tabs.length === 0 ? (
          <div className="no-terminals">
            <p>No terminals open</p>
            <p>Click + on a project to create a new terminal</p>
          </div>
        ) : (
          tabs.map((tab) => {
            const section = sections.find((item) => item.id === tab.sectionId);
            return (
              <Terminal
                key={tab.id}
                id={tab.id}
                cwd={section?.path ?? ''}
                isActive={activeTabId === tab.id}
              />
            );
          })
        )}
      </div>
    </div>
  );
}

export default App;
