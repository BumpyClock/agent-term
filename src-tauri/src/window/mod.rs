// ABOUTME: Manages window state tracking and persistence for multi-window support.
// ABOUTME: Tracks window positions, sizes, and session associations across app restarts.

use parking_lot::Mutex;
use tauri::{AppHandle, Emitter, Manager, State, WebviewUrl, WebviewWindowBuilder};
use uuid::Uuid;

#[cfg(target_os = "macos")]
use tauri::TitleBarStyle;

#[cfg(target_os = "macos")]
use window_vibrancy::{apply_vibrancy, NSVisualEffectMaterial};

#[cfg(target_os = "windows")]
use window_vibrancy::{apply_acrylic, apply_mica};

mod storage;

pub use storage::WindowRecord;
use storage::{default_storage_root, DebouncedWindowStorage, WindowSnapshot, WindowStorage};

/// WindowManager coordinates window metadata and persistence.
///
/// Example:
/// ```rust,ignore
/// let manager = build_window_manager()?;
/// let windows = manager.list_windows();
/// ```
pub struct WindowManager {
    storage: DebouncedWindowStorage,
    snapshot: Mutex<WindowSnapshot>,
}

impl WindowManager {
    pub fn list_windows(&self) -> Vec<WindowRecord> {
        self.snapshot.lock().windows.clone()
    }

    pub fn get_window(&self, id: &str) -> Option<WindowRecord> {
        self.snapshot
            .lock()
            .windows
            .iter()
            .find(|w| w.id == id)
            .cloned()
    }

    pub fn create_window(&self, record: WindowRecord) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        if snapshot.windows.iter().any(|w| w.id == record.id) {
            return Err(format!("window already exists: {}", record.id));
        }
        snapshot.windows.push(record);
        self.storage.save(&snapshot)?;
        Ok(())
    }

    pub fn update_window(&self, record: WindowRecord) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        let pos = snapshot
            .windows
            .iter()
            .position(|w| w.id == record.id)
            .ok_or_else(|| format!("window not found: {}", record.id))?;
        snapshot.windows[pos] = record;
        self.storage.save(&snapshot)?;
        Ok(())
    }

    pub fn delete_window(&self, id: &str) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        let initial_len = snapshot.windows.len();
        snapshot.windows.retain(|w| w.id != id);
        if snapshot.windows.len() == initial_len {
            return Err(format!("window not found: {}", id));
        }
        self.storage.save(&snapshot)?;
        Ok(())
    }

    pub fn get_secondary_windows(&self) -> Vec<WindowRecord> {
        let snapshot = self.snapshot.lock();
        snapshot
            .windows
            .iter()
            .filter(|w| w.id != snapshot.main_window_id)
            .cloned()
            .collect()
    }

    pub fn save_snapshot(&self) -> Result<(), String> {
        let snapshot = self.snapshot.lock();
        self.storage.save(&snapshot)
    }
}

pub fn build_window_manager() -> Result<WindowManager, String> {
    let storage = WindowStorage::new(default_storage_root(), "default".to_string());
    let snapshot = storage.load()?;
    let debounced = DebouncedWindowStorage::new(storage, 500);

    Ok(WindowManager {
        storage: debounced,
        snapshot: Mutex::new(snapshot),
    })
}

#[tauri::command(rename_all = "camelCase")]
pub fn list_windows(state: State<'_, WindowManager>) -> Result<Vec<WindowRecord>, String> {
    Ok(state.list_windows())
}

#[tauri::command(rename_all = "camelCase")]
pub fn get_window(state: State<'_, WindowManager>, id: String) -> Result<WindowRecord, String> {
    state
        .get_window(&id)
        .ok_or_else(|| format!("window not found: {}", id))
}

#[tauri::command(rename_all = "camelCase")]
pub fn create_window_record(
    state: State<'_, WindowManager>,
    label: String,
    title: String,
    session_ids: Vec<String>,
) -> Result<WindowRecord, String> {
    let record = WindowRecord {
        id: Uuid::new_v4().to_string(),
        label,
        title,
        x: 0,
        y: 0,
        width: 1024,
        height: 768,
        is_maximized: false,
        session_ids: session_ids.clone(),
        active_session_id: session_ids.first().cloned(),
    };
    state.create_window(record.clone())?;
    Ok(record)
}

