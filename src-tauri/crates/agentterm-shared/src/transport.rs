use std::io;
use std::path::PathBuf;

use interprocess::local_socket::prelude::*;
use interprocess::local_socket::{ListenerNonblockingMode, ListenerOptions};

#[cfg(unix)]
use interprocess::os::unix::local_socket::FilesystemUdSocket;

#[cfg(windows)]
use interprocess::os::windows::local_socket::NamedPipe;

pub type LocalListener = LocalSocketListener;
pub type LocalStream = LocalSocketStream;

pub fn bind(path: &PathBuf) -> io::Result<LocalListener> {
    #[cfg(unix)]
    {
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        if path.exists() {
            let _ = fs::remove_file(path);
        }
    }

    let name = to_local_name(path)?;
    let listener = ListenerOptions::new().name(name).create_sync()?;
    listener.set_nonblocking(ListenerNonblockingMode::Accept)?;
    Ok(listener)
}

pub fn connect(path: &PathBuf) -> io::Result<LocalStream> {
    let name = to_local_name(path)?;
    LocalSocketStream::connect(name)
}

fn to_local_name(path: &PathBuf) -> io::Result<interprocess::local_socket::Name<'_>> {
    #[cfg(unix)]
    {
        path.as_path().to_fs_name::<FilesystemUdSocket>()
    }
    #[cfg(windows)]
    {
        let path_str = path.to_string_lossy().to_string();
        path_str.to_fs_name::<NamedPipe>()
    }
}
#[cfg(unix)]
use std::fs;
