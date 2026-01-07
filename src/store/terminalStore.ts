// ABOUTME: Maintains terminal sections and sessions plus their persistence across app runs.
// ABOUTME: Exposes actions to create, update, move, and remove sessions and sections.

import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { immer } from 'zustand/middleware/immer';
import { enableMapSet } from 'immer';
import { invoke } from '@tauri-apps/api/core';
import { arrayMove } from '@dnd-kit/sortable';

enableMapSet();

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
  reorderSessionsInSection: (sectionId: string, activeId: string, overId: string) => void;
  reorderSections: (activeId: string, overId: string) => void;
  moveSessionToSectionAtIndex: (sessionId: string, targetSectionId: string, index: number) => void;
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
    immer((set, get) => ({
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
        set((state) => {
          state.sections.push({ ...section, isDefault: false });
        });
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

          sessionIdsToMove.forEach((sessionId) => {
            if (state.sessions[sessionId]) {
              state.sessions[sessionId].sectionId = defaultSectionId;
            }
          });

          delete state.sessionsBySection[id];
          if (!state.sessionsBySection[defaultSectionId]) {
            state.sessionsBySection[defaultSectionId] = [];
          }
          state.sessionsBySection[defaultSectionId].push(...sessionIdsToMove);

          const idx = state.sections.findIndex((s) => s.id === id);
          if (idx !== -1) {
            state.sections.splice(idx, 1);
          }
        });
      },

      updateSection: (id: string, updates: Partial<Section>) => {
        set((state) => {
          const section = state.sections.find((s) => s.id === id);
          if (section) {
            Object.assign(section, updates);
          }
        });
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
        const sectionCheck = get().sections.find((s) => s.id === id);
        if (sectionCheck?.isDefault) return;
        set((state) => {
          const section = state.sections.find((s) => s.id === id);
          if (section) {
            section.collapsed = !section.collapsed;
          }
        });
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
          state.sessions[session.id] = session;
          if (!state.sessionsBySection[sectionId]) {
            state.sessionsBySection[sectionId] = [];
          }
          state.sessionsBySection[sectionId].push(session.id);
          state.sessionOrder.push(session.id);
          state.activeSessionId = session.id;
          state.activatedSessionIds.add(session.id);
        });
        return session;
      },

      removeSession: async (id: string) => {
        const platformInfo =
          typeof navigator !== 'undefined'
            ? navigator.userAgent ?? 'unknown-platform'
            : 'unknown-platform';
        console.debug('[tab-close][store] delete_session invoke', { id, platform: platformInfo });
        try {
          await invoke('delete_session', { id });
          set((state) => {
            const session = state.sessions[id];
            if (!session) return;

            delete state.sessions[id];

            const sectionSessions = state.sessionsBySection[session.sectionId];
            if (sectionSessions) {
              const idx = sectionSessions.indexOf(id);
              if (idx !== -1) sectionSessions.splice(idx, 1);
            }

            const orderIdx = state.sessionOrder.indexOf(id);
            if (orderIdx !== -1) state.sessionOrder.splice(orderIdx, 1);

            if (state.activeSessionId === id) {
              if (state.sessionOrder.length > 0) {
                state.activeSessionId =
                  state.sessionOrder[Math.min(orderIdx, state.sessionOrder.length - 1)] || null;
              } else {
                state.activeSessionId = null;
              }
            }

            state.activatedSessionIds.delete(id);
          });
          const nextState = get();
          console.debug('[tab-close][store] removeSession complete', {
            id,
            activeSessionId: nextState.activeSessionId,
            remainingSessions: Object.keys(nextState.sessions).length,
            platform: platformInfo,
          });
        } catch (err) {
          console.error('[tab-close][store] removeSession failed', {
            id,
            platform: platformInfo,
            error: err,
          });
          throw err;
        }
      },

      setActiveSession: (id: string) => {
        set({ activeSessionId: id });
        invoke('set_active_session', { id }).catch(console.error);
      },

      updateSessionTitle: async (id: string, title: string) => {
        await invoke('rename_session', { id, title });
        set((state) => {
          if (state.sessions[id]) {
            state.sessions[id].title = title;
          }
        });
      },

      updateSessionCommand: async (id: string, command: string) => {
        await invoke('set_session_command', { id, command });
        set((state) => {
          if (state.sessions[id]) {
            state.sessions[id].command = command;
          }
        });
      },

      updateSessionIcon: async (id: string, icon: string | null) => {
        await invoke('set_session_icon', { id, icon });
        set((state) => {
          if (state.sessions[id]) {
            state.sessions[id].icon = icon;
          }
        });
      },

      moveSessionToSection: async (sessionId: string, sectionId: string) => {
        await invoke('move_session', { id: sessionId, sectionId });
        set((state) => {
          const session = state.sessions[sessionId];
          if (!session) return;

          const oldSectionId = session.sectionId;
          const oldSectionSessions = state.sessionsBySection[oldSectionId];
          if (oldSectionSessions) {
            const idx = oldSectionSessions.indexOf(sessionId);
            if (idx !== -1) oldSectionSessions.splice(idx, 1);
          }

          if (!state.sessionsBySection[sectionId]) {
            state.sessionsBySection[sectionId] = [];
          }
          state.sessionsBySection[sectionId].push(sessionId);

          session.sectionId = sectionId;
        });
      },

      reorderSessionsInSection: (sectionId: string, activeId: string, overId: string) => {
        set((state) => {
          const sessionIds = state.sessionsBySection[sectionId];
          if (!sessionIds) return;

          const oldIndex = sessionIds.indexOf(activeId);
          const newIndex = sessionIds.indexOf(overId);

          if (oldIndex === -1 || newIndex === -1) return;

          state.sessionsBySection[sectionId] = arrayMove(sessionIds, oldIndex, newIndex);

          state.sessionsBySection[sectionId].forEach((id, index) => {
            if (state.sessions[id]) {
              state.sessions[id].tabOrder = index;
            }
          });
        });
      },

      reorderSections: (activeId: string, overId: string) => {
        set((state) => {
          const nonDefaultSections = state.sections.filter((s) => !s.isDefault);
          const sectionIds = nonDefaultSections.map((s) => s.id);

          const oldIndex = sectionIds.indexOf(activeId);
          const newIndex = sectionIds.indexOf(overId);

          if (oldIndex === -1 || newIndex === -1) return;

          const reorderedIds = arrayMove(sectionIds, oldIndex, newIndex);

          reorderedIds.forEach((id, index) => {
            const section = state.sections.find((s) => s.id === id);
            if (section) {
              section.order = index + 1;
            }
          });

          state.sections.sort((a, b) => {
            if (a.isDefault) return 1;
            if (b.isDefault) return -1;
            return a.order - b.order;
          });
        });
      },

      moveSessionToSectionAtIndex: (sessionId: string, targetSectionId: string, index: number) => {
        set((state) => {
          const session = state.sessions[sessionId];
          if (!session) return;

          const oldSectionId = session.sectionId;

          if (oldSectionId !== targetSectionId) {
            const oldSectionSessions = state.sessionsBySection[oldSectionId];
            if (oldSectionSessions) {
              const idx = oldSectionSessions.indexOf(sessionId);
              if (idx !== -1) oldSectionSessions.splice(idx, 1);
            }

            if (!state.sessionsBySection[targetSectionId]) {
              state.sessionsBySection[targetSectionId] = [];
            }

            const clampedIndex = Math.min(index, state.sessionsBySection[targetSectionId].length);
            state.sessionsBySection[targetSectionId].splice(clampedIndex, 0, sessionId);

            session.sectionId = targetSectionId;
          } else {
            const sectionSessions = state.sessionsBySection[targetSectionId];
            if (!sectionSessions) return;

            const currentIndex = sectionSessions.indexOf(sessionId);
            if (currentIndex === -1) return;

            state.sessionsBySection[targetSectionId] = arrayMove(
              sectionSessions,
              currentIndex,
              Math.min(index, sectionSessions.length - 1)
            );
          }

          [oldSectionId, targetSectionId].forEach((secId) => {
            const ids = state.sessionsBySection[secId];
            if (ids) {
              ids.forEach((id, i) => {
                if (state.sessions[id]) {
                  state.sessions[id].tabOrder = i;
                }
              });
            }
          });
        });

        invoke('move_session', { id: sessionId, sectionId: targetSectionId }).catch(console.error);
      },

      updateSessionStatus: (id: string, status: SessionStatus) => {
        set((state) => {
          if (state.sessions[id]) {
            state.sessions[id].status = status;
          }
        });
      },

      updateToolSessionId: (id: string, tool: string, toolSessionId: string) => {
        set((state) => {
          const session = state.sessions[id];
          if (!session) return;
          if (tool === 'claude') {
            session.claudeSessionId = toolSessionId;
          } else if (tool === 'gemini') {
            session.geminiSessionId = toolSessionId;
          }
        });
        invoke('set_tool_session_id', { id, tool, toolSessionId }).catch(console.error);
      },

      setLastKnownSize: (rows: number, cols: number) => {
        if (rows <= 0 || cols <= 0) return;
        set({ lastKnownRows: rows, lastKnownCols: cols });
      },

      markSessionActivated: (id: string) => {
        set((state) => {
          state.activatedSessionIds.add(id);
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
    })),
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