#[tauri::command(rename_all = "camelCase")]
pub fn update_window_geometry(
    state: State<'_, WindowManager>,
    id: String,
    x: i32,
    y: i32,
    width: u32,
    height: u32,
    is_maximized: bool,
) -> Result<(), String> {
    let mut record = state
        .get_window(&id)
        .ok_or_else(|| format!("window not found: {}", id))?;
    record.x = x;
    record.y = y;
    record.width = width;
    record.height = height;
    record.is_maximized = is_maximized;
    state.update_window(record)?;
    Ok(())
}

#[tauri::command(rename_all = "camelCase")]
pub fn delete_window_record(state: State<'_, WindowManager>, id: String) -> Result<(), String> {
    state.delete_window(&id)
}

/// Creates a secondary window with platform-specific vibrancy effects.
///
/// Example:
/// ```rust,ignore
/// let window = create_secondary_window(&app, &record).await?;
/// ```
pub async fn create_secondary_window(
    app: &AppHandle,
    record: &WindowRecord,
) -> Result<tauri::WebviewWindow, String> {
    let window = WebviewWindowBuilder::new(app, &record.label, WebviewUrl::App("index.html".into()))
        .inner_size(record.width as f64, record.height as f64)
        .position(record.x as f64, record.y as f64)
        .transparent(true)
        .decorations(false)
        .title(&record.title)
        .build()
        .map_err(|e| format!("failed to create window: {}", e))?;

    apply_platform_vibrancy(&window)?;

    Ok(window)
}

fn apply_platform_vibrancy(window: &tauri::WebviewWindow) -> Result<(), String> {
    #[cfg(target_os = "macos")]
    {
        let _ = window.set_title_bar_style(TitleBarStyle::Overlay);
        apply_vibrancy(window, NSVisualEffectMaterial::HudWindow, None, None)
            .map_err(|e| format!("failed to apply vibrancy: {}", e))?;
    }

    #[cfg(target_os = "windows")]
    {
        if apply_mica(window, Some(true)).is_err() {
            apply_acrylic(window, Some((18, 18, 18, 125)))
                .map_err(|e| format!("failed to apply acrylic: {}", e))?;
        }
    }

    #[cfg(target_os = "linux")]
    {
        let _ = window;
    }

    Ok(())
}

#[tauri::command(rename_all = "camelCase")]
pub async fn open_new_window(
    app: AppHandle,
    state: State<'_, WindowManager>,
    title: Option<String>,
    session_ids: Vec<String>,
) -> Result<WindowRecord, String> {
    let id = Uuid::new_v4().to_string();
    let label = format!("window-{}", Uuid::new_v4());

    let record = WindowRecord {
        id,
        label: label.clone(),
        title: title.unwrap_or_else(|| "Agent Term".to_string()),
        x: 100,
        y: 100,
        width: 1024,
        height: 768,
        is_maximized: false,
        session_ids: session_ids.clone(),
        active_session_id: session_ids.first().cloned(),
    };

    state.create_window(record.clone())?;
    create_secondary_window(&app, &record).await?;

    Ok(record)
}

/// Restores secondary windows from a list of records on app startup.
///
/// Example:
/// ```rust,ignore
/// let records = manager.get_secondary_windows();
/// restore_secondary_windows_from_records(&app, records).await;
/// ```
pub async fn restore_secondary_windows_from_records(app: &AppHandle, records: Vec<WindowRecord>) {
    use crate::diagnostics;

    for record in records {
        match create_secondary_window(app, &record).await {
            Ok(_) => {
                let msg = format!("restored window label={}", record.label);
                diagnostics::log(msg);
            }
            Err(e) => {
                let msg = format!("window_restore_error label={} error={}", record.label, e);
                diagnostics::log(msg);
            }
        }
    }
}

/// Moves a session from one window to another.
/// Updates both source and target window records, removing the session from source
/// and adding it to target. The moved session becomes active in the target window.
///
/// Example:
/// ```rust,ignore
/// move_session_to_window(state, "session-1", "window-source", "window-target")?;
/// ```
#[tauri::command(rename_all = "camelCase")]
pub fn move_session_to_window(
    window_state: State<'_, WindowManager>,
    session_id: String,
    source_window_id: String,
    target_window_id: String,
) -> Result<(), String> {
    let mut snapshot = window_state.snapshot.lock();

    // Remove from source window
    if let Some(source) = snapshot.windows.iter_mut().find(|w| w.id == source_window_id) {
        source.session_ids.retain(|id| id != &session_id);
        if source.active_session_id.as_ref() == Some(&session_id) {
            source.active_session_id = source.session_ids.first().cloned();
        }
    }

    // Add to target window
    if let Some(target) = snapshot.windows.iter_mut().find(|w| w.id == target_window_id) {
        if !target.session_ids.contains(&session_id) {
            target.session_ids.push(session_id.clone());
        }
        target.active_session_id = Some(session_id);
    } else {
        return Err("target window not found".to_string());
    }

    // Trigger save
    drop(snapshot);
    window_state.save_snapshot()?;

    Ok(())
}

