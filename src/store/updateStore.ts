// ABOUTME: Zustand store for managing application update state.
// ABOUTME: Handles checking for updates, downloading, and triggering installation.

import { create } from 'zustand';
import { invoke } from '@tauri-apps/api/core';
import { listen, type UnlistenFn } from '@tauri-apps/api/event';
import type {
  UpdateInfo,
  UpdateSettings,
  UpdateStatus,
  UpdateStatusResponse,
  UpdateError,
} from '@/types/update';
import { DEFAULT_UPDATE_SETTINGS, parseUpdateError } from '@/types/update';

interface UpdateState {
  // State
  status: UpdateStatus;
  updateInfo: UpdateInfo | null;
  downloadProgress: number;
  error: UpdateError | null;
  settings: UpdateSettings;
  isLoadingSettings: boolean;
  currentVersion: string | null;
  showUpToDate: boolean;

  // Actions
  checkForUpdate: () => Promise<UpdateInfo | null>;
  downloadUpdate: () => Promise<void>;
  installUpdate: () => Promise<void>;
  loadSettings: () => Promise<void>;
  saveSettings: (updates: Partial<UpdateSettings>) => Promise<void>;
  refreshStatus: () => Promise<void>;
  clearError: () => void;
  reset: () => void;
  retryCheck: () => Promise<UpdateInfo | null>;
  retryDownload: () => Promise<void>;
  setCurrentVersion: (version: string) => void;

  // Internal
  _setStatus: (status: UpdateStatus) => void;
  _setProgress: (progress: number) => void;
  _unlistenProgress: UnlistenFn | null;
  _upToDateTimer: ReturnType<typeof setTimeout> | null;
  _setupProgressListener: () => Promise<void>;
  _cleanupProgressListener: () => void;
}

