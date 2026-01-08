import { useEffect, useCallback, useRef, useState, type MouseEvent as ReactMouseEvent } from 'react';
import { invoke } from '@tauri-apps/api/core';
import { listen } from '@tauri-apps/api/event';
import { getVersion } from '@tauri-apps/api/app';
import { Sidebar } from './components/sidebar/Sidebar';
import { TitleBar } from './components/titlebar/TitleBar';
import { Terminal } from './components/Terminal';
import { WindowDropZone } from './components/WindowDropZone';
import { useTerminalStore, type SessionStatus, type SessionTool } from './store/terminalStore';
import { useTerminalSettings } from './store/terminalSettingsStore';
import { useUpdateStore, shouldCheckForUpdates } from './store/updateStore';
import { applyAccentColor } from '@/lib/accentColors';
import { getCurrentWindowId, isMainWindow, initializeWindow, type WindowRecord } from '@/lib/windowContext';
import './App.css';
import "overlayscrollbars/overlayscrollbars.css";


function App() {
  const {
    sections,
    sessions,
    sessionOrder,
    activeSessionId,
    activatedSessionIds,
    addSession,
    removeSession,
    updateSection,
    updateSessionStatus,
    updateToolSessionId,
    setActiveSession,
    markSessionActivated,
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
  const sidebarInset = 8;
  const sidebarGap = 16;
  const initializedRef = useRef(false);

  const [windowId] = useState(() => getCurrentWindowId());
  const [windowRecord, setWindowRecord] = useState<WindowRecord | null>(null);
  const isMain = isMainWindow();

  // Apply accent color on mount and when it changes
  const accentColor = useTerminalSettings((state) => state.accentColor);
  useEffect(() => {
    applyAccentColor(accentColor);
  }, [accentColor]);

  useEffect(() => {
    if (isMain) return;

    initializeWindow()
      .then((record) => {
        if (record) {
          setWindowRecord(record);
        }
      })
      .catch((err) => {
        console.error('Failed to initialize window:', err);
      });
  }, [isMain]);

  // Handle window close for secondary windows: merge sessions back to main
  useEffect(() => {
    if (isMain) return;

    let unlisten: (() => void) | null = null;

    listen('tauri://close-requested', async () => {
      try {
        // Merge this window's sessions to main before closing
        await invoke('merge_windows', {
          sourceWindowId: windowId,
          targetWindowId: 'main',
        });
      } catch (err) {
        console.error('Failed to merge window on close:', err);
      }
    })
      .then((unsub) => {
        unlisten = unsub;
      })
      .catch((err) => {
        console.error('Failed to listen for close-requested event:', err);
      });

    return () => {
      unlisten?.();
    };
  }, [isMain, windowId]);

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

      const currentSessionOrder = useTerminalStore.getState().sessionOrder;
      if (!cancelled && currentSessionOrder.length === 0) {
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
    let cancelled = false;

    listen<{ sessionId: string; status: SessionStatus }>('session-status', (event) => {
      updateSessionStatus(event.payload.sessionId, event.payload.status);
    })
      .then((unsub) => {
        if (cancelled) {
          unsub();
        } else {
          unlisten = unsub;
        }
      })
      .catch((err) => {
        console.error('Failed to listen to session-status:', err);
      });

    return () => {
      cancelled = true;
      unlisten?.();
    };
  }, [updateSessionStatus]);

  useEffect(() => {
    let unlisten: (() => void) | null = null;
    let cancelled = false;

    listen<{ sessionId: string; toolSessionId: string; tool: string }>('tool-session-id', (event) => {
      updateToolSessionId(event.payload.sessionId, event.payload.tool, event.payload.toolSessionId);
    })
      .then((unsub) => {
        if (cancelled) {
          unsub();
        } else {
          unlisten = unsub;
        }
      })
      .catch((err) => {
        console.error('Failed to listen to tool-session-id:', err);
      });

    return () => {
      cancelled = true;
      unlisten?.();
    };
  }, [updateToolSessionId]);

  useEffect(() => {
    sidebarWidthRef.current = sidebarWidth;
  }, [sidebarWidth]);

  // Mark the active session as activated when it changes
  useEffect(() => {
    if (activeSessionId) {
      markSessionActivated(activeSessionId);
    }
  }, [activeSessionId, markSessionActivated]);

  // Initialize update checking and fetch version
  useEffect(() => {
    const initializeUpdateCheck = async () => {
      const { loadSettings, checkForUpdate, setCurrentVersion } = useUpdateStore.getState();

      // Fetch and store current version
      try {
        const version = await getVersion();
        setCurrentVersion(version);
      } catch (err) {
        console.error('Failed to get app version:', err);
      }

      try {
        await loadSettings();
        const settings = useUpdateStore.getState().settings;

        if (shouldCheckForUpdates(settings)) {
          // Check in background, don't block app startup
          checkForUpdate().catch(console.error);
        }
      } catch (err) {
        console.error('Failed to initialize update check:', err);
      }
    };

    initializeUpdateCheck();
  }, []);

  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      const isMeta = event.metaKey || event.ctrlKey;
      if (!isMeta) return;

      if (event.key >= '1' && event.key <= '9') {
        event.preventDefault();
        const index = parseInt(event.key, 10) - 1;
        if (index < sessionOrder.length) {
          setActiveSession(sessionOrder[index]);
        }
        return;
      }

      if (event.key === '[' || event.key === '{') {
        event.preventDefault();
        if (sessionOrder.length === 0) return;
        const currentIndex = sessionOrder.indexOf(activeSessionId || '');
        const prevIndex = currentIndex <= 0 ? sessionOrder.length - 1 : currentIndex - 1;
        setActiveSession(sessionOrder[prevIndex]);
        return;
      }

      if (event.key === ']' || event.key === '}') {
        event.preventDefault();
        if (sessionOrder.length === 0) return;
        const currentIndex = sessionOrder.indexOf(activeSessionId || '');
        const nextIndex = currentIndex >= sessionOrder.length - 1 ? 0 : currentIndex + 1;
        setActiveSession(sessionOrder[nextIndex]);
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

      if (event.key === 'w' && !event.shiftKey) {
        event.preventDefault();
        if (activeSessionId) {
          removeSession(activeSessionId);
        }
        return;
      }

      if (event.key === 'k') {
        event.preventDefault();
        window.dispatchEvent(new CustomEvent('toggle-command-bar'));
        return;
      }

      if (event.key === 'n' && !event.shiftKey) {
        event.preventDefault();
        invoke('open_new_window', { title: 'New Window', sessionIds: [] })
          .catch((err) => {
            console.error('Failed to open new window:', err);
          });
        return;
      }

      if (event.key === 'N' && event.shiftKey) {
        event.preventDefault();
        if (activeSessionId) {
          const session = sessions[activeSessionId];
          if (session) {
            invoke('open_new_window', {
              title: session.title,
              sessionIds: [activeSessionId],
            })
              .catch((err) => {
                console.error('Failed to move tab to new window:', err);
              });
          }
        }
        return;
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => {
      window.removeEventListener('keydown', handleKeyDown);
    };
  }, [sessionOrder, activeSessionId, sessions, setActiveSession, getDefaultSection, addSession, removeSession]);

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
    async (sectionId: string, tool: SessionTool, options?: { command?: string; icon?: string; title?: string }) => {
      const section = sections.find((s) => s.id === sectionId);
      if (!section) return;
      await addSession(sectionId, { tool, ...options });
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

  const terminalPaddingLeft = sidebarWidth + sidebarInset + sidebarGap;

  return (
    <div className="app" data-window-id={windowId} data-is-main={isMain}>
      <TitleBar />
      <WindowDropZone />
      <div className="sidebar-shell" style={{ width: sidebarWidth }}>
        <div className="sidebar-wrapper">
          <Sidebar onCreateTerminal={handleCreateTerminal} />
        </div>
        <div
          className="sidebar-resizer"
          onMouseDown={handleResizeStart}
          style={{ left: sidebarWidth - 3 }}
        />
      </div>
      <div className="terminal-container" style={{ paddingLeft: terminalPaddingLeft }}>
        {sessionOrder.length === 0 ? (
          <div className="no-terminals">
            <p>No terminals open</p>
            <p>Click + on a project to create a new terminal</p>
          </div>
        ) : (
          sessionOrder
            .filter((sessionId) => activatedSessionIds.has(sessionId))
            .map((sessionId) => {
              const session = sessions[sessionId];
              if (!session) return null;
              const section = sections.find((item) => item.id === session.sectionId);
              return (
                <Terminal
                  key={sessionId}
                  id={sessionId}
                  sessionId={sessionId}
                  cwd={section?.path ?? ''}
                  isActive={activeSessionId === sessionId}
                />
              );
            })
            .filter(Boolean)
        )}
      </div>
    </div>
  );
}

export default App;
