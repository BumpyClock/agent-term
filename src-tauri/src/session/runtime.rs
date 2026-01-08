// ABOUTME: Manages the live PTY runtime for a session, including the child process and I/O threads.
// ABOUTME: Handles shutdown and cleanup of session resources when stopped or dropped.

use std::io::Write;
use std::sync::mpsc::Sender;
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use crate::diagnostics;
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
}

impl SessionRuntime {
    pub fn new(
        master: Box<dyn MasterPty + Send>,
        writer: Box<dyn Write + Send>,
        child: Box<dyn Child + Send + Sync>,
        reader_thread: JoinHandle<()>,
        shutdown_tx: Sender<()>,
        id: String,
    ) -> Self {
        Self {
            id,
            master: Some(master),
            writer: Some(writer),
            child,
            reader_thread: Some(reader_thread),
            shutdown_tx,
            shutdown_called: false,
        }
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
