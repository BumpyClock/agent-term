// ABOUTME: Manages the live PTY runtime for a session, including the child process and I/O threads.
// ABOUTME: Handles shutdown and cleanup of session resources when stopped or dropped.

use std::collections::{HashSet, VecDeque};
use std::io::Write;
use std::sync::mpsc::Sender;
use std::sync::Arc;
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use crate::diagnostics;
use parking_lot::Mutex;
use portable_pty::{Child, MasterPty, PtySize};

/// Runtime state for a live PTY-backed session.
///
/// Example:
/// ```rust,ignore
/// let runtime = SessionRuntime::new(
///     master,
///     writer,
///     child,
///     reader_thread,
///     shutdown_tx,
/// );
/// ```
pub struct SessionRuntime {
    id: String,
    master: Option<Box<dyn MasterPty + Send>>,
    writer: Option<Box<dyn Write + Send>>,
    child: Box<dyn Child + Send + Sync>,
    reader_thread: Option<JoinHandle<()>>,
    shutdown_tx: Sender<()>,
    shutdown_called: bool,
    scrollback: Arc<Mutex<ScrollbackBuffer>>,
    subscribers: Arc<Mutex<HashSet<String>>>,
}

impl SessionRuntime {
    pub fn new(
        master: Box<dyn MasterPty + Send>,
        writer: Box<dyn Write + Send>,
        child: Box<dyn Child + Send + Sync>,
        reader_thread: JoinHandle<()>,
        shutdown_tx: Sender<()>,
        id: String,
        scrollback: Arc<Mutex<ScrollbackBuffer>>,
        subscribers: Arc<Mutex<HashSet<String>>>,
    ) -> Self {
        Self {
            id,
            master: Some(master),
            writer: Some(writer),
            child,
            reader_thread: Some(reader_thread),
            shutdown_tx,
            shutdown_called: false,
            scrollback,
            subscribers,
        }
    }

    pub fn get_scrollback(&self) -> Vec<u8> {
        self.scrollback.lock().get_all()
    }

    /// Add a window as a subscriber to this session's output events.
    pub fn add_subscriber(&self, window_label: String) {
        self.subscribers.lock().insert(window_label);
    }

    /// Remove a window from the subscriber list.
    pub fn remove_subscriber(&self, window_label: String) {
        self.subscribers.lock().remove(&window_label);
    }

    /// Get all currently subscribed window labels.
    pub fn get_subscribers(&self) -> Vec<String> {
        self.subscribers.lock().iter().cloned().collect()
    }

    /// Get the number of windows currently subscribed to this session.
    pub fn subscriber_count(&self) -> usize {
        self.subscribers.lock().len()
    }

    pub fn write(&mut self, data: &[u8]) -> Result<(), String> {
        let writer = self
            .writer
            .as_mut()
            .ok_or_else(|| "writer unavailable".to_string())?;
        writer
            .write_all(data)
            .map_err(|e| format!("failed to write: {}", e))?;
        writer
            .flush()
            .map_err(|e| format!("failed to flush: {}", e))?;
        Ok(())
    }

    pub fn resize(&mut self, rows: u16, cols: u16) -> Result<(), String> {
        self.master
            .as_mut()
            .ok_or_else(|| "master unavailable".to_string())?
            .resize(PtySize {
                rows,
                cols,
                pixel_width: 0,
                pixel_height: 0,
            })
            .map_err(|e| format!("failed to resize: {}", e))
    }

