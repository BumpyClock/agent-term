//! JSONL log indexing logic.
//!
//! Scans `~/.claude/projects/*/` for JSONL conversation logs,
//! parses them to extract messages, and stores indexed content in memory.
//! Supports incremental indexing by tracking file modification times.

use rayon::prelude::*;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::fs::{self, File};
use std::io::{BufRead, BufReader, BufWriter, Write};
use std::path::{Path, PathBuf};
use std::time::SystemTime;

const INDEX_METADATA_SCHEMA_VERSION: u32 = 1;

/// Lightweight message reference for in-memory index storage.
/// Stores metadata and file offset for on-demand content loading.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MessageRef {
    /// Path to the source JSONL file.
    pub file_path: String,
    /// Project name derived from directory.
    pub project_name: String,
    /// Message type: "user" or "assistant".
    pub message_type: String,
    /// ISO timestamp of the message.
    pub timestamp: Option<String>,
    /// UUID of this message entry.
    pub uuid: Option<String>,
    /// Byte offset in source file where this message line starts.
    pub file_offset: u64,
    /// Length of the JSON line in bytes.
    pub line_length: u32,
}

/// Full message with content (used during indexing and search results).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct IndexedMessage {
    /// Path to the source JSONL file.
    pub file_path: String,
    /// Project name derived from directory.
    pub project_name: String,
    /// Message type: "user" or "assistant".
    pub message_type: String,
    /// ISO timestamp of the message.
    pub timestamp: Option<String>,
    /// The actual message content (text only).
    pub content: String,
    /// UUID of this message entry.
    pub uuid: Option<String>,
}

impl IndexedMessage {
    /// Returns lowercase content for case-insensitive matching.
    #[inline]
    pub fn content_normalized(&self) -> String {
        self.content.to_lowercase()
    }
}

/// Status of the search index.
#[derive(Debug, Clone, Default)]
pub struct IndexStatus {
    /// Whether the index has been built.
    pub indexed: bool,
    /// Total number of indexed messages.
    pub message_count: usize,
    /// Number of JSONL files processed.
    pub file_count: usize,
    /// ISO timestamp of last indexing.
    pub last_indexed_at: Option<String>,
}

/// Persisted metadata for incremental indexing.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct IndexMetadata {
    /// Schema version for future migrations.
    pub schema_version: u32,
    /// Map of file_path -> (modification_time_secs, message_count).
    pub file_states: HashMap<String, (u64, usize)>,
    /// When the index was last fully built.
    pub last_full_index: Option<String>,
}

impl IndexMetadata {
    fn new() -> Self {
        Self {
            schema_version: INDEX_METADATA_SCHEMA_VERSION,
            file_states: HashMap::new(),
            last_full_index: None,
        }
    }
}

/// Persisted index data for fast startup.
/// Stored using bincode for efficient serialization.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PersistedIndex {
    /// Schema version for future migrations.
    pub schema_version: u32,
    /// Lightweight message references.
    pub message_refs: Vec<MessageRef>,
    /// Inverted index mapping terms to message indices.
    pub inverted_index: HashMap<String, Vec<usize>>,
    /// File modification tracking metadata.
    pub metadata: IndexMetadata,
}

const PERSISTED_INDEX_SCHEMA_VERSION: u32 = 1;

/// In-memory search index with inverted index for efficient term lookup.
/// Stores lightweight MessageRef instead of full content to minimize memory usage.
/// Can persist to disk for fast startup using bincode serialization.
pub struct SearchIndex {
    message_refs: Vec<MessageRef>,
    status: IndexStatus,
    inverted_index: HashMap<String, Vec<usize>>,
    metadata: IndexMetadata,
    metadata_path: Option<PathBuf>,
    index_path: Option<PathBuf>,
}

impl SearchIndex {
    pub fn new() -> Self {
        Self {
            message_refs: Vec::new(),
            status: IndexStatus::default(),
            inverted_index: HashMap::new(),
            metadata: IndexMetadata::new(),
            metadata_path: None,
            index_path: None,
        }
    }

