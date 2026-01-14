//! JSONL log indexing logic.
//!
//! Scans `~/.claude/projects/*/` and `~/.codex/sessions/` for conversation logs,
//! parses them to extract messages, and stores indexed content in memory.
//! Supports incremental indexing by tracking file modification times.

use rayon::ThreadPoolBuilder;
use rayon::prelude::*;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::fs::{self, File};
use std::io::{BufRead, BufReader, BufWriter, Write};
use std::path::{Path, PathBuf};
use std::time::SystemTime;

const INDEX_METADATA_SCHEMA_VERSION: u32 = 2; // Bumped for MessageSource addition

/// Source of the indexed message.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum MessageSource {
    /// Claude CLI conversation from ~/.claude/projects/
    Claude,
    /// Codex CLI conversation from ~/.codex/sessions/
    Codex,
}

/// Lightweight message reference for in-memory index storage.
/// Stores metadata and file offset for on-demand content loading.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MessageRef {
    /// Source of this message (Claude or Codex).
    pub source: MessageSource,
    /// Path to the source JSONL file.
    pub file_path: String,
    /// Project name derived from directory.
    pub project_name: String,
    /// Message type: "user" or "assistant".
    pub message_type: String,
    /// ISO timestamp of the message.
    pub timestamp: Option<String>,
    /// UUID/session ID of this message entry (for resumption).
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
/// Stored using JSON for compatibility and simplicity.
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
/// Can persist to disk for fast startup.
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
            eprintln!("[search] Loaded index from disk");
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

        let entry: JsonlEntry =
            serde_json::from_str(&line_str).map_err(|e| format!("Failed to parse JSON: {}", e))?;

        Ok(extract_content_text(&entry.message.and_then(|m| m.content)))
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
                    eprintln!("[search] Failed to parse index metadata: {}", e);
                }
            },
            Err(e) => {
                eprintln!("[search] Failed to read index metadata: {}", e);
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
                eprintln!("[search] Failed to create metadata directory: {}", e);
                return;
            }
        }
        let tmp_path = path.with_extension("json.tmp");
        let file = match File::create(&tmp_path) {
            Ok(f) => f,
            Err(e) => {
                eprintln!("[search] Failed to create metadata file: {}", e);
                return;
            }
        };
        let mut writer = BufWriter::new(file);
        if let Err(e) = serde_json::to_writer_pretty(&mut writer, &self.metadata) {
            eprintln!("[search] Failed to write metadata: {}", e);
            return;
        }
        if let Err(e) = writer.flush() {
            eprintln!("[search] Failed to flush metadata: {}", e);
            return;
        }
        if let Err(e) = fs::rename(&tmp_path, path) {
            eprintln!("[search] Failed to rename metadata file: {}", e);
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
                eprintln!("[search] Failed to read persisted index: {}", e);
                return false;
            }
        };

        let persisted: PersistedIndex = match serde_json::from_slice(&data) {
            Ok(p) => p,
            Err(e) => {
                eprintln!("[search] Failed to deserialize persisted index: {}", e);
                return false;
            }
        };

        if persisted.schema_version != PERSISTED_INDEX_SCHEMA_VERSION {
            eprintln!(
                "[search] Persisted index schema mismatch: expected {}, got {}",
                PERSISTED_INDEX_SCHEMA_VERSION, persisted.schema_version
            );
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

    /// Save persisted index to disk atomically.
    fn save_persisted_index(&self) {
        let Some(path) = &self.index_path else {
            return;
        };
        if let Some(parent) = path.parent() {
            if let Err(e) = fs::create_dir_all(parent) {
                eprintln!("[search] Failed to create index directory: {}", e);
                return;
            }
        }

        let persisted = PersistedIndex {
            schema_version: PERSISTED_INDEX_SCHEMA_VERSION,
            message_refs: self.message_refs.clone(),
            inverted_index: self.inverted_index.clone(),
            metadata: self.metadata.clone(),
        };

        let data = match serde_json::to_vec(&persisted) {
            Ok(d) => d,
            Err(e) => {
                eprintln!("[search] Failed to serialize index: {}", e);
                return;
            }
        };

        let tmp_path = path.with_extension("bin.tmp");
        if let Err(e) = fs::write(&tmp_path, &data) {
            eprintln!("[search] Failed to write index: {}", e);
            return;
        }
        if let Err(e) = fs::rename(&tmp_path, path) {
            eprintln!("[search] Failed to rename index file: {}", e);
        }
    }

    /// Perform reindex of JSONL logs from both Claude and Codex directories.
    /// Parses all files, builds inverted index from content, stores only MessageRefs.
    /// Content is externalized and loaded on-demand during search.
    pub fn reindex(
        &mut self,
        claude_root: &str,
        codex_root: &str,
        recent_days: u32,
    ) -> Result<(), String> {
        let cutoff = SystemTime::now()
            .duration_since(SystemTime::UNIX_EPOCH)
            .map(|d| {
                d.as_secs()
                    .saturating_sub(recent_days as u64 * 24 * 60 * 60)
            })
            .unwrap_or(0);

        // Collect files from both sources
        let mut all_files: Vec<(PathBuf, String, u64, MessageSource)> = Vec::new();

        // Scan Claude logs (~/.claude/projects/*)
        let claude_path = Path::new(claude_root);
        if claude_path.exists() {
            let claude_files = scan_claude_directory(claude_path, cutoff);
            all_files.extend(claude_files);
        }

        // Scan Codex logs (~/.codex/sessions/)
        let codex_path = Path::new(codex_root);
        if codex_path.exists() {
            let codex_files = scan_codex_directory(codex_path, cutoff);
            all_files.extend(codex_files);
        }

        if all_files.is_empty() {
            // No files found but not an error - directories might not exist yet
            self.status = IndexStatus {
                indexed: true,
                message_count: 0,
                file_count: 0,
                last_indexed_at: Some(now_iso()),
            };
            return Ok(());
        }

        let available = std::thread::available_parallelism()
            .map(|count| count.get())
            .unwrap_or(1);
        let max_threads = std::cmp::max(1, available / 2);
        let pool = ThreadPoolBuilder::new()
            .num_threads(max_threads)
            .build()
            .map_err(|e| format!("Failed to build search index pool: {}", e))?;

        let parse_results: Vec<(String, u64, Vec<ParsedMessage>, bool)> = pool.install(|| {
            all_files
                .par_iter()
                .map(|(file_path, project_name, mtime, source)| {
                    let path_str = file_path.to_string_lossy().to_string();
                    let result = match source {
                        MessageSource::Claude => parse_claude_jsonl_file(file_path, project_name),
                        MessageSource::Codex => parse_codex_file(file_path, project_name),
                    };
                    match result {
                        Ok(msgs) => (path_str, *mtime, msgs, true),
                        Err(e) => {
                            eprintln!(
                                "[search] Parse error: file={} source={:?} error={}",
                                file_path.display(),
                                source,
                                e
                            );
                            (path_str, *mtime, Vec::new(), false)
                        }
                    }
                })
                .collect()
        });

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

/// Get modification time of a file in seconds since epoch.
fn get_file_mtime(path: &Path) -> Option<u64> {
    path.metadata()
        .ok()
        .and_then(|m| m.modified().ok())
        .and_then(|t| t.duration_since(SystemTime::UNIX_EPOCH).ok())
        .map(|d| d.as_secs())
}

/// Scan Claude logs directory (~/.claude/projects/*) for JSONL files.
/// Returns (path, project_name, mtime, source) tuples.
fn scan_claude_directory(root: &Path, cutoff: u64) -> Vec<(PathBuf, String, u64, MessageSource)> {
    let entries = match fs::read_dir(root) {
        Ok(e) => e,
        Err(_) => return Vec::new(),
    };

    entries
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
                    let mtime = get_file_mtime(&file_path)?;
                    if mtime < cutoff {
                        return None;
                    }
                    Some((
                        file_path,
                        project_name.clone(),
                        mtime,
                        MessageSource::Claude,
                    ))
                })
                .collect();
            Some(files)
        })
        .flatten()
        .collect()
}

