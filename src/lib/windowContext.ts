// ABOUTME: Window context utilities for multi-window support.
// ABOUTME: Provides functions to identify and initialize windows in a multi-window environment.

import { getCurrentWindow } from '@tauri-apps/api/window';
import { invoke } from '@tauri-apps/api/core';

/**
 * WindowRecord represents a single window's state and metadata.
 *
 * Example:
 * ```typescript
 * const record: WindowRecord = {
 *   id: "win-1",
 *   label: "main",
 *   title: "Agent Term",
 *   x: 100,
 *   y: 100,
 *   width: 1024,
 *   height: 768,
 *   isMaximized: false,
 *   sessionIds: ["session-1"],
 *   activeSessionId: "session-1",
 * };
 * ```
 */
export interface WindowRecord {
  id: string;
  label: string;
  title: string;
  x: number;
  y: number;
  width: number;
  height: number;
  isMaximized: boolean;
  sessionIds: string[];
  activeSessionId: string | null;
}

/**
 * Returns the current window's unique identifier (label).
 *
 * Example:
 * ```typescript
 * const windowId = getCurrentWindowId();
 * console.log(windowId); // "main" or "window-abc123"
 * ```
 */
export function getCurrentWindowId(): string {
  return getCurrentWindow().label;
}

/**
 * Returns the current window's label (alias for getCurrentWindowId).
 *
 * Example:
 * ```typescript
 * const label = getCurrentWindowLabel();
 * ```
 */
export function getCurrentWindowLabel(): string {
  return getCurrentWindow().label;
}

/**
 * Returns true if this is the main window.
 *
 * Example:
 * ```typescript
 * if (isMainWindow()) {
 *   console.log("This is the main window");
 * }
 * ```
 */
export function isMainWindow(): boolean {
  return getCurrentWindow().label === 'main';
}

/**
 * Initializes and returns the window record for secondary windows.
 * Returns null for the main window.
 *
 * Example:
 * ```typescript
 * const record = await initializeWindow();
 * if (record) {
 *   console.log("Secondary window:", record.title);
 * }
 * ```
 */
export async function initializeWindow(): Promise<WindowRecord | null> {
  const windowId = getCurrentWindowId();

  if (windowId === 'main') {
    return null;
  }

  try {
    const record = await invoke<WindowRecord>('get_window', { id: windowId });
    return record;
  } catch {
    return null;
  }
}

/**
 * Opens a new window with the specified title and sessions.
 *
 * Example:
 * ```typescript
 * const record = await openNewWindow("My Window", ["session-1"]);
 * console.log("New window created:", record.label);
 * ```
 */
export async function openNewWindow(
  title?: string,
  sessionIds: string[] = []
): Promise<WindowRecord> {
  return invoke<WindowRecord>('open_new_window', { title, sessionIds });
}

/**
 * Fetches all window records from the backend.
 *
 * Example:
 * ```typescript
 * const windows = await listWindows();
 * console.log("Open windows:", windows.length);
 * ```
 */
export async function listWindows(): Promise<WindowRecord[]> {
  return invoke<WindowRecord[]>('list_windows');
}

/**
 * Fetches a specific window record by ID.
 *
 * Example:
 * ```typescript
 * const window = await getWindow("win-123");
 * console.log("Window title:", window?.title);
 * ```
 */
export async function getWindow(id: string): Promise<WindowRecord | null> {
  try {
    return await invoke<WindowRecord>('get_window', { id });
  } catch {
    return null;
  }
}

/**
 * Updates a window's geometry (position and size).
 *
 * Example:
 * ```typescript
 * await updateWindowGeometry("win-123", 100, 100, 1024, 768, false);
 * ```
 */
export async function updateWindowGeometry(
  id: string,
  x: number,
  y: number,
  width: number,
  height: number,
  isMaximized: boolean
): Promise<void> {
  return invoke('update_window_geometry', { id, x, y, width, height, isMaximized });
}

/**
 * Deletes a window record from storage.
 *
 * Example:
 * ```typescript
 * await deleteWindowRecord("win-123");
 * ```
 */
export async function deleteWindowRecord(id: string): Promise<void> {
  return invoke('delete_window_record', { id });
}