    pub fn shutdown(&mut self) {
        if self.shutdown_called {
            diagnostics::log(format!(
                "session_runtime_shutdown id={} status=already-called os={}",
                self.id,
                std::env::consts::OS
            ));
            return;
        }
        self.shutdown_called = true;
        diagnostics::log(format!(
            "session_runtime_shutdown id={} status=begin os={}",
            self.id,
            std::env::consts::OS
        ));
        // Close writer/master early to unblock reader thread on Windows.
        if let Some(writer) = self.writer.take() {
            drop(writer);
            diagnostics::log(format!(
                "session_runtime_shutdown id={} writer_dropped os={}",
                self.id,
                std::env::consts::OS
            ));
        }
        if let Some(master) = self.master.take() {
            drop(master);
            diagnostics::log(format!(
                "session_runtime_shutdown id={} master_dropped os={}",
                self.id,
                std::env::consts::OS
            ));
        }
        let _ = self.shutdown_tx.send(());

        // Retry kill with exponential backoff
        for attempt in 0..3 {
            match self.child.kill() {
                Ok(_) => {
                    diagnostics::log(format!(
                        "session_runtime_shutdown id={} kill_sent attempt={} os={}",
                        self.id, attempt, std::env::consts::OS
                    ));
                    break;
                }
                Err(e) => {
                    diagnostics::log(format!(
                        "session_runtime_shutdown id={} kill_error={} attempt={} os={}",
                        self.id, e, attempt, std::env::consts::OS
                    ));
                    if attempt < 2 {
                        std::thread::sleep(Duration::from_millis(100 * (1 << attempt)));
                    }
                }
            }
        }

        // Wait for process with timeout
        let deadline = Instant::now() + Duration::from_secs(5);
        while Instant::now() < deadline {
            match self.child.try_wait() {
                Ok(Some(status)) => {
                    diagnostics::log(format!(
                        "session_runtime_shutdown id={} exited={:?} os={}",
                        self.id, status, std::env::consts::OS
                    ));
                    break;
                }
                Ok(None) => {
                    std::thread::sleep(Duration::from_millis(100));
                }
                Err(e) => {
                    diagnostics::log(format!(
                        "session_runtime_shutdown id={} try_wait_error={} os={}",
                        self.id, e, std::env::consts::OS
                    ));
                    break;
                }
            }
        }
        diagnostics::log(format!(
            "session_runtime_shutdown id={} before_join os={}",
            self.id,
            std::env::consts::OS
        ));
        if let Some(handle) = self.reader_thread.take() {
            match handle.join() {
                Ok(_) => diagnostics::log(format!(
                    "session_runtime_shutdown id={} reader_thread_joined os={}",
                    self.id,
                    std::env::consts::OS
                )),
                Err(_) => diagnostics::log(format!(
                    "session_runtime_shutdown id={} reader_thread_panic os={}",
                    self.id,
                    std::env::consts::OS
                )),
            };
        }
        diagnostics::log(format!(
            "session_runtime_shutdown id={} status=end os={}",
            self.id,
            std::env::consts::OS
        ));
    }
}

impl Drop for SessionRuntime {
    fn drop(&mut self) {
        self.shutdown();
    }
}

/// Circular buffer for storing terminal output history.
///
/// This buffer captures all terminal output so it can be replayed when windows
/// reconnect to running sessions. It enforces a maximum size limit by evicting
/// the oldest chunks when the total size exceeds the limit.
///
/// Example:
/// ```rust,ignore
/// let mut buffer = ScrollbackBuffer::new(10 * 1024 * 1024); // 10MB
/// buffer.append(b"hello world");
/// let all_data = buffer.get_all();
/// ```
pub struct ScrollbackBuffer {
    chunks: VecDeque<Vec<u8>>,
    total_bytes: usize,
    max_bytes: usize,
}

impl ScrollbackBuffer {
    /// Creates a new scrollback buffer with the specified maximum size in bytes.
    pub fn new(max_bytes: usize) -> Self {
        Self {
            chunks: VecDeque::new(),
            total_bytes: 0,
            max_bytes,
        }
    }

    /// Appends data to the buffer, evicting oldest chunks if necessary to stay under the size limit.
    pub fn append(&mut self, data: &[u8]) {
        if data.is_empty() {
            return;
        }

        let chunk = data.to_vec();
        let chunk_size = chunk.len();
        self.chunks.push_back(chunk);
        self.total_bytes += chunk_size;

        while self.total_bytes > self.max_bytes && !self.chunks.is_empty() {
            if let Some(oldest) = self.chunks.pop_front() {
                self.total_bytes -= oldest.len();
            }
        }
    }

    /// Returns all buffered data concatenated into a single vector.
    pub fn get_all(&self) -> Vec<u8> {
        let mut result = Vec::with_capacity(self.total_bytes);
        for chunk in &self.chunks {
            result.extend_from_slice(chunk);
        }
        result
    }

