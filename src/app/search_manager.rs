//! Global search manager for command palette search.
//!
//! Provides a singleton SearchManager that indexes Claude workspace logs
//! from ~/.claude/projects/ for quick search in the command palette.

use agentterm_search::SearchManager;
use std::sync::{
    Arc, OnceLock,
    atomic::{AtomicBool, Ordering},
};
use std::time::Duration;

/// Returns the global SearchManager singleton.
///
/// The search manager is initialized once and shared across all windows.
/// Indexing happens lazily on first search or can be triggered manually.
pub fn global_search_manager() -> Arc<SearchManager> {
    INIT_STARTED.store(true, Ordering::SeqCst);
    INSTANCE.get_or_init(init_manager).clone()
}

/// Try to get the global SearchManager without initializing.
pub fn try_global_search_manager() -> Option<Arc<SearchManager>> {
    INSTANCE.get().cloned()
}

/// Start warming the search manager on a background thread (non-blocking).
pub fn warm_search_manager_async(delay: Duration) {
    if INIT_STARTED.swap(true, Ordering::SeqCst) {
        return;
    }

    let _ = std::thread::Builder::new()
        .name("search-index-warmup".into())
        .spawn(move || {
            if delay > Duration::from_millis(0) {
                std::thread::sleep(delay);
            }
            let _ = global_search_manager();
        });
}

/// Returns true if index build has started but is not yet ready.
pub fn search_indexing_in_progress() -> bool {
    INDEXING.load(Ordering::SeqCst)
}

fn init_manager() -> Arc<SearchManager> {
    let manager = Arc::new(SearchManager::new());
    // Trigger initial indexing in background
    let manager_clone = manager.clone();
    std::thread::spawn(move || {
        INDEXING.store(true, Ordering::SeqCst);
        if let Err(e) = manager_clone.reindex() {
            eprintln!("Search index error: {}", e);
        }
        INDEXING.store(false, Ordering::SeqCst);
    });
    manager
}

static INSTANCE: OnceLock<Arc<SearchManager>> = OnceLock::new();
static INIT_STARTED: AtomicBool = AtomicBool::new(false);
static INDEXING: AtomicBool = AtomicBool::new(false);