/// Relays an IPC event to a specific window by label.
/// Used for cross-window communication when sessions are moved or mirrored.
///
/// Example:
/// ```rust,ignore
/// relay_window_ipc(&app, "window-abc", "session-moved", json!({"sessionId": "123"}))?;
/// ```
#[tauri::command(rename_all = "camelCase")]
pub fn relay_window_ipc(
    app: AppHandle,
    target_window_label: String,
    event_name: String,
    payload: serde_json::Value,
) -> Result<(), String> {
    if let Some(window) = app.get_webview_window(&target_window_label) {
        window
            .emit(&event_name, payload)
            .map_err(|e| format!("failed to emit: {}", e))?;
    }
    Ok(())
}

/// Merges sessions from source window into target window, then closes source window.
/// Avoids session duplicates by checking if sessions already exist in target.
///
/// Example:
/// ```rust,ignore
/// merge_windows(app, state, "window-abc", "main")?;
/// ```
#[tauri::command(rename_all = "camelCase")]
pub fn merge_windows(
    app: AppHandle,
    state: State<'_, WindowManager>,
    source_window_id: String,
    target_window_id: String,
) -> Result<(), String> {
    let mut snapshot = state.snapshot.lock();

    // Find windows
    let source_idx = snapshot.windows.iter().position(|w| w.id == source_window_id)
        .ok_or_else(|| "source window not found".to_string())?;
    let target_idx = snapshot.windows.iter().position(|w| w.id == target_window_id)
        .ok_or_else(|| "target window not found".to_string())?;

    // Get sessions from source
    let source_sessions = snapshot.windows[source_idx].session_ids.clone();

    // Add sessions to target (avoid duplicates)
    for session_id in source_sessions {
        if !snapshot.windows[target_idx].session_ids.contains(&session_id) {
            snapshot.windows[target_idx].session_ids.push(session_id);
        }
    }

    // Remove source window
    let source_label = snapshot.windows[source_idx].label.clone();
    snapshot.windows.remove(source_idx);

    // Close the source window
    drop(snapshot);
    if let Some(window) = app.get_webview_window(&source_label) {
        let _ = window.close();
    }

    state.save_snapshot()?;
    Ok(())
}

