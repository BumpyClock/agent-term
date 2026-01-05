use std::io::Write;
use std::sync::mpsc::Sender;
use std::thread::JoinHandle;

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
    master: Box<dyn MasterPty + Send>,
    writer: Box<dyn Write + Send>,
    child: Box<dyn Child + Send + Sync>,
    reader_thread: Option<JoinHandle<()>>,
    shutdown_tx: Sender<()>,
}

impl SessionRuntime {
    pub fn new(
        master: Box<dyn MasterPty + Send>,
        writer: Box<dyn Write + Send>,
        child: Box<dyn Child + Send + Sync>,
        reader_thread: JoinHandle<()>,
        shutdown_tx: Sender<()>,
    ) -> Self {
        Self {
            master,
            writer,
            child,
            reader_thread: Some(reader_thread),
            shutdown_tx,
        }
    }

    pub fn write(&mut self, data: &[u8]) -> Result<(), String> {
        self.writer
            .write_all(data)
            .map_err(|e| format!("failed to write: {}", e))?;
        self.writer
            .flush()
            .map_err(|e| format!("failed to flush: {}", e))?;
        Ok(())
    }

    pub fn resize(&mut self, rows: u16, cols: u16) -> Result<(), String> {
        self.master
            .resize(PtySize {
                rows,
                cols,
                pixel_width: 0,
                pixel_height: 0,
            })
            .map_err(|e| format!("failed to resize: {}", e))
    }

    pub fn shutdown(&mut self) {
        let _ = self.shutdown_tx.send(());
        if let Err(e) = self.child.kill() {
            let _ = e;
        }
        let _ = self.child.try_wait();
        if let Some(handle) = self.reader_thread.take() {
            let _ = handle.join();
        }
    }
}

impl Drop for SessionRuntime {
    fn drop(&mut self) {
        self.shutdown();
    }
}
