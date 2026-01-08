// ABOUTME: Appearance settings tab for theme, accent color, and terminal customization.
// ABOUTME: Provides a warm, approachable interface matching GeneralSettings pattern.

import { useTheme } from '@/components/theme-provider';
import { useTerminalSettings } from '@/store/terminalSettingsStore';
import {
  ThemeSection,
  TerminalFontSection,
  AccentColorSection,
  TerminalColorSchemeSection,
} from './appearance';

export function AppearanceSettings() {
  const { theme, setTheme } = useTheme();
  const accentColor = useTerminalSettings((state) => state.accentColor);
  const setAccentColor = useTerminalSettings((state) => state.setAccentColor);
  const terminalColorScheme = useTerminalSettings((state) => state.terminalColorScheme);
  const setTerminalColorScheme = useTerminalSettings((state) => state.setTerminalColorScheme);

  return (
    <div className="space-y-8">
      <ThemeSection theme={theme} onThemeChange={setTheme} />
      <AccentColorSection accentColor={accentColor} onAccentColorChange={setAccentColor} />
      <TerminalColorSchemeSection
        colorScheme={terminalColorScheme}
        onColorSchemeChange={setTerminalColorScheme}
      />
      <TerminalFontSection />
    </div>
  );
}
