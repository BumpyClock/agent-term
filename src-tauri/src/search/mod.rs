//! Global Search module for indexing and searching Claude JSONL conversation logs.
//!
//! Provides in-memory indexing of conversation logs from `~/.claude/projects/*/`
//! and fuzzy search with snippet previews.

use parking_lot::Mutex;
use serde::{Deserialize, Serialize};
use std::sync::Arc;
use tauri::State;

mod index;
mod query;

pub use index::IndexStatus;
pub use query::SearchResult;

use index::SearchIndex;
use query::SearchEngine;

/// Configuration for the search index.
#[derive(Debug, Clone)]
pub struct SearchConfig {
    /// Number of days to look back when indexing (default: 90).
    pub recent_days: u32,
    /// Root directory for Claude logs (default: ~/.claude/projects).
    pub log_root: Option<String>,
}

impl Default for SearchConfig {
    fn default() -> Self {
        Self {
            recent_days: 90,
            log_root: None,
        }
    }
}

/// Manages the search index and query operations.
pub struct SearchManager {
    index: Mutex<SearchIndex>,
    engine: SearchEngine,
    config: SearchConfig,
}

impl SearchManager {
    /// Create a new SearchManager with default configuration.
    pub fn new() -> Self {
        Self::with_config(SearchConfig::default())
    }

    /// Create a new SearchManager with custom configuration.
    pub fn with_config(config: SearchConfig) -> Self {
        Self {
            index: Mutex::new(SearchIndex::new()),
            engine: SearchEngine::new(),
            config,
        }
    }

    /// Get the current indexing status.
    pub fn status(&self) -> IndexStatus {
        self.index.lock().status()
    }

    /// Trigger a full re-index of all JSONL logs.
    pub fn reindex(&self) -> Result<IndexStatus, String> {
        let log_root = self.config.log_root.clone().unwrap_or_else(|| {
            dirs::home_dir()
                .map(|h| h.join(".claude").join("projects").to_string_lossy().to_string())
                .unwrap_or_else(|| "~/.claude/projects".to_string())
        });

        let mut index = self.index.lock();
        index.reindex(&log_root, self.config.recent_days)?;
        Ok(index.status())
    }

    /// Search indexed messages with fuzzy matching.
    pub fn search(&self, query: &str, limit: usize) -> Vec<SearchResult> {
        let index = self.index.lock();
        self.engine.search(&index, query, limit)
    }
}

impl Default for SearchManager {
    fn default() -> Self {
        Self::new()
    }
}

/// Build a new SearchManager for use with Tauri.
pub fn build_search_manager() -> Result<Arc<SearchManager>, String> {
    Ok(Arc::new(SearchManager::new()))
}

// ============================================================================
// Tauri Commands
// ============================================================================

/// Response type for index status command.
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct IndexStatusResponse {
    pub indexed: bool,
    pub message_count: usize,
    pub file_count: usize,
    pub last_indexed_at: Option<String>,
}

impl From<IndexStatus> for IndexStatusResponse {
    fn from(status: IndexStatus) -> Self {
        Self {
            indexed: status.indexed,
            message_count: status.message_count,
            file_count: status.file_count,
            last_indexed_at: status.last_indexed_at,
        }
    }
}

/// Response type for search results.
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SearchResultResponse {
    pub file_path: String,
    pub project_name: String,
    pub message_type: String,
    pub timestamp: Option<String>,
    pub snippet: String,
    pub match_positions: Vec<(usize, usize)>,
    pub score: f32,
}

impl From<SearchResult> for SearchResultResponse {
    fn from(result: SearchResult) -> Self {
        Self {
            file_path: result.file_path,
            project_name: result.project_name,
            message_type: result.message_type,
            timestamp: result.timestamp,
            snippet: result.snippet,
            match_positions: result.match_positions,
            score: result.score,
        }
    }
}

/// Get the current indexing status.
#[tauri::command(rename_all = "camelCase")]
pub fn search_index_status(
    state: State<'_, Arc<SearchManager>>,
) -> Result<IndexStatusResponse, String> {
    Ok(state.status().into())
}

/// Trigger a full re-index of all JSONL logs.
#[tauri::command(rename_all = "camelCase")]
pub fn search_reindex(state: State<'_, Arc<SearchManager>>) -> Result<IndexStatusResponse, String> {
    state.reindex().map(|s| s.into())
}

/// Perform a fuzzy search across indexed messages.
#[tauri::command(rename_all = "camelCase")]
pub fn search_query(
    state: State<'_, Arc<SearchManager>>,
    query: String,
    limit: Option<usize>,
) -> Result<Vec<SearchResultResponse>, String> {
    let limit = limit.unwrap_or(50);
    if query.trim().is_empty() {
        return Ok(vec![]);
    }
    Ok(state.search(&query, limit).into_iter().map(|r| r.into()).collect())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_search_manager_creation() {
        let manager = SearchManager::new();
        let status = manager.status();
        assert!(!status.indexed);
        assert_eq!(status.message_count, 0);
    }

    #[test]
    fn test_empty_query_returns_empty() {
        let manager = SearchManager::new();
        let results = manager.search("", 10);
        assert!(results.is_empty());

        let results = manager.search("   ", 10);
        assert!(results.is_empty());
    }
}
