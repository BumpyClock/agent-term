import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { invoke } from '@tauri-apps/api/core';

export type SessionTool = 'shell' | 'claude' | 'gemini' | 'codex' | 'openCode' | { custom: string };
export type SessionStatus = 'running' | 'waiting' | 'idle' | 'error' | 'starting';

export interface Session {
  id: string;
  title: string;
  projectPath: string;
  sectionId: string;
  tool: SessionTool;
  command: string;
  icon: string | null;
  status: SessionStatus;
  createdAt: string;
  lastAccessedAt: string | null;
  claudeSessionId: string | null;
  geminiSessionId: string | null;
  loadedMcpNames: string[];
  isOpen: boolean;
  tabOrder: number | null;
}

export interface Section {
  id: string;
  name: string;
  path: string;
  icon: string | null;
  collapsed: boolean;
  order: number;
  isDefault?: boolean;
}

interface TerminalState {
  sections: Section[];
  sessions: Record<string, Session>;
  sessionsBySection: Record<string, string[]>;
  sessionOrder: string[];
  activeSessionId: string | null;
  activatedSessionIds: Set<string>;
  hasHydrated: boolean;
  lastKnownRows: number;
  lastKnownCols: number;

  addSection: (name: string, path: string) => Promise<Section>;
  removeSection: (id: string) => Promise<void>;
  updateSection: (id: string, updates: Partial<Section>) => void;
  toggleSectionCollapse: (id: string) => void;

  addSession: (
    sectionId: string,
    options?: { title?: string; tool?: SessionTool }
  ) => Promise<Session>;
  removeSession: (id: string) => Promise<void>;
  setActiveSession: (id: string) => void;
  updateSessionTitle: (id: string, title: string) => Promise<void>;
  updateSessionCommand: (id: string, command: string) => Promise<void>;
  updateSessionIcon: (id: string, icon: string | null) => Promise<void>;
  moveSessionToSection: (sessionId: string, sectionId: string) => Promise<void>;
  updateSessionStatus: (id: string, status: SessionStatus) => void;
  updateToolSessionId: (id: string, tool: string, toolSessionId: string) => void;
  setLastKnownSize: (rows: number, cols: number) => void;
  markSessionActivated: (id: string) => void;

  loadFromBackend: () => Promise<void>;
  setHasHydrated: (value: boolean) => void;

  getSession: (id: string) => Session | undefined;
  getAllSessions: () => Session[];
  getSessionsBySection: (sectionId: string) => Session[];
  getDefaultSection: () => Section | undefined;
}

const DEFAULT_SECTION_ID = 'default-section';

async function getDefaultShell(): Promise<string> {
  if (typeof window === 'undefined') {
    return '/bin/bash';
  }
  try {
    return await invoke<string>('get_default_shell');
  } catch (err) {
    console.error('Failed to get default shell:', err);
    return '/bin/bash';
  }
}

async function getCommandForTool(tool: SessionTool): Promise<string> {
  if (typeof tool === 'string') {
    switch (tool) {
      case 'shell':
        return getDefaultShell();
      case 'claude':
        return 'claude';
      case 'gemini':
        return 'gemini';
      case 'codex':
        return 'codex';
      case 'openCode':
        return 'opencode';
      default:
        return tool;
    }
  }
  return tool.custom;
}

function getToolTitle(tool: SessionTool): string {
  if (typeof tool === 'string') {
    switch (tool) {
      case 'shell':
        return 'Terminal';
      case 'claude':
        return 'Claude Code';
      case 'gemini':
        return 'Gemini';
      case 'codex':
        return 'Codex';
      case 'openCode':
        return 'OpenCode';
      default:
        return tool;
    }
  }
  return tool.custom;
}