    /// Create a new SearchIndex with persistence enabled.
    /// Will attempt to load existing index from disk for fast startup.
    pub fn with_persistence(metadata_path: PathBuf, index_path: PathBuf) -> Self {
        let mut index = Self::new();
        index.metadata_path = Some(metadata_path);
        index.index_path = Some(index_path);

        if index.try_load_persisted_index() {
            crate::diagnostics::log("search_index_loaded_from_disk".to_string());
        } else {
            index.load_metadata();
        }
        index
    }

    /// Backwards-compatible constructor (metadata only, no persisted index).
    pub fn with_metadata_path(metadata_path: PathBuf) -> Self {
        let index_path = metadata_path.with_file_name("index.bin");
        Self::with_persistence(metadata_path, index_path)
    }

    /// Get message indices containing a specific term.
    pub fn get_term_indices(&self, term: &str) -> Option<&Vec<usize>> {
        self.inverted_index.get(term)
    }

    /// Get a message reference by index.
    pub fn get_message_ref(&self, idx: usize) -> Option<&MessageRef> {
        self.message_refs.get(idx)
    }

    /// Load content from disk for a message reference.
    pub fn load_content(&self, msg_ref: &MessageRef) -> Result<String, String> {
        use std::io::{Read, Seek, SeekFrom};

        let file = File::open(&msg_ref.file_path)
            .map_err(|e| format!("Failed to open {}: {}", msg_ref.file_path, e))?;
        let mut reader = BufReader::new(file);
        reader
            .seek(SeekFrom::Start(msg_ref.file_offset))
            .map_err(|e| format!("Failed to seek: {}", e))?;

        let mut line = vec![0u8; msg_ref.line_length as usize];
        reader
            .read_exact(&mut line)
            .map_err(|e| format!("Failed to read line: {}", e))?;

        let line_str =
            String::from_utf8(line).map_err(|e| format!("Invalid UTF-8 in line: {}", e))?;

        let entry: JsonlEntry = serde_json::from_str(&line_str)
            .map_err(|e| format!("Failed to parse JSON: {}", e))?;

        Ok(extract_content_text(
            &entry.message.and_then(|m| m.content),
        ))
    }

    pub fn status(&self) -> IndexStatus {
        self.status.clone()
    }

    #[allow(dead_code)]
    pub fn message_refs(&self) -> &[MessageRef] {
        &self.message_refs
    }

    /// Load metadata from disk if available.
    fn load_metadata(&mut self) {
        let Some(path) = &self.metadata_path else {
            return;
        };
        if !path.exists() {
            return;
        }
        match fs::read_to_string(path) {
            Ok(data) => match serde_json::from_str::<IndexMetadata>(&data) {
                Ok(meta) => {
                    self.metadata = meta;
                }
                Err(e) => {
                    crate::diagnostics::log(format!("index_metadata_parse_error: {}", e));
                }
            },
            Err(e) => {
                crate::diagnostics::log(format!("index_metadata_read_error: {}", e));
            }
        }
    }

    /// Save metadata to disk atomically.
    fn save_metadata(&self) {
        let Some(path) = &self.metadata_path else {
            return;
        };
        if let Some(parent) = path.parent() {
            if let Err(e) = fs::create_dir_all(parent) {
                crate::diagnostics::log(format!("index_metadata_dir_error: {}", e));
                return;
            }
        }
        let tmp_path = path.with_extension("json.tmp");
        let file = match File::create(&tmp_path) {
            Ok(f) => f,
            Err(e) => {
                crate::diagnostics::log(format!("index_metadata_create_error: {}", e));
                return;
            }
        };
        let mut writer = BufWriter::new(file);
        if let Err(e) = serde_json::to_writer_pretty(&mut writer, &self.metadata) {
            crate::diagnostics::log(format!("index_metadata_write_error: {}", e));
            return;
        }
        if let Err(e) = writer.flush() {
            crate::diagnostics::log(format!("index_metadata_flush_error: {}", e));
            return;
        }
        if let Err(e) = fs::rename(&tmp_path, path) {
            crate::diagnostics::log(format!("index_metadata_rename_error: {}", e));
        }
    }

