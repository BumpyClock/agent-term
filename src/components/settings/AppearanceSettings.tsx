// ABOUTME: Appearance settings tab for theme and terminal font customization.
// ABOUTME: Provides a warm, approachable interface matching GeneralSettings pattern.

import { useTheme } from '@/components/theme-provider';
import { ThemeSection, TerminalFontSection } from './appearance';

export function AppearanceSettings() {
  const { theme, setTheme } = useTheme();

  return (
    <div className="space-y-8">
      <ThemeSection theme={theme} onThemeChange={setTheme} />
      <TerminalFontSection />
    </div>
  );
}
