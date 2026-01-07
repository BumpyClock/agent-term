// ABOUTME: General settings tab with app info, updates, and reset options.
// ABOUTME: Provides a warm, approachable interface for managing app settings.

import { useEffect } from 'react';
import { useUpdateStore } from '@/store/updateStore';
import { useTerminalSettings } from '@/store/terminalSettingsStore';
import { Loader2 } from 'lucide-react';
import { AppInfoSection, UpdatesSection, ResetSection } from './general';

export function GeneralSettings() {
  const {
    updateInfo,
    settings,
    isLoadingSettings,
    loadSettings,
    saveSettings,
    checkForUpdate,
    error,
    clearError,
    showUpToDate,
  } = useUpdateStore();

  const { resetToDefaults: resetTerminalSettings } = useTerminalSettings();

  useEffect(() => {
    loadSettings();
  }, [loadSettings]);

  const handleAutoUpdateChange = async (checked: boolean) => {
    try {
      await saveSettings({ autoUpdate: checked });
    } catch (err) {
      console.error('Failed to save auto-update setting:', err);
    }
  };

  const handleCheckEnabledChange = async (checked: boolean) => {
    try {
      await saveSettings({ checkEnabled: checked });
    } catch (err) {
      console.error('Failed to save check-enabled setting:', err);
    }
  };

  const handleCheckNow = async () => {
    clearError();
    await checkForUpdate();
  };

  const handleResetSettings = async () => {
    // Reset terminal settings
    resetTerminalSettings();
    // Reset update settings to defaults
    await saveSettings({
      autoUpdate: true,
      checkEnabled: true,
    });
  };

  const handleClearCache = async () => {
    // Clear localStorage cache (except for essential settings)
    const keysToPreserve = ['terminal-settings', 'theme'];
    const keysToRemove: string[] = [];

    for (let i = 0; i < localStorage.length; i++) {
      const key = localStorage.key(i);
      if (key && !keysToPreserve.includes(key)) {
        keysToRemove.push(key);
      }
    }

    keysToRemove.forEach(key => localStorage.removeItem(key));
  };

  if (isLoadingSettings) {
    return (
      <div className="flex items-center justify-center py-12 text-muted-foreground">
        <Loader2 className="animate-spin mr-3" size={18} />
        <span className="text-sm">Loading your settings...</span>
      </div>
    );
  }

  return (
    <div className="space-y-8">
      <UpdatesSection
        settings={settings}
        updateInfo={updateInfo}
        error={error}
        showUpToDate={showUpToDate}
        onAutoUpdateChange={handleAutoUpdateChange}
        onCheckEnabledChange={handleCheckEnabledChange}
        onCheckNow={handleCheckNow}
      />

      <ResetSection
        onResetSettings={handleResetSettings}
        onClearCache={handleClearCache}
      />

      <AppInfoSection />
    </div>
  );
}
