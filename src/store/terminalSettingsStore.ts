import { create } from 'zustand';
import { persist } from 'zustand/middleware';

export interface TerminalSettings {
  fontFamily: string;
  fontSize: number;
  lineHeight: number;
  letterSpacing: number;
}

interface TerminalSettingsState extends TerminalSettings {
  setFontFamily: (fontFamily: string) => void;
  setFontSize: (fontSize: number) => void;
  setLineHeight: (lineHeight: number) => void;
  setLetterSpacing: (letterSpacing: number) => void;
  resetToDefaults: () => void;
}

export const DEFAULT_TERMINAL_SETTINGS: TerminalSettings = {
  fontFamily: '"FiraCode Nerd Font", Menlo, Monaco, "Courier New", monospace',
  fontSize: 14,
  lineHeight: 1.2,
  letterSpacing: 0,
};

export const FONT_OPTIONS = [
  { value: '"FiraCode Nerd Font", Menlo, Monaco, "Courier New", monospace', label: 'FiraCode Nerd Font' },
  { value: '"JetBrains Mono", Menlo, Monaco, "Courier New", monospace', label: 'JetBrains Mono' },
  { value: '"Source Code Pro", Menlo, Monaco, "Courier New", monospace', label: 'Source Code Pro' },
  { value: '"SF Mono", Menlo, Monaco, "Courier New", monospace', label: 'SF Mono' },
  { value: 'Menlo, Monaco, "Courier New", monospace', label: 'Menlo' },
  { value: 'Monaco, Menlo, "Courier New", monospace', label: 'Monaco' },
  { value: 'Consolas, Menlo, Monaco, "Courier New", monospace', label: 'Consolas' },
  { value: '"Courier New", Courier, monospace', label: 'Courier New' },
];

export const useTerminalSettings = create<TerminalSettingsState>()(
  persist(
    (set) => ({
      ...DEFAULT_TERMINAL_SETTINGS,

      setFontFamily: (fontFamily) => set({ fontFamily }),
      setFontSize: (fontSize) => set({ fontSize: Math.min(32, Math.max(8, fontSize)) }),
      setLineHeight: (lineHeight) => set({ lineHeight: Math.min(2.0, Math.max(1.0, lineHeight)) }),
      setLetterSpacing: (letterSpacing) => set({ letterSpacing: Math.min(5, Math.max(-2, letterSpacing)) }),
      resetToDefaults: () => set(DEFAULT_TERMINAL_SETTINGS),
    }),
    {
      name: 'terminal-settings',
    }
  )
);