    /// Clears all buffered data.
    pub fn clear(&mut self) {
        self.chunks.clear();
        self.total_bytes = 0;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_scrollback_buffer_append_and_get() {
        let mut buf = ScrollbackBuffer::new(100);
        buf.append(b"hello");
        assert_eq!(
            buf.get_all(),
            b"hello",
            "buffer should contain appended data"
        );
    }

    #[test]
    fn test_scrollback_buffer_multiple_appends() {
        let mut buf = ScrollbackBuffer::new(100);
        buf.append(b"hello");
        buf.append(b" ");
        buf.append(b"world");
        assert_eq!(
            buf.get_all(),
            b"hello world",
            "buffer should concatenate multiple chunks"
        );
    }

    #[test]
    fn test_scrollback_buffer_overflow_evicts_oldest() {
        let mut buf = ScrollbackBuffer::new(10);
        buf.append(b"12345");
        buf.append(b"67890");
        buf.append(b"abcde");

        assert!(
            buf.total_bytes <= 10,
            "buffer should not exceed max size after overflow"
        );

        let result = buf.get_all();
        assert!(
            !result.starts_with(b"12345"),
            "oldest chunk should have been evicted"
        );
    }

    #[test]
    fn test_scrollback_buffer_empty_append() {
        let mut buf = ScrollbackBuffer::new(100);
        buf.append(b"test");
        buf.append(b"");
        assert_eq!(
            buf.get_all(),
            b"test",
            "empty append should not affect buffer"
        );
    }

    #[test]
    fn test_scrollback_buffer_clear() {
        let mut buf = ScrollbackBuffer::new(100);
        buf.append(b"data");
        buf.clear();
        assert_eq!(
            buf.get_all(),
            b"",
            "buffer should be empty after clear"
        );
        assert_eq!(buf.total_bytes, 0, "total_bytes should be zero after clear");
    }

    #[test]
    fn test_scrollback_buffer_single_large_chunk() {
        let mut buf = ScrollbackBuffer::new(5);
        buf.append(b"1234567890");

        assert!(
            buf.total_bytes <= 5,
            "buffer should evict chunks to stay under limit"
        );
    }

    #[test]
    fn test_scrollback_buffer_exact_size_limit() {
        let mut buf = ScrollbackBuffer::new(10);
        buf.append(b"12345");
        buf.append(b"67890");
        assert_eq!(buf.total_bytes, 10, "buffer should hold exactly max_bytes");
        assert_eq!(
            buf.get_all(),
            b"1234567890",
            "buffer should contain all data at limit"
        );
    }

    // Subscriber tests - test the HashSet-backed subscriber tracking directly
    #[test]
    fn test_subscriber_add_and_count() {
        let subscribers: Arc<Mutex<HashSet<String>>> = Arc::new(Mutex::new(HashSet::new()));

        assert_eq!(subscribers.lock().len(), 0, "should start with no subscribers");

        subscribers.lock().insert("main".to_string());
        assert_eq!(subscribers.lock().len(), 1, "should have one subscriber after add");

        subscribers.lock().insert("window-123".to_string());
        assert_eq!(subscribers.lock().len(), 2, "should have two subscribers after second add");
    }

    #[test]
    fn test_subscriber_add_duplicate() {
        let subscribers: Arc<Mutex<HashSet<String>>> = Arc::new(Mutex::new(HashSet::new()));

        subscribers.lock().insert("main".to_string());
        subscribers.lock().insert("main".to_string()); // Duplicate

        assert_eq!(
            subscribers.lock().len(),
            1,
            "duplicate subscriber should not increase count"
        );
    }

    #[test]
    fn test_subscriber_remove() {
        let subscribers: Arc<Mutex<HashSet<String>>> = Arc::new(Mutex::new(HashSet::new()));

        subscribers.lock().insert("main".to_string());
        subscribers.lock().insert("window-123".to_string());
        assert_eq!(subscribers.lock().len(), 2);

        subscribers.lock().remove(&"main".to_string());
        assert_eq!(subscribers.lock().len(), 1, "should have one subscriber after remove");

        let remaining: Vec<String> = subscribers.lock().iter().cloned().collect();
        assert_eq!(remaining, vec!["window-123"], "correct subscriber should remain");
    }

    #[test]
    fn test_subscriber_remove_nonexistent() {
        let subscribers: Arc<Mutex<HashSet<String>>> = Arc::new(Mutex::new(HashSet::new()));

        subscribers.lock().insert("main".to_string());
        subscribers.lock().remove(&"nonexistent".to_string());

        assert_eq!(
            subscribers.lock().len(),
            1,
            "removing nonexistent subscriber should not affect count"
        );
    }

    #[test]
    fn test_subscriber_get_list() {
        let subscribers: Arc<Mutex<HashSet<String>>> = Arc::new(Mutex::new(HashSet::new()));

        subscribers.lock().insert("main".to_string());
        subscribers.lock().insert("window-a".to_string());
        subscribers.lock().insert("window-b".to_string());

        let mut list: Vec<String> = subscribers.lock().iter().cloned().collect();
        list.sort();

        assert_eq!(list, vec!["main", "window-a", "window-b"]);
    }

    #[test]
    fn test_subscriber_empty_list() {
        let subscribers: Arc<Mutex<HashSet<String>>> = Arc::new(Mutex::new(HashSet::new()));

        let list: Vec<String> = subscribers.lock().iter().cloned().collect();
        assert!(list.is_empty(), "empty subscribers should return empty list");
    }
}