    /// Try to load persisted index from disk.
    /// Returns true if successful, false otherwise.
    fn try_load_persisted_index(&mut self) -> bool {
        let Some(path) = &self.index_path else {
            return false;
        };
        if !path.exists() {
            return false;
        }

        let data = match fs::read(path) {
            Ok(d) => d,
            Err(e) => {
                crate::diagnostics::log(format!("persisted_index_read_error: {}", e));
                return false;
            }
        };

        let persisted: PersistedIndex = match bincode::deserialize(&data) {
            Ok(p) => p,
            Err(e) => {
                crate::diagnostics::log(format!("persisted_index_deserialize_error: {}", e));
                return false;
            }
        };

        if persisted.schema_version != PERSISTED_INDEX_SCHEMA_VERSION {
            crate::diagnostics::log(format!(
                "persisted_index_schema_mismatch: expected {}, got {}",
                PERSISTED_INDEX_SCHEMA_VERSION, persisted.schema_version
            ));
            return false;
        }

        self.message_refs = persisted.message_refs;
        self.inverted_index = persisted.inverted_index;
        self.metadata = persisted.metadata;
        self.status = IndexStatus {
            indexed: true,
            message_count: self.message_refs.len(),
            file_count: self.metadata.file_states.len(),
            last_indexed_at: self.metadata.last_full_index.clone(),
        };

        true
    }

    /// Save persisted index to disk atomically using bincode.
    fn save_persisted_index(&self) {
        let Some(path) = &self.index_path else {
            return;
        };
        if let Some(parent) = path.parent() {
            if let Err(e) = fs::create_dir_all(parent) {
                crate::diagnostics::log(format!("persisted_index_dir_error: {}", e));
                return;
            }
        }

        let persisted = PersistedIndex {
            schema_version: PERSISTED_INDEX_SCHEMA_VERSION,
            message_refs: self.message_refs.clone(),
            inverted_index: self.inverted_index.clone(),
            metadata: self.metadata.clone(),
        };

        let data = match bincode::serialize(&persisted) {
            Ok(d) => d,
            Err(e) => {
                crate::diagnostics::log(format!("persisted_index_serialize_error: {}", e));
                return;
            }
        };

        let tmp_path = path.with_extension("bin.tmp");
        if let Err(e) = fs::write(&tmp_path, &data) {
            crate::diagnostics::log(format!("persisted_index_write_error: {}", e));
            return;
        }
        if let Err(e) = fs::rename(&tmp_path, path) {
            crate::diagnostics::log(format!("persisted_index_rename_error: {}", e));
        }
    }

    /// Get modification time of a file in seconds since epoch.
    fn get_file_mtime(path: &Path) -> Option<u64> {
        path.metadata()
            .ok()
            .and_then(|m| m.modified().ok())
            .and_then(|t| t.duration_since(SystemTime::UNIX_EPOCH).ok())
            .map(|d| d.as_secs())
    }