/// Scan Codex logs directory (~/.codex/sessions/) for JSONL and JSON files.
/// Handles both old format (rollout-*.json) and new format (YYYY/MM/DD/rollout-*.jsonl).
/// Returns (path, project_name, mtime, source) tuples.
fn scan_codex_directory(root: &Path, cutoff: u64) -> Vec<(PathBuf, String, u64, MessageSource)> {
    let mut files = Vec::new();

    // Scan for old format: rollout-*.json directly in sessions/
    if let Ok(entries) = fs::read_dir(root) {
        for entry in entries.flatten() {
            let path = entry.path();
            if path.is_file() && is_codex_session_file(&path) {
                if let Some(mtime) = get_file_mtime(&path) {
                    if mtime >= cutoff {
                        // Extract project name from filename or use "codex"
                        let project = extract_codex_project_from_path(&path);
                        files.push((path, project, mtime, MessageSource::Codex));
                    }
                }
            } else if path.is_dir() {
                // Scan for new format: YYYY/MM/DD/rollout-*.jsonl
                scan_codex_date_directory(&path, cutoff, &mut files);
            }
        }
    }

    files
}

/// Recursively scan date-based Codex directories (YYYY/MM/DD).
fn scan_codex_date_directory(
    dir: &Path,
    cutoff: u64,
    files: &mut Vec<(PathBuf, String, u64, MessageSource)>,
) {
    if let Ok(entries) = fs::read_dir(dir) {
        for entry in entries.flatten() {
            let path = entry.path();
            if path.is_file() && is_codex_session_file(&path) {
                if let Some(mtime) = get_file_mtime(&path) {
                    if mtime >= cutoff {
                        let project = extract_codex_project_from_path(&path);
                        files.push((path, project, mtime, MessageSource::Codex));
                    }
                }
            } else if path.is_dir() {
                // Recurse into subdirectories (YYYY -> MM -> DD)
                scan_codex_date_directory(&path, cutoff, files);
            }
        }
    }
}

