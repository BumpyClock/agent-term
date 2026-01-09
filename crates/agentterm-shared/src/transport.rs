use std::io;
use std::path::PathBuf;

#[cfg(unix)]
use tokio::net::UnixStream;

#[cfg(windows)]
use tokio::net::windows::named_pipe::ClientOptions;

#[cfg(windows)]
pub type LocalStream = tokio::net::windows::named_pipe::NamedPipeClient;

#[cfg(unix)]
pub type LocalStream = UnixStream;

pub async fn connect(path: &PathBuf) -> io::Result<LocalStream> {
    #[cfg(unix)]
    {
        UnixStream::connect(path).await
    }

    #[cfg(windows)]
    {
        let path_str = path.to_string_lossy().to_string();
        ClientOptions::new().open(path_str)
    }
}