    /// Perform reindex of JSONL logs.
    /// Parses all files, builds inverted index from content, stores only MessageRefs.
    /// Content is externalized and loaded on-demand during search.
    pub fn reindex(&mut self, log_root: &str, recent_days: u32) -> Result<(), String> {
        let root_path = Path::new(log_root);
        if !root_path.exists() {
            return Err(format!("Log root does not exist: {}", log_root));
        }

        let cutoff = SystemTime::now()
            .duration_since(SystemTime::UNIX_EPOCH)
            .map(|d| d.as_secs().saturating_sub(recent_days as u64 * 24 * 60 * 60))
            .unwrap_or(0);

        let entries = fs::read_dir(root_path)
            .map_err(|e| format!("Failed to read log root: {}", e))?;

        let all_files: Vec<(PathBuf, String, u64)> = entries
            .flatten()
            .filter_map(|entry| {
                let project_path = entry.path();
                if !project_path.is_dir() {
                    return None;
                }
                let project_name = project_path
                    .file_name()
                    .and_then(|n| n.to_str())
                    .unwrap_or("unknown")
                    .to_string();

                let files: Vec<_> = fs::read_dir(&project_path)
                    .ok()?
                    .flatten()
                    .filter_map(|file_entry| {
                        let file_path = file_entry.path();
                        if !is_jsonl_file(&file_path) {
                            return None;
                        }
                        let mtime = Self::get_file_mtime(&file_path)?;
                        if mtime < cutoff {
                            return None;
                        }
                        Some((file_path, project_name.clone(), mtime))
                    })
                    .collect();
                Some(files)
            })
            .flatten()
            .collect();

        let parse_results: Vec<(String, u64, Vec<ParsedMessage>, bool)> = all_files
            .par_iter()
            .map(|(file_path, project_name, mtime)| {
                let path_str = file_path.to_string_lossy().to_string();
                match parse_jsonl_file_with_offsets(file_path, project_name) {
                    Ok(msgs) => (path_str, *mtime, msgs, true),
                    Err(e) => {
                        crate::diagnostics::log(format!(
                            "jsonl_parse_error file={} project={} error={}",
                            file_path.display(),
                            project_name,
                            e
                        ));
                        (path_str, *mtime, Vec::new(), false)
                    }
                }
            })
            .collect();

        let mut all_refs: Vec<MessageRef> = Vec::new();
        let mut inverted_index: HashMap<String, Vec<usize>> = HashMap::new();
        let mut new_file_states: HashMap<String, (u64, usize)> = HashMap::new();
        let mut file_count = 0;

        for (path_str, mtime, parsed_msgs, success) in parse_results {
            if !success {
                continue;
            }
            file_count += 1;
            let msg_count = parsed_msgs.len();
            new_file_states.insert(path_str, (mtime, msg_count));

            let base_idx = all_refs.len();
            let file_index = build_inverted_index_from_parsed(&parsed_msgs, base_idx);
            inverted_index = merge_inverted_indices(inverted_index, file_index);

            for pm in parsed_msgs {
                all_refs.push(pm.msg_ref);
            }
        }

        let message_count = all_refs.len();
        self.message_refs = all_refs;
        self.inverted_index = inverted_index;
        self.metadata.file_states = new_file_states;
        self.metadata.last_full_index = Some(now_iso());
        self.status = IndexStatus {
            indexed: true,
            message_count,
            file_count,
            last_indexed_at: Some(now_iso()),
        };

        self.save_metadata();
        self.save_persisted_index();
        Ok(())
    }
}

impl Default for SearchIndex {
    fn default() -> Self {
        Self::new()
    }
}

/// Check if a path is a JSONL file.
fn is_jsonl_file(path: &Path) -> bool {
    path.extension()
        .and_then(|e| e.to_str())
        .map(|e| e == "jsonl")
        .unwrap_or(false)
}

/// Tokenize normalized content into terms for indexing.
fn tokenize(content: &str) -> impl Iterator<Item = &str> {
    content.split_whitespace().filter(|term| term.len() >= 2)
}

/// Build an inverted index from parsed messages (has content available).
fn build_inverted_index_from_parsed(
    messages: &[ParsedMessage],
    base_idx: usize,
) -> HashMap<String, Vec<usize>> {
    let mut index: HashMap<String, Vec<usize>> = HashMap::new();
    for (i, msg) in messages.iter().enumerate() {
        let idx = base_idx + i;
        let normalized = msg.content.to_lowercase();
        for term in tokenize(&normalized) {
            index.entry(term.to_string()).or_default().push(idx);
        }
    }
    index
}

/// Merge two inverted indices.
fn merge_inverted_indices(
    mut base: HashMap<String, Vec<usize>>,
    other: HashMap<String, Vec<usize>>,
) -> HashMap<String, Vec<usize>> {
    for (term, indices) in other {
        base.entry(term).or_default().extend(indices);
    }
    base
}

/// Build an inverted index mapping terms to message indices.
#[allow(dead_code)]
fn build_inverted_index(messages: &[IndexedMessage]) -> HashMap<String, Vec<usize>> {
    let mut index: HashMap<String, Vec<usize>> = HashMap::new();
    for (idx, msg) in messages.iter().enumerate() {
        let normalized = msg.content_normalized();
        for term in tokenize(&normalized) {
            index.entry(term.to_string()).or_default().push(idx);
        }
    }
    index
}

/// JSONL entry structure matching Claude's log format.
#[derive(Debug, Deserialize)]
struct JsonlEntry {
    #[serde(rename = "type")]
    entry_type: Option<String>,
    message: Option<JsonlMessage>,
    timestamp: Option<String>,
    uuid: Option<String>,
}

#[derive(Debug, Deserialize)]
struct JsonlMessage {
    role: Option<String>,
    content: Option<JsonlContent>,
}

