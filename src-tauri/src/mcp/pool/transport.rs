use std::io;
use std::path::PathBuf;

#[cfg(unix)]
use tokio::net::{UnixListener, UnixStream};

#[cfg(windows)]
use tokio::net::windows::named_pipe::{NamedPipeServer, ServerOptions};

pub struct LocalListener {
    #[cfg(unix)]
    inner: UnixListener,
    #[cfg(windows)]
    pipe_name: String,
}

#[cfg(unix)]
pub type LocalStream = UnixStream;

#[cfg(windows)]
pub type LocalStream = NamedPipeServer;

pub fn bind(path: &PathBuf) -> io::Result<LocalListener> {
    #[cfg(unix)]
    {
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        if path.exists() {
            let _ = std::fs::remove_file(path);
        }
        let inner = UnixListener::bind(path)?;
        return Ok(LocalListener { inner });
    }

    #[cfg(windows)]
    {
        return Ok(LocalListener {
            pipe_name: path.to_string_lossy().to_string(),
        });
    }
}

impl LocalListener {
    pub async fn accept(&self) -> io::Result<LocalStream> {
        #[cfg(unix)]
        {
            let (stream, _) = self.inner.accept().await?;
            return Ok(stream);
        }

        #[cfg(windows)]
        {
            let server = ServerOptions::new().create(&self.pipe_name)?;
            server.connect().await?;
            return Ok(server);
        }
    }
}
