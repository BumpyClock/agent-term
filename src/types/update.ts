// ABOUTME: TypeScript types for the auto-update feature.
// ABOUTME: Defines UpdateInfo, UpdateSettings, and UpdateStatus types.

/**
 * Information about an available update
 */
export interface UpdateInfo {
  /** New version string (e.g., "1.2.0") */
  version: string;
  /** Release notes / changelog (markdown) */
  body: string | null;
  /** Release date (ISO 8601 string) */
  date: string | null;
}

/**
 * User settings for auto-update behavior
 */
export interface UpdateSettings {
  /** Auto-download updates when available (opt-in, default false) */
  autoUpdate: boolean;
  /** Enable checking for updates at all */
  checkEnabled: boolean;
  /** How often to check for updates (in hours) */
  checkIntervalHours: number;
  /** Show update notifications in CLI sessions */
  notifyInCli: boolean;
  /** Last time we checked for updates (ISO 8601) */
  lastCheckTime: string | null;
}

/**
 * Current status of the update process
 */
export type UpdateStatus =
  | 'idle'        // No update activity
  | 'checking'    // Checking for updates
  | 'available'   // Update is available, waiting for user action
  | 'downloading' // Currently downloading update
  | 'ready'       // Update downloaded, ready to install
  | 'error';      // Error occurred

/**
 * Response from update_get_status command
 */
export interface UpdateStatusResponse {
  status: UpdateStatus;
  updateInfo: UpdateInfo | null;
  downloadProgress: number;
  error: string | null;
}

/**
 * Default update settings (matches Rust defaults)
 */
export const DEFAULT_UPDATE_SETTINGS: UpdateSettings = {
  autoUpdate: false,
  checkEnabled: true,
  checkIntervalHours: 24,
  notifyInCli: true,
  lastCheckTime: null,
};

/**
 * Specific error types for update operations
 */
export type UpdateErrorType =
  | 'check_failed'
  | 'download_failed'
  | 'signature_invalid'
  | 'network_error'
  | 'unknown';

/**
 * Structured update error
 */
export interface UpdateError {
  type: UpdateErrorType;
  message: string;
}

/**
 * Parse error message to determine error type
 */
export function parseUpdateError(message: string): UpdateError {
  const lowerMessage = message.toLowerCase();

  if (lowerMessage.includes('network') || lowerMessage.includes('connection') || lowerMessage.includes('offline')) {
    return { type: 'network_error', message };
  }
  if (lowerMessage.includes('signature') || lowerMessage.includes('verify')) {
    return { type: 'signature_invalid', message };
  }
  if (lowerMessage.includes('download')) {
    return { type: 'download_failed', message };
  }
  if (lowerMessage.includes('check') || lowerMessage.includes('fetch')) {
    return { type: 'check_failed', message };
  }
  return { type: 'unknown', message };
}

/**
 * Get user-friendly error message
 */
export function getErrorMessage(error: UpdateError | null): string {
  if (!error) return 'An error occurred';

  switch (error.type) {
    case 'network_error':
      return 'No internet connection';
    case 'signature_invalid':
      return 'Update verification failed';
    case 'download_failed':
      return 'Download failed';
    case 'check_failed':
      return 'Could not check for updates';
    default:
      return 'Update error';
  }
}