export const useUpdateStore = create<UpdateState>()((set, get) => ({
  // Initial state
  status: 'idle',
  updateInfo: null,
  downloadProgress: 0,
  error: null,
  settings: DEFAULT_UPDATE_SETTINGS,
  isLoadingSettings: false,
  currentVersion: null,
  showUpToDate: false,
  _unlistenProgress: null,
  _upToDateTimer: null,

  // Check for available updates
  checkForUpdate: async () => {
    // Clear any existing up-to-date timer
    const { _upToDateTimer } = get();
    if (_upToDateTimer) {
      clearTimeout(_upToDateTimer);
      set({ _upToDateTimer: null });
    }

    set({ status: 'checking', error: null, showUpToDate: false });

    try {
      const updateInfo = await invoke<UpdateInfo | null>('update_check');

      if (updateInfo) {
        set({
          status: 'available',
          updateInfo,
          error: null,
        });

        // Auto-download if enabled
        const { settings } = get();
        if (settings.autoUpdate) {
          // Small delay to let UI update
          setTimeout(() => {
            get().downloadUpdate();
          }, 500);
        }

        return updateInfo;
      } else {
        // Show "up to date" for 2 seconds, then fade back to idle
        set({ status: 'idle', updateInfo: null, showUpToDate: true });

        const timer = setTimeout(() => {
          set({ showUpToDate: false, _upToDateTimer: null });
        }, 2000);

        set({ _upToDateTimer: timer });
        return null;
      }
    } catch (err) {
      const errorMsg = err instanceof Error ? err.message : String(err);
      set({ status: 'error', error: parseUpdateError(errorMsg) });
      console.error('Failed to check for updates:', err);
      return null;
    }
  },

  // Download the available update
  downloadUpdate: async () => {
    const { status, updateInfo } = get();

    if (status !== 'available' || !updateInfo) {
      console.warn('No update available to download');
      return;
    }

    set({ status: 'downloading', downloadProgress: 0, error: null });

    // Setup progress listener before starting download
    await get()._setupProgressListener();

    try {
      await invoke('update_download');
      set({ status: 'ready', downloadProgress: 100 });
    } catch (err) {
      const errorMsg = err instanceof Error ? err.message : String(err);
      set({ status: 'error', error: parseUpdateError(errorMsg) });
      console.error('Failed to download update:', err);
    } finally {
      get()._cleanupProgressListener();
    }
  },

  // Install the downloaded update (triggers app restart)
  installUpdate: async () => {
    const { status } = get();

    if (status !== 'ready') {
      console.warn('No update ready to install');
      return;
    }

    try {
      await invoke('update_install');
      // App will restart, so this won't continue
    } catch (err) {
      const errorMsg = err instanceof Error ? err.message : String(err);
      set({ status: 'error', error: parseUpdateError(errorMsg) });
      console.error('Failed to install update:', err);
    }
  },

  // Load settings from backend
  loadSettings: async () => {
    set({ isLoadingSettings: true });

    try {
      const settings = await invoke<UpdateSettings>('update_get_settings');
      set({ settings, isLoadingSettings: false });
    } catch (err) {
      console.error('Failed to load update settings:', err);
      set({ isLoadingSettings: false });
    }
  },

  // Save settings to backend
  saveSettings: async (updates) => {
    const { settings } = get();
    const newSettings = { ...settings, ...updates };

    try {
      await invoke('update_set_settings', { settings: newSettings });
      set({ settings: newSettings });
    } catch (err) {
      console.error('Failed to save update settings:', err);
      throw err;
    }
  },

  // Refresh status from backend
  refreshStatus: async () => {
    try {
      const response = await invoke<UpdateStatusResponse>('update_get_status');
      set({
        status: response.status,
        updateInfo: response.updateInfo,
        downloadProgress: response.downloadProgress,
        error: response.error ? parseUpdateError(response.error) : null,
      });
    } catch (err) {
      console.error('Failed to refresh update status:', err);
    }
  },

  // Clear error state
  clearError: () => {
    set({ error: null });
    // If we were in error state, go back to idle
    const { status } = get();
    if (status === 'error') {
      set({ status: 'idle' });
    }
  },

  // Reset to initial state
  reset: () => {
    get()._cleanupProgressListener();
    set({
      status: 'idle',
      updateInfo: null,
      downloadProgress: 0,
      error: null,
    });
  },

  // Internal: Set status
  _setStatus: (status) => set({ status }),

  // Internal: Set download progress
  _setProgress: (progress) => set({ downloadProgress: progress }),

  // Internal: Setup progress event listener
  _setupProgressListener: async () => {
    get()._cleanupProgressListener();

    try {
      const unlisten = await listen<number>('update-download-progress', (event) => {
        set({ downloadProgress: event.payload });
      });
      set({ _unlistenProgress: unlisten });
    } catch (err) {
      console.error('Failed to setup progress listener:', err);
    }
  },

  // Internal: Cleanup progress listener
  _cleanupProgressListener: () => {
    const { _unlistenProgress } = get();
    if (_unlistenProgress) {
      _unlistenProgress();
      set({ _unlistenProgress: null });
    }
  },

  // Retry check for updates
  retryCheck: async () => {
    get().clearError();
    return get().checkForUpdate();
  },

  // Retry download
  retryDownload: async () => {
    const { status } = get();
    get().clearError();
    if (status === 'error') {
      set({ status: 'available' });
    }
    return get().downloadUpdate();
  },

  // Set current app version
  setCurrentVersion: (version: string) => {
    set({ currentVersion: version });
  },
}));

/**
 * Check if enough time has passed since last update check
 */
export function shouldCheckForUpdates(settings: UpdateSettings): boolean {
  if (!settings.checkEnabled) {
    return false;
  }

  if (!settings.lastCheckTime) {
    return true;
  }

  const lastCheck = new Date(settings.lastCheckTime);
  const now = new Date();
  const hoursSinceLastCheck = (now.getTime() - lastCheck.getTime()) / (1000 * 60 * 60);

  return hoursSinceLastCheck >= settings.checkIntervalHours;
}

/**
 * Hook for initializing update check on app start
 */
export function useInitializeUpdateCheck() {
  const { loadSettings, checkForUpdate, settings } = useUpdateStore();

  // This should be called once on app mount
  const initialize = async () => {
    await loadSettings();

    const currentSettings = useUpdateStore.getState().settings;
    if (shouldCheckForUpdates(currentSettings)) {
      await checkForUpdate();
    }
  };

  return { initialize, settings };
}
