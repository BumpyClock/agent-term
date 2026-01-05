import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { v4 as uuidv4 } from 'uuid';

export interface Tab {
  id: string;
  title: string;
  sectionId: string;
}

export interface Section {
  id: string;
  name: string;
  path: string;
  isDefault?: boolean;
  isCollapsed?: boolean;
}

interface TerminalState {
  sections: Section[];
  tabs: Tab[];
  activeTabId: string | null;

  // Section actions
  addSection: (name: string, path: string) => Section;
  removeSection: (id: string) => void;
  updateSection: (id: string, updates: Partial<Section>) => void;
  toggleSectionCollapse: (id: string) => void;

  // Tab actions
  addTab: (sectionId: string, title?: string) => Tab;
  removeTab: (id: string) => void;
  setActiveTab: (id: string) => void;
  updateTabTitle: (id: string, title: string) => void;
  moveTabToSection: (tabId: string, sectionId: string) => void;

  // Getters
  getTabsBySection: (sectionId: string) => Tab[];
  getDefaultSection: () => Section | undefined;
}

const DEFAULT_SECTION_ID = 'default-section';

export const useTerminalStore = create<TerminalState>()(
  persist(
    (set, get) => ({
      sections: [
        {
          id: DEFAULT_SECTION_ID,
          name: 'Default',
          path: '', // Will be set to home dir
          isDefault: true,
          isCollapsed: false,
        },
      ],
      tabs: [],
      activeTabId: null,

      addSection: (name: string, path: string) => {
        const newSection: Section = {
          id: uuidv4(),
          name,
          path,
          isCollapsed: false,
        };
        set((state) => ({
          sections: [...state.sections, newSection],
        }));
        return newSection;
      },

      removeSection: (id: string) => {
        const section = get().sections.find((s) => s.id === id);
        if (section?.isDefault) return; // Cannot remove default section

        set((state) => {
          // Move tabs from this section to default
          const defaultSection = state.sections.find((s) => s.isDefault);
          const updatedTabs = state.tabs.map((tab) =>
            tab.sectionId === id
              ? { ...tab, sectionId: defaultSection?.id || DEFAULT_SECTION_ID }
              : tab
          );

          return {
            sections: state.sections.filter((s) => s.id !== id),
            tabs: updatedTabs,
          };
        });
      },

      updateSection: (id: string, updates: Partial<Section>) => {
        set((state) => ({
          sections: state.sections.map((s) =>
            s.id === id ? { ...s, ...updates } : s
          ),
        }));
      },

      toggleSectionCollapse: (id: string) => {
        const section = get().sections.find((s) => s.id === id);
        if (section?.isDefault) return;
        set((state) => ({
          sections: state.sections.map((s) =>
            s.id === id ? { ...s, isCollapsed: !s.isCollapsed } : s
          ),
        }));
      },

      addTab: (sectionId: string, title?: string) => {
        const tabs = get().tabs;
        const sectionTabs = tabs.filter((t) => t.sectionId === sectionId);
        const newTab: Tab = {
          id: uuidv4(),
          title: title || `Terminal ${sectionTabs.length + 1}`,
          sectionId,
        };
        set((state) => ({
          tabs: [...state.tabs, newTab],
          activeTabId: newTab.id,
        }));
        return newTab;
      },

      removeTab: (id: string) => {
        set((state) => {
          const newTabs = state.tabs.filter((t) => t.id !== id);
          let newActiveTabId = state.activeTabId;

          if (state.activeTabId === id) {
            // Find another tab to activate
            const tabIndex = state.tabs.findIndex((t) => t.id === id);
            if (newTabs.length > 0) {
              newActiveTabId =
                newTabs[Math.min(tabIndex, newTabs.length - 1)]?.id || null;
            } else {
              newActiveTabId = null;
            }
          }

          return {
            tabs: newTabs,
            activeTabId: newActiveTabId,
          };
        });
      },

      setActiveTab: (id: string) => {
        set({ activeTabId: id });
      },

      updateTabTitle: (id: string, title: string) => {
        set((state) => ({
          tabs: state.tabs.map((t) => (t.id === id ? { ...t, title } : t)),
        }));
      },

      moveTabToSection: (tabId: string, sectionId: string) => {
        set((state) => ({
          tabs: state.tabs.map((t) =>
            t.id === tabId ? { ...t, sectionId } : t
          ),
        }));
      },

      getTabsBySection: (sectionId: string) => {
        return get().tabs.filter((t) => t.sectionId === sectionId);
      },

      getDefaultSection: () => {
        return get().sections.find((s) => s.isDefault);
      },
    }),
    {
      name: 'terminal-storage',
      partialize: (state) => ({
        sections: state.sections,
        // Don't persist tabs - they will be recreated
      }),
    }
  )
);