#[derive(Debug, Deserialize)]
#[serde(untagged)]
enum JsonlContent {
    Text(String),
    Array(Vec<JsonlContentPart>),
}

#[derive(Debug, Deserialize)]
struct JsonlContentPart {
    #[serde(rename = "type")]
    part_type: Option<String>,
    text: Option<String>,
}

/// Parsed message with reference and content for indexing.
struct ParsedMessage {
    msg_ref: MessageRef,
    content: String,
}

/// Parse a JSONL file and extract messages with file offsets.
/// Returns both MessageRef (for storage) and content (for inverted index building).
fn parse_jsonl_file_with_offsets(
    path: &Path,
    project_name: &str,
) -> Result<Vec<ParsedMessage>, String> {
    use std::io::BufRead;

    let file =
        File::open(path).map_err(|e| format!("Failed to open {}: {}", path.display(), e))?;

    let mut reader = BufReader::new(file);
    let file_path = path.to_string_lossy().to_string();
    let mut messages = Vec::new();
    let mut offset: u64 = 0;
    let mut line = String::new();

    loop {
        line.clear();
        let bytes_read = reader
            .read_line(&mut line)
            .map_err(|e| format!("Failed to read line: {}", e))?;

        if bytes_read == 0 {
            break;
        }

        let line_len = bytes_read as u32;
        let current_offset = offset;
        offset += bytes_read as u64;

        if line.trim().is_empty() {
            continue;
        }

        let entry: JsonlEntry = match serde_json::from_str(&line) {
            Ok(e) => e,
            Err(e) => {
                static PARSE_ERROR_COUNT: std::sync::atomic::AtomicU32 =
                    std::sync::atomic::AtomicU32::new(0);
                let count = PARSE_ERROR_COUNT.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
                if count < 10 {
                    crate::diagnostics::log(format!(
                        "jsonl_entry_parse_error file={} error={}",
                        path.display(),
                        e
                    ));
                }
                continue;
            }
        };

        let (message_type, content_text) = match (&entry.entry_type, &entry.message) {
            (Some(t), Some(msg)) if t == "user" || t == "assistant" => {
                let role = msg.role.as_deref().unwrap_or(t.as_str());
                let text = extract_content_text(&msg.content);
                if text.is_empty() {
                    continue;
                }
                (role.to_string(), text)
            }
            _ => continue,
        };

        messages.push(ParsedMessage {
            msg_ref: MessageRef {
                file_path: file_path.clone(),
                project_name: project_name.to_string(),
                message_type,
                timestamp: entry.timestamp,
                uuid: entry.uuid,
                file_offset: current_offset,
                line_length: line_len,
            },
            content: content_text,
        });
    }

    Ok(messages)
}

/// Parse a JSONL file and extract messages (legacy function for tests).
#[allow(dead_code)]
fn parse_jsonl_file(path: &Path, project_name: &str) -> Result<Vec<IndexedMessage>, String> {
    let file = File::open(path)
        .map_err(|e| format!("Failed to open {}: {}", path.display(), e))?;

    let reader = BufReader::new(file);
    let file_path = path.to_string_lossy().to_string();
    let mut messages = Vec::new();

    for line_result in reader.lines() {
        let line = match line_result {
            Ok(l) => l,
            Err(e) => {
                crate::diagnostics::log(format!(
                    "jsonl_line_read_error file={} error={}",
                    path.display(),
                    e
                ));
                continue;
            }
        };

        if line.trim().is_empty() {
            continue;
        }

        let entry: JsonlEntry = match serde_json::from_str(&line) {
            Ok(e) => e,
            Err(e) => {
                static PARSE_ERROR_COUNT: std::sync::atomic::AtomicU32 =
                    std::sync::atomic::AtomicU32::new(0);
                let count = PARSE_ERROR_COUNT.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
                if count < 10 {
                    crate::diagnostics::log(format!(
                        "jsonl_entry_parse_error file={} error={}",
                        path.display(),
                        e
                    ));
                }
                continue;
            }
        };

        let (message_type, content_text) = match (&entry.entry_type, &entry.message) {
            (Some(t), Some(msg)) if t == "user" || t == "assistant" => {
                let role = msg.role.as_deref().unwrap_or(t.as_str());
                let text = extract_content_text(&msg.content);
                if text.is_empty() {
                    continue;
                }
                (role.to_string(), text)
            }
            _ => continue,
        };

        messages.push(IndexedMessage {
            file_path: file_path.clone(),
            project_name: project_name.to_string(),
            message_type,
            timestamp: entry.timestamp,
            content: content_text,
            uuid: entry.uuid,
        });
    }

    Ok(messages)
}