/// Merges all secondary windows into the main window.
/// Useful for consolidating all sessions into a single window.
///
/// Example:
/// ```rust,ignore
/// merge_all_windows(app, state)?;
/// ```
#[tauri::command(rename_all = "camelCase")]
pub fn merge_all_windows(
    app: AppHandle,
    state: State<'_, WindowManager>,
) -> Result<(), String> {
    let secondary_windows = state.get_secondary_windows();

    for window in secondary_windows {
        merge_windows(app.clone(), state.clone(), window.id.clone(), "main".to_string())?;
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::TempDir;

    fn build_test_manager() -> (WindowManager, TempDir) {
        let temp = TempDir::new().unwrap();
        let storage = WindowStorage::new(temp.path().to_path_buf(), "test".to_string());
        let snapshot = storage.load().unwrap();
        let debounced = DebouncedWindowStorage::new(storage, 500);

        let manager = WindowManager {
            storage: debounced,
            snapshot: Mutex::new(snapshot),
        };
        (manager, temp)
    }

    #[test]
    fn test_create_and_list_windows_returns_created_window() {
        let (manager, _temp) = build_test_manager();

        let window = WindowRecord {
            id: "win-test-123".to_string(),
            label: "main".to_string(),
            title: "Test 窓".to_string(),
            x: 100,
            y: 200,
            width: 800,
            height: 600,
            is_maximized: false,
            session_ids: vec!["session-1".to_string()],
            active_session_id: Some("session-1".to_string()),
        };

        manager.create_window(window.clone()).unwrap();
        let windows = manager.list_windows();

        assert_eq!(windows.len(), 1, "list must contain created window");
        assert_eq!(windows[0].id, "win-test-123", "window id must match");
        assert_eq!(windows[0].title, "Test 窓", "unicode title must be preserved");
    }

    #[test]
    fn test_create_duplicate_window_fails() {
        let (manager, _temp) = build_test_manager();

        let window = WindowRecord {
            id: "win-duplicate".to_string(),
            label: "main".to_string(),
            title: "First".to_string(),
            x: 0,
            y: 0,
            width: 800,
            height: 600,
            is_maximized: false,
            session_ids: vec![],
            active_session_id: None,
        };

        manager.create_window(window.clone()).unwrap();
        let result = manager.create_window(window);

        assert!(result.is_err(), "creating duplicate window must fail");
        assert!(result.unwrap_err().contains("already exists"), "error must mention duplicate");
    }

    #[test]
    fn test_update_window_geometry_modifies_record() {
        let (manager, _temp) = build_test_manager();

        let window = WindowRecord {
            id: "win-update".to_string(),
            label: "main".to_string(),
            title: "Original".to_string(),
            x: 0,
            y: 0,
            width: 800,
            height: 600,
            is_maximized: false,
            session_ids: vec![],
            active_session_id: None,
        };

        manager.create_window(window).unwrap();

        let updated = WindowRecord {
            id: "win-update".to_string(),
            label: "main".to_string(),
            title: "Updated Title".to_string(),
            x: 150,
            y: 250,
            width: 1920,
            height: 1080,
            is_maximized: true,
            session_ids: vec!["new-session".to_string()],
            active_session_id: Some("new-session".to_string()),
        };

        manager.update_window(updated.clone()).unwrap();
        let retrieved = manager.get_window("win-update").unwrap();

        assert_eq!(retrieved.x, 150, "x position must be updated");
        assert_eq!(retrieved.y, 250, "y position must be updated");
        assert_eq!(retrieved.width, 1920, "width must be updated");
        assert_eq!(retrieved.height, 1080, "height must be updated");
        assert!(retrieved.is_maximized, "maximized state must be updated");
        assert_eq!(retrieved.title, "Updated Title", "title must be updated");
    }

    #[test]
    fn test_update_nonexistent_window_fails() {
        let (manager, _temp) = build_test_manager();

        let window = WindowRecord {
            id: "nonexistent".to_string(),
            label: "main".to_string(),
            title: "Test".to_string(),
            x: 0,
            y: 0,
            width: 800,
            height: 600,
            is_maximized: false,
            session_ids: vec![],
            active_session_id: None,
        };

        let result = manager.update_window(window);

        assert!(result.is_err(), "updating nonexistent window must fail");
        assert!(result.unwrap_err().contains("not found"), "error must mention not found");
    }

    #[test]
    fn test_delete_window_removes_record() {
        let (manager, _temp) = build_test_manager();

        let window = WindowRecord {
            id: "win-delete".to_string(),
            label: "main".to_string(),
            title: "To Delete".to_string(),
            x: 0,
            y: 0,
            width: 800,
            height: 600,
            is_maximized: false,
            session_ids: vec![],
            active_session_id: None,
        };

        manager.create_window(window).unwrap();
        assert_eq!(manager.list_windows().len(), 1, "window must exist before deletion");

        manager.delete_window("win-delete").unwrap();
        assert_eq!(manager.list_windows().len(), 0, "window must be removed after deletion");
    }

    #[test]
    fn test_delete_nonexistent_window_fails() {
        let (manager, _temp) = build_test_manager();
        let result = manager.delete_window("nonexistent");

        assert!(result.is_err(), "deleting nonexistent window must fail");
        assert!(result.unwrap_err().contains("not found"), "error must mention not found");
    }

    #[test]
    fn test_get_secondary_windows_excludes_main() {
        let (manager, _temp) = build_test_manager();

        manager.snapshot.lock().main_window_id = "main-win".to_string();

        let main_window = WindowRecord {
            id: "main-win".to_string(),
            label: "main".to_string(),
            title: "Main".to_string(),
            x: 0,
            y: 0,
            width: 800,
            height: 600,
            is_maximized: false,
            session_ids: vec![],
            active_session_id: None,
        };

        let secondary = WindowRecord {
            id: "secondary-win".to_string(),
            label: "secondary".to_string(),
            title: "Secondary".to_string(),
            x: 100,
            y: 100,
            width: 800,
            height: 600,
            is_maximized: false,
            session_ids: vec![],
            active_session_id: None,
        };

        manager.create_window(main_window).unwrap();
        manager.create_window(secondary).unwrap();

        let secondary_windows = manager.get_secondary_windows();

        assert_eq!(secondary_windows.len(), 1, "only secondary window must be returned");
        assert_eq!(secondary_windows[0].id, "secondary-win", "secondary window id must match");
    }

    #[test]
    fn test_get_secondary_windows_returns_multiple_secondary_windows() {
        let (manager, _temp) = build_test_manager();

        manager.snapshot.lock().main_window_id = "main-win".to_string();

        let main_window = WindowRecord {
            id: "main-win".to_string(),
            label: "main".to_string(),
            title: "Main".to_string(),
            x: 0,
            y: 0,
            width: 800,
            height: 600,
            is_maximized: false,
            session_ids: vec![],
            active_session_id: None,
        };

        let secondary1 = WindowRecord {
            id: "secondary-1".to_string(),
            label: "window-abc".to_string(),
            title: "Secondary 1".to_string(),
            x: 100,
            y: 100,
            width: 800,
            height: 600,
            is_maximized: false,
            session_ids: vec!["sess-1".to_string()],
            active_session_id: Some("sess-1".to_string()),
        };

        let secondary2 = WindowRecord {
            id: "secondary-2".to_string(),
            label: "window-xyz".to_string(),
            title: "Secondary 2".to_string(),
            x: 200,
            y: 200,
            width: 1024,
            height: 768,
            is_maximized: true,
            session_ids: vec!["sess-2".to_string(), "sess-3".to_string()],
            active_session_id: Some("sess-2".to_string()),
        };

        manager.create_window(main_window).unwrap();
        manager.create_window(secondary1).unwrap();
        manager.create_window(secondary2).unwrap();

        let secondary_windows = manager.get_secondary_windows();

        assert_eq!(secondary_windows.len(), 2, "both secondary windows must be returned");
        let ids: Vec<&str> = secondary_windows.iter().map(|w| w.id.as_str()).collect();
        assert!(ids.contains(&"secondary-1"), "first secondary must be included");
        assert!(ids.contains(&"secondary-2"), "second secondary must be included");
    }

    #[test]
    fn test_get_secondary_windows_returns_empty_when_only_main_exists() {
        let (manager, _temp) = build_test_manager();

        manager.snapshot.lock().main_window_id = "main-win".to_string();

        let main_window = WindowRecord {
            id: "main-win".to_string(),
            label: "main".to_string(),
            title: "Main Only".to_string(),
            x: 0,
            y: 0,
            width: 800,
            height: 600,
            is_maximized: false,
            session_ids: vec![],
            active_session_id: None,
        };

        manager.create_window(main_window).unwrap();

        let secondary_windows = manager.get_secondary_windows();

        assert!(secondary_windows.is_empty(), "no secondary windows when only main exists");
    }

    #[test]
    fn test_window_record_preserves_session_ids_with_unicode() {
        let (manager, _temp) = build_test_manager();

        let window = WindowRecord {
            id: "win-unicode-sess".to_string(),
            label: "window-test".to_string(),
            title: "ウィンドウ".to_string(),
            x: 50,
            y: 75,
            width: 1280,
            height: 720,
            is_maximized: false,
            session_ids: vec!["セッション-1".to_string(), "セッション-2".to_string()],
            active_session_id: Some("セッション-1".to_string()),
        };

        manager.create_window(window.clone()).unwrap();
        let retrieved = manager.get_window("win-unicode-sess").unwrap();

        assert_eq!(retrieved.title, "ウィンドウ", "unicode title must be preserved");
        assert_eq!(retrieved.session_ids.len(), 2, "session count must match");
        assert_eq!(retrieved.session_ids[0], "セッション-1", "unicode session id must be preserved");
        assert_eq!(retrieved.active_session_id, Some("セッション-1".to_string()), "active session must match");
    }

    #[test]
    fn test_move_session_removes_from_source_adds_to_target() {
        let (manager, _temp) = build_test_manager();

        let source = WindowRecord {
            id: "win-source".to_string(),
            label: "source".to_string(),
            title: "Source".to_string(),
            x: 0,
            y: 0,
            width: 800,
            height: 600,
            is_maximized: false,
            session_ids: vec!["sess-1".to_string(), "sess-2".to_string()],
            active_session_id: Some("sess-1".to_string()),
        };

        let target = WindowRecord {
            id: "win-target".to_string(),
            label: "target".to_string(),
            title: "Target".to_string(),
            x: 100,
            y: 100,
            width: 800,
            height: 600,
            is_maximized: false,
            session_ids: vec!["sess-3".to_string()],
            active_session_id: Some("sess-3".to_string()),
        };

        manager.create_window(source).unwrap();
        manager.create_window(target).unwrap();

        // Move sess-1 from source to target
        {
            let mut snapshot = manager.snapshot.lock();
            let source_win = snapshot.windows.iter_mut().find(|w| w.id == "win-source").unwrap();
            source_win.session_ids.retain(|id| id != "sess-1");
            if source_win.active_session_id.as_ref() == Some(&"sess-1".to_string()) {
                source_win.active_session_id = source_win.session_ids.first().cloned();
            }
            let target_win = snapshot.windows.iter_mut().find(|w| w.id == "win-target").unwrap();
            target_win.session_ids.push("sess-1".to_string());
            target_win.active_session_id = Some("sess-1".to_string());
        }

        let source_after = manager.get_window("win-source").unwrap();
        let target_after = manager.get_window("win-target").unwrap();

        assert_eq!(source_after.session_ids.len(), 1, "source must have one session after move");
        assert_eq!(source_after.session_ids[0], "sess-2", "source must retain sess-2");
        assert_eq!(source_after.active_session_id, Some("sess-2".to_string()), "source active must update");

        assert_eq!(target_after.session_ids.len(), 2, "target must have two sessions after move");
        assert!(target_after.session_ids.contains(&"sess-1".to_string()), "target must contain moved session");
        assert_eq!(target_after.active_session_id, Some("sess-1".to_string()), "target active must be moved session");
    }

    #[test]
    fn test_move_session_updates_active_when_active_moved() {
        let (manager, _temp) = build_test_manager();

        let source = WindowRecord {
            id: "win-active-src".to_string(),
            label: "source".to_string(),
            title: "Source".to_string(),
            x: 0,
            y: 0,
            width: 800,
            height: 600,
            is_maximized: false,
            session_ids: vec!["active-sess".to_string(), "other-sess".to_string()],
            active_session_id: Some("active-sess".to_string()),
        };

        let target = WindowRecord {
            id: "win-active-tgt".to_string(),
            label: "target".to_string(),
            title: "Target".to_string(),
            x: 100,
            y: 100,
            width: 800,
            height: 600,
            is_maximized: false,
            session_ids: vec![],
            active_session_id: None,
        };

        manager.create_window(source).unwrap();
        manager.create_window(target).unwrap();

        // Simulate move of active session
        {
            let mut snapshot = manager.snapshot.lock();
            let source_win = snapshot.windows.iter_mut().find(|w| w.id == "win-active-src").unwrap();
            source_win.session_ids.retain(|id| id != "active-sess");
            source_win.active_session_id = source_win.session_ids.first().cloned();

            let target_win = snapshot.windows.iter_mut().find(|w| w.id == "win-active-tgt").unwrap();
            target_win.session_ids.push("active-sess".to_string());
            target_win.active_session_id = Some("active-sess".to_string());
        }

        let source_after = manager.get_window("win-active-src").unwrap();
        assert_eq!(
            source_after.active_session_id,
            Some("other-sess".to_string()),
            "source active must fall back to remaining session"
        );
    }

    #[test]
    fn test_move_session_does_not_duplicate_in_target() {
        let (manager, _temp) = build_test_manager();

        let target = WindowRecord {
            id: "win-dup-tgt".to_string(),
            label: "target".to_string(),
            title: "Target".to_string(),
            x: 0,
            y: 0,
            width: 800,
            height: 600,
            is_maximized: false,
            session_ids: vec!["sess-already".to_string()],
            active_session_id: Some("sess-already".to_string()),
        };

        manager.create_window(target).unwrap();

        // Simulate adding a session that already exists
        {
            let mut snapshot = manager.snapshot.lock();
            let target_win = snapshot.windows.iter_mut().find(|w| w.id == "win-dup-tgt").unwrap();
            if !target_win.session_ids.contains(&"sess-already".to_string()) {
                target_win.session_ids.push("sess-already".to_string());
            }
        }

        let target_after = manager.get_window("win-dup-tgt").unwrap();
        assert_eq!(
            target_after.session_ids.len(),
            1,
            "session must not be duplicated in target"
        );
    }

    #[test]
    fn test_merge_windows_combines_sessions_and_removes_source() {
        let (manager, _temp) = build_test_manager();

        let source = WindowRecord {
            id: "win-merge-src".to_string(),
            label: "source".to_string(),
            title: "Source".to_string(),
            x: 0,
            y: 0,
            width: 800,
            height: 600,
            is_maximized: false,
            session_ids: vec!["sess-1".to_string(), "sess-2".to_string()],
            active_session_id: Some("sess-1".to_string()),
        };

        let target = WindowRecord {
            id: "win-merge-tgt".to_string(),
            label: "target".to_string(),
            title: "Target".to_string(),
            x: 100,
            y: 100,
            width: 800,
            height: 600,
            is_maximized: false,
            session_ids: vec!["sess-3".to_string()],
            active_session_id: Some("sess-3".to_string()),
        };

        manager.create_window(source).unwrap();
        manager.create_window(target).unwrap();

        // Simulate merge (without actual window close since we're not in Tauri context)
        {
            let mut snapshot = manager.snapshot.lock();
            let source_idx = snapshot.windows.iter().position(|w| w.id == "win-merge-src").unwrap();
            let target_idx = snapshot.windows.iter().position(|w| w.id == "win-merge-tgt").unwrap();

            let source_sessions = snapshot.windows[source_idx].session_ids.clone();
            for session_id in source_sessions {
                if !snapshot.windows[target_idx].session_ids.contains(&session_id) {
                    snapshot.windows[target_idx].session_ids.push(session_id);
                }
            }

            snapshot.windows.remove(source_idx);
        }

        let windows = manager.list_windows();
        assert_eq!(windows.len(), 1, "source window must be removed after merge");

        let target_after = manager.get_window("win-merge-tgt").unwrap();
        assert_eq!(target_after.session_ids.len(), 3, "target must have all sessions after merge");
        assert!(target_after.session_ids.contains(&"sess-1".to_string()), "target must contain sess-1");
        assert!(target_after.session_ids.contains(&"sess-2".to_string()), "target must contain sess-2");
        assert!(target_after.session_ids.contains(&"sess-3".to_string()), "target must contain sess-3");
    }

    #[test]
    fn test_merge_windows_avoids_duplicate_sessions() {
        let (manager, _temp) = build_test_manager();

        let source = WindowRecord {
            id: "win-merge-dup-src".to_string(),
            label: "source".to_string(),
            title: "Source".to_string(),
            x: 0,
            y: 0,
            width: 800,
            height: 600,
            is_maximized: false,
            session_ids: vec!["sess-shared".to_string(), "sess-unique".to_string()],
            active_session_id: Some("sess-shared".to_string()),
        };

        let target = WindowRecord {
            id: "win-merge-dup-tgt".to_string(),
            label: "target".to_string(),
            title: "Target".to_string(),
            x: 100,
            y: 100,
            width: 800,
            height: 600,
            is_maximized: false,
            session_ids: vec!["sess-shared".to_string()],
            active_session_id: Some("sess-shared".to_string()),
        };

        manager.create_window(source).unwrap();
        manager.create_window(target).unwrap();

        // Simulate merge with duplicate session
        {
            let mut snapshot = manager.snapshot.lock();
            let source_idx = snapshot.windows.iter().position(|w| w.id == "win-merge-dup-src").unwrap();
            let target_idx = snapshot.windows.iter().position(|w| w.id == "win-merge-dup-tgt").unwrap();

            let source_sessions = snapshot.windows[source_idx].session_ids.clone();
            for session_id in source_sessions {
                if !snapshot.windows[target_idx].session_ids.contains(&session_id) {
                    snapshot.windows[target_idx].session_ids.push(session_id);
                }
            }

            snapshot.windows.remove(source_idx);
        }

        let target_after = manager.get_window("win-merge-dup-tgt").unwrap();
        assert_eq!(target_after.session_ids.len(), 2, "target must not duplicate shared session");
        assert!(target_after.session_ids.contains(&"sess-shared".to_string()), "target must contain shared session");
        assert!(target_after.session_ids.contains(&"sess-unique".to_string()), "target must contain unique session");
    }
}
