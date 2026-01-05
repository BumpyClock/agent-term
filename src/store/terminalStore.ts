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
  collapsed: boolean;
  order: number;
  isDefault?: boolean;
}

interface TerminalState {
  sections: Section[];
  sessions: Session[];
  activeSessionId: string | null;
  hasHydrated: boolean;

  addSection: (name: string, path: string) => Promise<Section>;
  removeSection: (id: string) => Promise<void>;
  updateSection: (id: string, updates: Partial<Section>) => void;
  toggleSectionCollapse: (id: string) => void;

  addSession: (sectionId: string, title?: string) => Promise<Session>;
  removeSession: (id: string) => Promise<void>;
  setActiveSession: (id: string) => void;
  updateSessionTitle: (id: string, title: string) => Promise<void>;
  moveSessionToSection: (sessionId: string, sectionId: string) => Promise<void>;
  updateSessionStatus: (id: string, status: SessionStatus) => void;
  updateToolSessionId: (id: string, tool: string, toolSessionId: string) => void;

  loadFromBackend: () => Promise<void>;
  setHasHydrated: (value: boolean) => void;

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

export const useTerminalStore = create<TerminalState>()(
  persist(
    (set, get) => ({
      sections: [
        {
          id: DEFAULT_SECTION_ID,
          name: 'Default',
          path: '',
          collapsed: false,
          order: 0,
          isDefault: true,
        },
      ],
      sessions: [],
      activeSessionId: null,
      hasHydrated: false,

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
          const updatedSessions = state.sessions.map((session) =>
            session.sectionId === id
              ? { ...session, sectionId: defaultSection?.id || DEFAULT_SECTION_ID }
              : session
          );
          return {
            sections: state.sections.filter((s) => s.id !== id),
            sessions: updatedSessions,
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

      addSession: async (sectionId: string, title?: string) => {
        const section = get().sections.find((s) => s.id === sectionId);
        const sessions = get().sessions;
        const sectionSessions = sessions.filter((s) => s.sectionId === sectionId);
        const sessionTitle = title || `Terminal ${sectionSessions.length + 1}`;
        const projectPath = section?.path || '';
        const shell = await getDefaultShell();

        const session = await invoke<Session>('create_session', {
          input: {
            title: sessionTitle,
            projectPath,
            sectionId,
            tool: 'shell',
            command: shell,
          },
        });

        set((state) => ({
          sessions: [...state.sessions, session],
          activeSessionId: session.id,
        }));
        return session;
      },

      removeSession: async (id: string) => {
        await invoke('delete_session', { id });
        set((state) => {
          const newSessions = state.sessions.filter((s) => s.id !== id);
          let newActiveSessionId = state.activeSessionId;
          if (state.activeSessionId === id) {
            const sessionIndex = state.sessions.findIndex((s) => s.id === id);
            if (newSessions.length > 0) {
              newActiveSessionId =
                newSessions[Math.min(sessionIndex, newSessions.length - 1)]?.id || null;
            } else {
              newActiveSessionId = null;
            }
          }
          return {
            sessions: newSessions,
            activeSessionId: newActiveSessionId,
          };
        });
      },

      setActiveSession: (id: string) => {
        set({ activeSessionId: id });
        invoke('set_active_session', { id }).catch(console.error);
      },

      updateSessionTitle: async (id: string, title: string) => {
        await invoke('rename_session', { id, title });
        set((state) => ({
          sessions: state.sessions.map((s) =>
            s.id === id ? { ...s, title } : s
          ),
        }));
      },

      moveSessionToSection: async (sessionId: string, sectionId: string) => {
        await invoke('move_session', { id: sessionId, sectionId });
        set((state) => ({
          sessions: state.sessions.map((s) =>
            s.id === sessionId ? { ...s, sectionId } : s
          ),
        }));
      },

      updateSessionStatus: (id: string, status: SessionStatus) => {
        set((state) => ({
          sessions: state.sessions.map((s) =>
            s.id === id ? { ...s, status } : s
          ),
        }));
      },

      updateToolSessionId: (id: string, tool: string, toolSessionId: string) => {
        set((state) => ({
          sessions: state.sessions.map((s) => {
            if (s.id !== id) return s;
            if (tool === 'claude') {
              return { ...s, claudeSessionId: toolSessionId };
            } else if (tool === 'gemini') {
              return { ...s, geminiSessionId: toolSessionId };
            }
            return s;
          }),
        }));
        // Persist to backend
        invoke('set_tool_session_id', { id, tool, toolSessionId }).catch(console.error);
      },

      loadFromBackend: async () => {
        try {
          const [sessions, sections] = await Promise.all([
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

          set({
            sessions,
            sections: mergedSections,
            activeSessionId: sessions.find((s) => s.isOpen)?.id || null,
          });
        } catch (err) {
          console.error('Failed to load from backend:', err);
        }
      },

      setHasHydrated: (value: boolean) => {
        set({ hasHydrated: value });
      },

      getSessionsBySection: (sectionId: string) => {
        return get().sessions.filter((s) => s.sectionId === sectionId);
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