export const useTerminalStore = create<TerminalState>()(
  persist(
    (set, get) => ({
      sections: [
        {
          id: DEFAULT_SECTION_ID,
          name: 'Default',
          path: '',
          icon: null,
          collapsed: false,
          order: 0,
          isDefault: true,
        },
      ],
      sessions: {},
      sessionsBySection: {},
      sessionOrder: [],
      activeSessionId: null,
      activatedSessionIds: new Set<string>(),
      hasHydrated: false,
      lastKnownRows: 24,
      lastKnownCols: 80,

      addSection: async (name: string, path: string) => {
        const section = await invoke<Section>('create_section', { name, path });
        set((state) => ({
          sections: [...state.sections, { ...section, isDefault: false }],
        }));
        return section;
      },

      removeSection: async (id: string) => {
        const section = get().sections.find((s) => s.id === id);
        if (section?.isDefault) return;
        await invoke('delete_section', { id });
        set((state) => {
          const defaultSection = state.sections.find((s) => s.isDefault);
          const defaultSectionId = defaultSection?.id || DEFAULT_SECTION_ID;
          const sessionIdsToMove = state.sessionsBySection[id] || [];

          const newSessions = { ...state.sessions };
          sessionIdsToMove.forEach((sessionId) => {
            if (newSessions[sessionId]) {
              newSessions[sessionId] = { ...newSessions[sessionId], sectionId: defaultSectionId };
            }
          });

          const newSessionsBySection = { ...state.sessionsBySection };
          delete newSessionsBySection[id];
          const defaultSessionIds = newSessionsBySection[defaultSectionId] || [];
          newSessionsBySection[defaultSectionId] = [...defaultSessionIds, ...sessionIdsToMove];

          return {
            sections: state.sections.filter((s) => s.id !== id),
            sessions: newSessions,
            sessionsBySection: newSessionsBySection,
          };
        });
      },

      updateSection: (id: string, updates: Partial<Section>) => {
        set((state) => ({
          sections: state.sections.map((s) =>
            s.id === id ? { ...s, ...updates } : s
          ),
        }));
        if (updates.name) {
          invoke('rename_section', { id, name: updates.name }).catch(console.error);
        }
        if (Object.prototype.hasOwnProperty.call(updates, 'path')) {
          invoke('set_section_path', { id, path: updates.path ?? '' }).catch(console.error);
        }
        if (Object.prototype.hasOwnProperty.call(updates, 'icon')) {
          invoke('set_section_icon', { id, icon: updates.icon ?? null }).catch(console.error);
        }
      },

      toggleSectionCollapse: (id: string) => {
        const section = get().sections.find((s) => s.id === id);
        if (section?.isDefault) return;
        set((state) => ({
          sections: state.sections.map((s) =>
            s.id === id ? { ...s, collapsed: !s.collapsed } : s
          ),
        }));
      },

      addSession: async (sectionId: string, options?: { title?: string; tool?: SessionTool }) => {
        const section = get().sections.find((s) => s.id === sectionId);
        const state = get();
        const sectionSessionIds = state.sessionsBySection[sectionId] || [];
        const sectionSessions = sectionSessionIds.map((id) => state.sessions[id]).filter(Boolean);
        const tool = options?.tool ?? 'shell';
        const toolTitle = getToolTitle(tool);
        const toolCount = sectionSessions.filter((s) =>
          typeof tool === 'string' && typeof s.tool === 'string'
            ? s.tool === tool
            : false
        ).length;
        const sessionTitle =
          options?.title ||
          (tool === 'shell'
            ? `Terminal ${sectionSessions.length + 1}`
            : toolCount === 0
              ? toolTitle
              : `${toolTitle} ${toolCount + 1}`);
        const projectPath = section?.path || '';
        const command = await getCommandForTool(tool);

        const session = await invoke<Session>('create_session', {
          input: {
            title: sessionTitle,
            projectPath,
            sectionId,
            tool,
            command,
            icon: null,
          },
        });

        set((state) => {
          const newActivated = new Set(state.activatedSessionIds);
          newActivated.add(session.id);

          const newSessionsBySection = { ...state.sessionsBySection };
          const sectionIds = newSessionsBySection[sectionId] || [];
          newSessionsBySection[sectionId] = [...sectionIds, session.id];

          return {
            sessions: { ...state.sessions, [session.id]: session },
            sessionsBySection: newSessionsBySection,
            sessionOrder: [...state.sessionOrder, session.id],
            activeSessionId: session.id,
            activatedSessionIds: newActivated,
          };
        });
        return session;
      },

      removeSession: async (id: string) => {
        await invoke('delete_session', { id });
        set((state) => {
          const session = state.sessions[id];
          if (!session) return state;

          const { [id]: removed, ...newSessions } = state.sessions;

          const newSessionsBySection = { ...state.sessionsBySection };
          const sectionIds = newSessionsBySection[session.sectionId] || [];
          newSessionsBySection[session.sectionId] = sectionIds.filter((sid) => sid !== id);

          const newSessionOrder = state.sessionOrder.filter((sid) => sid !== id);

          let newActiveSessionId = state.activeSessionId;
          if (state.activeSessionId === id) {
            const currentIndex = state.sessionOrder.indexOf(id);
            if (newSessionOrder.length > 0) {
              newActiveSessionId =
                newSessionOrder[Math.min(currentIndex, newSessionOrder.length - 1)] || null;
            } else {
              newActiveSessionId = null;
            }
          }

          const newActivated = new Set(state.activatedSessionIds);
          newActivated.delete(id);

          return {
            sessions: newSessions,
            sessionsBySection: newSessionsBySection,
            sessionOrder: newSessionOrder,
            activeSessionId: newActiveSessionId,
            activatedSessionIds: newActivated,
          };
        });
      },

      setActiveSession: (id: string) => {
        set({ activeSessionId: id });
        invoke('set_active_session', { id }).catch(console.error);
      },

      updateSessionTitle: async (id: string, title: string) => {
        await invoke('rename_session', { id, title });
        set((state) => {
          const session = state.sessions[id];
          if (!session) return state;
          return {
            sessions: { ...state.sessions, [id]: { ...session, title } },
          };
        });
      },

      updateSessionCommand: async (id: string, command: string) => {
        await invoke('set_session_command', { id, command });
        set((state) => {
          const session = state.sessions[id];
          if (!session) return state;
          return {
            sessions: { ...state.sessions, [id]: { ...session, command } },
          };
        });
      },

      updateSessionIcon: async (id: string, icon: string | null) => {
        await invoke('set_session_icon', { id, icon });
        set((state) => {
          const session = state.sessions[id];
          if (!session) return state;
          return {
            sessions: { ...state.sessions, [id]: { ...session, icon } },
          };
        });
      },

      moveSessionToSection: async (sessionId: string, sectionId: string) => {
        await invoke('move_session', { id: sessionId, sectionId });
        set((state) => {
          const session = state.sessions[sessionId];
          if (!session) return state;

          const oldSectionId = session.sectionId;
          const newSessionsBySection = { ...state.sessionsBySection };
          const oldSectionIds = newSessionsBySection[oldSectionId] || [];
          newSessionsBySection[oldSectionId] = oldSectionIds.filter((id) => id !== sessionId);
          const newSectionIds = newSessionsBySection[sectionId] || [];
          newSessionsBySection[sectionId] = [...newSectionIds, sessionId];

          return {
            sessions: { ...state.sessions, [sessionId]: { ...session, sectionId } },
            sessionsBySection: newSessionsBySection,
          };
        });
      },

      updateSessionStatus: (id: string, status: SessionStatus) => {
        set((state) => {
          const session = state.sessions[id];
          if (!session) return state;
          return {
            sessions: { ...state.sessions, [id]: { ...session, status } },
          };
        });
      },

      updateToolSessionId: (id: string, tool: string, toolSessionId: string) => {
        set((state) => {
          const session = state.sessions[id];
          if (!session) return state;
          let updated: Session;
          if (tool === 'claude') {
            updated = { ...session, claudeSessionId: toolSessionId };
          } else if (tool === 'gemini') {
            updated = { ...session, geminiSessionId: toolSessionId };
          } else {
            return state;
          }
          return {
            sessions: { ...state.sessions, [id]: updated },
          };
        });
        invoke('set_tool_session_id', { id, tool, toolSessionId }).catch(console.error);
      },

      setLastKnownSize: (rows: number, cols: number) => {
        if (rows <= 0 || cols <= 0) return;
        set({ lastKnownRows: rows, lastKnownCols: cols });
      },

      markSessionActivated: (id: string) => {
        set((state) => {
          if (state.activatedSessionIds.has(id)) return state;
          const newSet = new Set(state.activatedSessionIds);
          newSet.add(id);
          return { activatedSessionIds: newSet };
        });
      },

      loadFromBackend: async () => {
        try {
          const [sessionsArray, sections] = await Promise.all([
            invoke<Session[]>('list_sessions'),
            invoke<Section[]>('list_sections'),
          ]);

          const currentSections = get().sections;
          const hasDefaultSection = sections.some((s) =>
            currentSections.find((cs) => cs.isDefault && cs.id === s.id)
          );

          let mergedSections: Section[];
          if (hasDefaultSection || sections.length === 0) {
            mergedSections = sections.length > 0
              ? sections.map((s) => ({
                  ...s,
                  isDefault: currentSections.find((cs) => cs.id === s.id)?.isDefault || false,
                }))
              : currentSections;
          } else {
            mergedSections = [
              ...currentSections.filter((s) => s.isDefault),
              ...sections.map((s) => ({ ...s, isDefault: false })),
            ];
          }

          const sessionsMap: Record<string, Session> = {};
          const sessionsBySection: Record<string, string[]> = {};
          const sessionOrder: string[] = [];

          sessionsArray.forEach((session) => {
            sessionsMap[session.id] = session;
            sessionOrder.push(session.id);
            if (!sessionsBySection[session.sectionId]) {
              sessionsBySection[session.sectionId] = [];
            }
            sessionsBySection[session.sectionId].push(session.id);
          });

          const initialActiveId = sessionsArray.find((s) => s.isOpen)?.id || null;
          const initialActivated = initialActiveId ? new Set([initialActiveId]) : new Set<string>();
          set({
            sessions: sessionsMap,
            sessionsBySection,
            sessionOrder,
            sections: mergedSections,
            activeSessionId: initialActiveId,
            activatedSessionIds: initialActivated,
          });
        } catch (err) {
          console.error('Failed to load from backend:', err);
        }
      },

      setHasHydrated: (value: boolean) => {
        set({ hasHydrated: value });
      },

      getSession: (id: string) => {
        return get().sessions[id];
      },

      getAllSessions: () => {
        const state = get();
        return state.sessionOrder.map((id) => state.sessions[id]).filter(Boolean);
      },

      getSessionsBySection: (sectionId: string) => {
        const state = get();
        const sessionIds = state.sessionsBySection[sectionId] || [];
        return sessionIds.map((id) => state.sessions[id]).filter(Boolean);
      },

      getDefaultSection: () => {
        return get().sections.find((s) => s.isDefault);
      },
    }),
    {
      name: 'terminal-storage',
      partialize: (state) => ({
        sections: state.sections,
      }),
      onRehydrateStorage: () => (state) => {
        state?.setHasHydrated(true);
      },
    }
  )
);
