import { useEffect, useCallback, useRef, useState, type MouseEvent as ReactMouseEvent } from 'react';
import { invoke } from '@tauri-apps/api/core';
import { listen } from '@tauri-apps/api/event';
import { Sidebar } from './components/Sidebar';
import { Terminal } from './components/Terminal';
import { useTerminalStore, type SessionStatus, type SessionTool } from './store/terminalStore';
import './App.css';

function App() {
  const {
    sections,
    sessions,
    activeSessionId,
    addSession,
    updateSection,
    updateSessionStatus,
    updateToolSessionId,
    setActiveSession,
    getDefaultSection,
    loadFromBackend,
    hasHydrated,
  } = useTerminalStore();

  const [sidebarWidth, setSidebarWidth] = useState(250);
  const sidebarWidthRef = useRef(250);
  const isResizingRef = useRef(false);
  const resizeStartXRef = useRef(0);
  const resizeStartWidthRef = useRef(250);
  const minSidebarWidth = 200;
  const maxSidebarWidth = 420;
  const initializedRef = useRef(false);

  useEffect(() => {
    if (!hasHydrated || initializedRef.current) return;
    initializedRef.current = true;
    let cancelled = false;

    const hydrateDefaults = async () => {
      await loadFromBackend();

      const defaultSection = getDefaultSection();
      if (!defaultSection) return;

      if (!defaultSection.path) {
        try {
          const homeDir = await invoke<string | null>('get_home_dir');
          if (!cancelled && homeDir) {
            updateSection(defaultSection.id, { path: homeDir });
          }
        } catch (err) {
          console.error('Failed to get home dir:', err);
        }
      }

      const currentSessions = useTerminalStore.getState().sessions;
      if (!cancelled && currentSessions.length === 0) {
        await addSession(defaultSection.id, { tool: 'shell' });
      }
    };

    hydrateDefaults();
    return () => {
      cancelled = true;
    };
  }, [addSession, getDefaultSection, hasHydrated, loadFromBackend, updateSection]);

  useEffect(() => {
    let unlisten: (() => void) | null = null;
    listen<{ sessionId: string; status: SessionStatus }>('session-status', (event) => {
      updateSessionStatus(event.payload.sessionId, event.payload.status);
    }).then((unsub) => {
      unlisten = unsub;
    });
    return () => {
      unlisten?.();
    };
  }, [updateSessionStatus]);

  useEffect(() => {
    let unlisten: (() => void) | null = null;
    listen<{ sessionId: string; toolSessionId: string; tool: string }>('tool-session-id', (event) => {
      updateToolSessionId(event.payload.sessionId, event.payload.tool, event.payload.toolSessionId);
    }).then((unsub) => {
      unlisten = unsub;
    });
    return () => {
      unlisten?.();
    };
  }, [updateToolSessionId]);

  useEffect(() => {
    sidebarWidthRef.current = sidebarWidth;
  }, [sidebarWidth]);

  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      const isMeta = event.metaKey || event.ctrlKey;
      if (!isMeta) return;

      if (event.key >= '1' && event.key <= '9') {
        event.preventDefault();
        const index = parseInt(event.key, 10) - 1;
        if (index < sessions.length) {
          setActiveSession(sessions[index].id);
        }
        return;
      }

      if (event.key === '[' || event.key === '{') {
        event.preventDefault();
        if (sessions.length === 0) return;
        const currentIndex = sessions.findIndex((s) => s.id === activeSessionId);
        const prevIndex = currentIndex <= 0 ? sessions.length - 1 : currentIndex - 1;
        setActiveSession(sessions[prevIndex].id);
        return;
      }

      if (event.key === ']' || event.key === '}') {
        event.preventDefault();
        if (sessions.length === 0) return;
        const currentIndex = sessions.findIndex((s) => s.id === activeSessionId);
        const nextIndex = currentIndex >= sessions.length - 1 ? 0 : currentIndex + 1;
        setActiveSession(sessions[nextIndex].id);
        return;
      }

      if (event.key === 't' && !event.shiftKey) {
        event.preventDefault();
        const defaultSection = getDefaultSection();
        if (defaultSection) {
          addSession(defaultSection.id, { tool: 'shell' });
        }
        return;
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => {
      window.removeEventListener('keydown', handleKeyDown);
    };
  }, [sessions, activeSessionId, setActiveSession, getDefaultSection, addSession]);

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
    async (sectionId: string, tool: SessionTool) => {
      const section = sections.find((s) => s.id === sectionId);
      if (!section) return;
      await addSession(sectionId, { tool });
    },
    [sections, addSession]
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
        {sessions.length === 0 ? (
          <div className="no-terminals">
            <p>No terminals open</p>
            <p>Click + on a project to create a new terminal</p>
          </div>
        ) : (
          sessions.map((session) => {
            const section = sections.find((item) => item.id === session.sectionId);
            return (
              <Terminal
                key={session.id}
                id={session.id}
                sessionId={session.id}
                cwd={section?.path ?? ''}
                isActive={activeSessionId === session.id}
              />
            );
          })
        )}
      </div>
    </div>
  );
}

export default App;