/// Extract text content from JsonlContent.
fn extract_content_text(content: &Option<JsonlContent>) -> String {
    match content {
        Some(JsonlContent::Text(s)) => s.clone(),
        Some(JsonlContent::Array(parts)) => {
            parts
                .iter()
                .filter_map(|p| {
                    if p.part_type.as_deref() == Some("text") {
                        p.text.clone()
                    } else {
                        None
                    }
                })
                .collect::<Vec<_>>()
                .join("\n")
        }
        None => String::new(),
    }
}

/// Get current time as ISO 8601 string.
fn now_iso() -> String {
    time::OffsetDateTime::now_utc()
        .format(&time::format_description::well_known::Rfc3339)
        .unwrap_or_else(|_| String::new())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;
    use std::path::PathBuf;
    use tempfile::TempDir;

    fn create_test_jsonl(dir: &Path, filename: &str, content: &str) -> PathBuf {
        let path = dir.join(filename);
        let mut file = fs::File::create(&path).unwrap();
        file.write_all(content.as_bytes()).unwrap();
        path
    }

    #[test]
    fn test_parse_jsonl_user_message() {
        let temp = TempDir::new().unwrap();
        let content = r#"{"type":"user","message":{"role":"user","content":"Hello world"},"timestamp":"2025-01-01T00:00:00Z","uuid":"abc123"}"#;
        let path = create_test_jsonl(temp.path(), "test.jsonl", content);

        let messages = parse_jsonl_file(&path, "test-project").unwrap();
        assert_eq!(messages.len(), 1);
        assert_eq!(messages[0].message_type, "user");
        assert_eq!(messages[0].content, "Hello world");
        assert_eq!(messages[0].project_name, "test-project");
    }

    #[test]
    fn test_parse_jsonl_array_content() {
        let temp = TempDir::new().unwrap();
        let content = r#"{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Part 1"},{"type":"text","text":"Part 2"}]},"timestamp":"2025-01-01T00:00:00Z"}"#;
        let path = create_test_jsonl(temp.path(), "test.jsonl", content);

        let messages = parse_jsonl_file(&path, "test-project").unwrap();
        assert_eq!(messages.len(), 1);
        assert_eq!(messages[0].message_type, "assistant");
        assert_eq!(messages[0].content, "Part 1\nPart 2");
    }

    #[test]
    fn test_parse_jsonl_skips_invalid_lines() {
        let temp = TempDir::new().unwrap();
        let content = r#"not valid json
{"type":"user","message":{"role":"user","content":"Valid"},"timestamp":"2025-01-01T00:00:00Z"}
{"type":"other","message":null}"#;
        let path = create_test_jsonl(temp.path(), "test.jsonl", content);

        let messages = parse_jsonl_file(&path, "test-project").unwrap();
        assert_eq!(messages.len(), 1);
        assert_eq!(messages[0].content, "Valid");
    }

    #[test]
    fn test_index_reindex() {
        let temp = TempDir::new().unwrap();
        let project_dir = temp.path().join("test-project");
        fs::create_dir(&project_dir).unwrap();

        let content = r#"{"type":"user","message":{"role":"user","content":"Test message"},"timestamp":"2025-01-01T00:00:00Z"}"#;
        create_test_jsonl(&project_dir, "conv.jsonl", content);

        let mut index = SearchIndex::new();
        index.reindex(temp.path().to_str().unwrap(), 90).unwrap();

        assert!(index.status().indexed);
        assert_eq!(index.status().message_count, 1);
        assert_eq!(index.status().file_count, 1);
    }

    #[test]
    fn test_is_jsonl_file() {
        assert!(is_jsonl_file(Path::new("test.jsonl")));
        assert!(!is_jsonl_file(Path::new("test.json")));
        assert!(!is_jsonl_file(Path::new("test.txt")));
        assert!(!is_jsonl_file(Path::new("test")));
    }
}