/// Check if a file is a Codex session file (.json or .jsonl with rollout- prefix).
fn is_codex_session_file(path: &Path) -> bool {
    let filename = path.file_name().and_then(|n| n.to_str()).unwrap_or("");

    if !filename.starts_with("rollout-") {
        return false;
    }

    path.extension()
        .and_then(|e| e.to_str())
        .map(|e| e == "json" || e == "jsonl")
        .unwrap_or(false)
}

/// Extract project name from Codex file path.
/// Uses the cwd from session metadata if available, otherwise derives from filename.
fn extract_codex_project_from_path(path: &Path) -> String {
    // Try to extract from filename: rollout-YYYY-MM-DD-<UUID>.json
    // or rollout-YYYY-MM-DDTHH:MM:SS-<ULID>.jsonl
    path.file_stem()
        .and_then(|s| s.to_str())
        .map(|s| {
            // Remove "rollout-" prefix and take the date/id portion
            s.strip_prefix("rollout-")
                .unwrap_or(s)
                .split('-')
                .take(3) // Take YYYY-MM-DD
                .collect::<Vec<_>>()
                .join("-")
        })
        .unwrap_or_else(|| "codex".to_string())
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

/// Parse a Claude JSONL file and extract messages with file offsets.
/// Returns both MessageRef (for storage) and content (for inverted index building).
fn parse_claude_jsonl_file(path: &Path, project_name: &str) -> Result<Vec<ParsedMessage>, String> {
    let file = File::open(path).map_err(|e| format!("Failed to open {}: {}", path.display(), e))?;

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
            Err(_) => continue,
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
                source: MessageSource::Claude,
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

/// Codex JSONL entry types.
#[derive(Debug, Deserialize)]
struct CodexJsonlEntry {
    #[serde(rename = "type")]
    entry_type: String,
    timestamp: Option<String>,
    payload: serde_json::Value,
}

/// Parse a Codex session file (JSONL or JSON format).
/// Handles both old JSON format and new JSONL format.
fn parse_codex_file(path: &Path, project_name: &str) -> Result<Vec<ParsedMessage>, String> {
    let ext = path.extension().and_then(|e| e.to_str()).unwrap_or("");

    match ext {
        "jsonl" => parse_codex_jsonl_file(path, project_name),
        "json" => parse_codex_json_file(path, project_name),
        _ => Err(format!("Unsupported Codex file format: {}", ext)),
    }
}

/// Parse Codex JSONL file (new format from Nov 2025+).
/// Format: Each line is {"timestamp": "...", "type": "...", "payload": {...}}
fn parse_codex_jsonl_file(path: &Path, project_name: &str) -> Result<Vec<ParsedMessage>, String> {
    let file = File::open(path).map_err(|e| format!("Failed to open {}: {}", path.display(), e))?;
    let mut reader = BufReader::new(file);
    let file_path = path.to_string_lossy().to_string();
    let mut messages = Vec::new();
    let mut offset: u64 = 0;
    let mut line = String::new();
    let mut session_id: Option<String> = None;
    let mut actual_project_name = project_name.to_string();

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

        let entry: CodexJsonlEntry = match serde_json::from_str(&line) {
            Ok(e) => e,
            Err(_) => continue,
        };

        match entry.entry_type.as_str() {
            "session_meta" => {
                // Extract session ID and project name from session_meta
                if let Some(id) = entry.payload.get("id").and_then(|v| v.as_str()) {
                    session_id = Some(id.to_string());
                }
                if let Some(cwd) = entry.payload.get("cwd").and_then(|v| v.as_str()) {
                    // Extract project name from cwd (last component)
                    actual_project_name = Path::new(cwd)
                        .file_name()
                        .and_then(|n| n.to_str())
                        .unwrap_or(project_name)
                        .to_string();
                }
            }
            "response_item" => {
                // Extract role and content from response_item
                let role = entry
                    .payload
                    .get("role")
                    .and_then(|v| v.as_str())
                    .unwrap_or("unknown");

                if role != "user" && role != "assistant" {
                    continue;
                }

                let content_text = extract_codex_content(&entry.payload);
                if content_text.is_empty() {
                    continue;
                }

                messages.push(ParsedMessage {
                    msg_ref: MessageRef {
                        source: MessageSource::Codex,
                        file_path: file_path.clone(),
                        project_name: actual_project_name.clone(),
                        message_type: role.to_string(),
                        timestamp: entry.timestamp.clone(),
                        uuid: session_id.clone(),
                        file_offset: current_offset,
                        line_length: line_len,
                    },
                    content: content_text,
                });
            }
            "event_msg" => {
                // Extract message from event_msg
                if let Some(msg) = entry.payload.get("message").and_then(|v| v.as_str()) {
                    if !msg.is_empty() {
                        messages.push(ParsedMessage {
                            msg_ref: MessageRef {
                                source: MessageSource::Codex,
                                file_path: file_path.clone(),
                                project_name: actual_project_name.clone(),
                                message_type: "user".to_string(),
                                timestamp: entry.timestamp.clone(),
                                uuid: session_id.clone(),
                                file_offset: current_offset,
                                line_length: line_len,
                            },
                            content: msg.to_string(),
                        });
                    }
                }
            }
            _ => {}
        }
    }

    Ok(messages)
}

/// Parse Codex JSON file (old format from April 2025).
/// Format: Single JSON object with session info and items array.
fn parse_codex_json_file(path: &Path, project_name: &str) -> Result<Vec<ParsedMessage>, String> {
    let content = fs::read_to_string(path)
        .map_err(|e| format!("Failed to read {}: {}", path.display(), e))?;

    let json: serde_json::Value =
        serde_json::from_str(&content).map_err(|e| format!("Failed to parse JSON: {}", e))?;

    let file_path = path.to_string_lossy().to_string();
    let mut messages = Vec::new();

    // Extract session ID
    let session_id = json
        .get("session")
        .and_then(|s| s.get("id"))
        .and_then(|v| v.as_str())
        .map(|s| s.to_string());

    // Extract project name from session.cwd if available
    let actual_project_name = json
        .get("session")
        .and_then(|s| s.get("cwd"))
        .and_then(|v| v.as_str())
        .and_then(|cwd| Path::new(cwd).file_name())
        .and_then(|n| n.to_str())
        .unwrap_or(project_name)
        .to_string();

    // Parse items array
    if let Some(items) = json.get("items").and_then(|v| v.as_array()) {
        for item in items {
            let role = item
                .get("role")
                .and_then(|v| v.as_str())
                .unwrap_or("unknown");

            if role != "user" && role != "assistant" {
                continue;
            }

            let content_text = extract_codex_content(item);
            if content_text.is_empty() {
                continue;
            }

            let timestamp = item
                .get("created_at")
                .and_then(|v| v.as_str())
                .map(|s| s.to_string());

            messages.push(ParsedMessage {
                msg_ref: MessageRef {
                    source: MessageSource::Codex,
                    file_path: file_path.clone(),
                    project_name: actual_project_name.clone(),
                    message_type: role.to_string(),
                    timestamp,
                    uuid: session_id.clone(),
                    file_offset: 0, // Not applicable for JSON format
                    line_length: 0,
                },
                content: content_text,
            });
        }
    }

    Ok(messages)
}

/// Extract text content from Codex response payload.
fn extract_codex_content(payload: &serde_json::Value) -> String {
    // Try content array first (response_item format)
    if let Some(content_arr) = payload.get("content").and_then(|v| v.as_array()) {
        let texts: Vec<String> = content_arr
            .iter()
            .filter_map(|part| {
                let part_type = part.get("type").and_then(|v| v.as_str()).unwrap_or("");
                if part_type == "input_text" || part_type == "output_text" || part_type == "text" {
                    part.get("text")
                        .and_then(|v| v.as_str())
                        .map(|s| s.to_string())
                } else {
                    None
                }
            })
            .collect();
        return texts.join("\n");
    }

    // Try direct text field
    if let Some(text) = payload.get("text").and_then(|v| v.as_str()) {
        return text.to_string();
    }

    String::new()
}

/// Extract text content from JsonlContent.
fn extract_content_text(content: &Option<JsonlContent>) -> String {
    match content {
        Some(JsonlContent::Text(s)) => s.clone(),
        Some(JsonlContent::Array(parts)) => parts
            .iter()
            .filter_map(|p| {
                if p.part_type.as_deref() == Some("text") {
                    p.text.clone()
                } else {
                    None
                }
            })
            .collect::<Vec<_>>()
            .join("\n"),
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
    fn test_parse_claude_jsonl_user_message() {
        let temp = TempDir::new().unwrap();
        let content = r#"{"type":"user","message":{"role":"user","content":"Hello world"},"timestamp":"2025-01-01T00:00:00Z","uuid":"abc123"}"#;
        let path = create_test_jsonl(temp.path(), "test.jsonl", content);

        let messages = parse_claude_jsonl_file(&path, "test-project").unwrap();
        assert_eq!(messages.len(), 1);
        assert_eq!(messages[0].msg_ref.message_type, "user");
        assert_eq!(messages[0].msg_ref.source, MessageSource::Claude);
        assert_eq!(messages[0].content, "Hello world");
        assert_eq!(messages[0].msg_ref.project_name, "test-project");
    }

    #[test]
    fn test_parse_claude_jsonl_array_content() {
        let temp = TempDir::new().unwrap();
        let content = r#"{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Part 1"},{"type":"text","text":"Part 2"}]},"timestamp":"2025-01-01T00:00:00Z"}"#;
        let path = create_test_jsonl(temp.path(), "test.jsonl", content);

        let messages = parse_claude_jsonl_file(&path, "test-project").unwrap();
        assert_eq!(messages.len(), 1);
        assert_eq!(messages[0].msg_ref.message_type, "assistant");
        assert_eq!(messages[0].content, "Part 1\nPart 2");
    }

    #[test]
    fn test_index_reindex() {
        let temp = TempDir::new().unwrap();
        let claude_dir = temp.path().join("claude");
        let codex_dir = temp.path().join("codex");
        fs::create_dir(&claude_dir).unwrap();
        fs::create_dir(&codex_dir).unwrap();

        // Create a Claude project directory with a JSONL file
        let project_dir = claude_dir.join("test-project");
        fs::create_dir(&project_dir).unwrap();
        let content = r#"{"type":"user","message":{"role":"user","content":"Test message"},"timestamp":"2025-01-01T00:00:00Z"}"#;
        create_test_jsonl(&project_dir, "conv.jsonl", content);

        let mut index = SearchIndex::new();
        index
            .reindex(
                claude_dir.to_str().unwrap(),
                codex_dir.to_str().unwrap(),
                90,
            )
            .unwrap();

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
