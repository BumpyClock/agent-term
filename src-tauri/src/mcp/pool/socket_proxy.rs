use std::collections::HashMap;
use std::fs::{create_dir_all, File};
use std::io::{self, BufRead, BufReader, Write};
use std::os::unix::net::{UnixListener, UnixStream};
use std::path::{Path, PathBuf};
use std::process::{Child, ChildStdin, ChildStdout, Command, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::Duration;

use serde_json::Value;

use crate::diagnostics;
use super::types::ServerStatus;

pub struct SocketProxy {
    name: String,
    socket_path: PathBuf,
    command: String,
    args: Vec<String>,
    env: HashMap<String, String>,
    status: Mutex<ServerStatus>,
    owned: bool,
    stdin: Arc<Mutex<Option<ChildStdin>>>,
    child: Mutex<Option<Child>>,
    listener: Mutex<Option<Arc<UnixListener>>>,
    clients: Arc<Mutex<HashMap<String, UnixStream>>>,
    request_map: Arc<Mutex<HashMap<String, String>>>,
    shutdown: Arc<AtomicBool>,
}

impl SocketProxy {
    pub fn new(
        name: String,
        socket_path: PathBuf,
        command: String,
        args: Vec<String>,
        env: HashMap<String, String>,
        owned: bool,
    ) -> Self {
        Self {
            name,
            socket_path,
            command,
            args,
            env,
            status: Mutex::new(ServerStatus::Stopped),
            owned,
            stdin: Arc::new(Mutex::new(None)),
            child: Mutex::new(None),
            listener: Mutex::new(None),
            clients: Arc::new(Mutex::new(HashMap::new())),
            request_map: Arc::new(Mutex::new(HashMap::new())),
            shutdown: Arc::new(AtomicBool::new(false)),
        }
    }

    pub fn status(&self) -> ServerStatus {
        *self.status.lock().unwrap()
    }

    pub fn name(&self) -> String {
        self.name.clone()
    }

    pub fn socket_path(&self) -> PathBuf {
        self.socket_path.clone()
    }

    pub fn is_owned(&self) -> bool {
        self.owned
    }

    pub fn start(&self) -> io::Result<()> {
        if self.status() == ServerStatus::Running {
            return Ok(());
        }
        if !self.owned {
            *self.status.lock().unwrap() = ServerStatus::Running;
            return Ok(());
        }

        *self.status.lock().unwrap() = ServerStatus::Starting;

        let mut child = Command::new(&self.command)
            .args(&self.args)
            .envs(self.env.clone())
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()?;

        let stdin = child.stdin.take();
        let stdout = child.stdout.take();
        let stderr = child.stderr.take();

        if let Some(stdin) = stdin {
            *self.stdin.lock().unwrap() = Some(stdin);
        }

        if let Some(stderr) = stderr {
            self.spawn_stderr_logger(stderr)?;
        }

        if let Some(stdout) = stdout {
            self.spawn_stdout_router(stdout);
        }

        if Path::new(&self.socket_path).exists() {
            let _ = std::fs::remove_file(&self.socket_path);
        }

        let listener = UnixListener::bind(&self.socket_path)?;
        listener.set_nonblocking(true)?;
        let listener = Arc::new(listener);
        *self.listener.lock().unwrap() = Some(listener.clone());

        self.spawn_accept_loop(listener);

        *self.child.lock().unwrap() = Some(child);
        *self.status.lock().unwrap() = ServerStatus::Running;
        diagnostics::log(format!(
            "pool_proxy_started name={} socket={}",
            self.name,
            self.socket_path.display()
        ));
        Ok(())
    }

    pub fn stop(&self) -> io::Result<()> {
        self.shutdown.store(true, Ordering::SeqCst);
        if let Some(listener) = self.listener.lock().unwrap().take() {
            drop(listener);
        }

        if self.owned {
            if let Some(mut child) = self.child.lock().unwrap().take() {
                let _ = child.kill();
                let _ = child.wait();
            }
            let _ = std::fs::remove_file(&self.socket_path);
        }

        *self.stdin.lock().unwrap() = None;
        self.clients.lock().unwrap().clear();
        self.request_map.lock().unwrap().clear();
        *self.status.lock().unwrap() = ServerStatus::Stopped;
        Ok(())
    }

    pub fn client_count(&self) -> usize {
        self.clients.lock().unwrap().len()
    }

    fn spawn_stderr_logger(&self, stderr: std::process::ChildStderr) -> io::Result<()> {
        let log_dir = diagnostics::log_dir().unwrap_or_else(|| PathBuf::from("."));
        let pool_dir = log_dir.join("mcppool");
        create_dir_all(&pool_dir)?;
        let log_path = pool_dir.join(format!("{}_socket.log", self.name));
        let mut file = File::create(log_path)?;
        thread::spawn(move || {
            let mut reader = BufReader::new(stderr);
            let mut buffer = String::new();
            loop {
                buffer.clear();
                if reader.read_line(&mut buffer).is_err() {
                    break;
                }
                if buffer.is_empty() {
                    break;
                }
                let _ = file.write_all(buffer.as_bytes());
            }
        });
        Ok(())
    }

    fn spawn_accept_loop(&self, listener: Arc<UnixListener>) {
        let clients = self.clients.clone();
        let request_map = self.request_map.clone();
        let stdin = self.stdin.clone();
        let shutdown = self.shutdown.clone();
        let name = self.name.clone();

        thread::spawn(move || {
            let mut counter = 0;
            while !shutdown.load(Ordering::SeqCst) {
                match listener.accept() {
                    Ok((stream, _)) => {
                        let client_id = format!("{}-client-{}", name, counter);
                        counter += 1;
                        if let Ok(write_stream) = stream.try_clone() {
                            clients.lock().unwrap().insert(client_id.clone(), write_stream);
                            diagnostics::log(format!(
                                "pool_client_connected name={} client_id={}",
                                name, client_id
                            ));
                        }

                        let clients_for_drop = clients.clone();
                        let request_map_for_drop = request_map.clone();
                        let stdin_for_client = stdin.clone();
                        let shutdown_for_client = shutdown.clone();
                        let client_id_clone = client_id.clone();

                        thread::spawn(move || {
                            handle_client(
                                stream,
                                client_id_clone,
                                stdin_for_client,
                                request_map_for_drop,
                                clients_for_drop,
                                shutdown_for_client,
                            );
                        });
                    }
                    Err(err) if err.kind() == io::ErrorKind::WouldBlock => {
                        thread::sleep(Duration::from_millis(50));
                    }
                    Err(_) => {
                        thread::sleep(Duration::from_millis(50));
                    }
                }
            }
        });
    }

    fn spawn_stdout_router(&self, stdout: ChildStdout) {
        let clients = self.clients.clone();
        let request_map = self.request_map.clone();
        let shutdown = self.shutdown.clone();
        thread::spawn(move || {
            let mut reader = BufReader::new(stdout);
            let mut buffer = String::new();
            while !shutdown.load(Ordering::SeqCst) {
                buffer.clear();
                match reader.read_line(&mut buffer) {
                    Ok(0) => break,
                    Ok(_) => {
                        let line = buffer.trim_end_matches('\n').to_string();
                        if line.is_empty() {
                            continue;
                        }
                        route_response(&line, &clients, &request_map);
                    }
                    Err(_) => break,
                }
            }
        });
    }
}

fn handle_client(
    mut stream: UnixStream,
    client_id: String,
    stdin: Arc<Mutex<Option<ChildStdin>>>,
    request_map: Arc<Mutex<HashMap<String, String>>>,
    clients: Arc<Mutex<HashMap<String, UnixStream>>>,
    shutdown: Arc<AtomicBool>,
) {
    let mut reader = BufReader::new(stream.try_clone().unwrap_or(stream));
    let mut buffer = String::new();
    while !shutdown.load(Ordering::SeqCst) {
        buffer.clear();
        match reader.read_line(&mut buffer) {
            Ok(0) => break,
            Ok(_) => {
                let line = buffer.trim_end_matches('\n').to_string();
                if line.is_empty() {
                    continue;
                }
                if let Ok(value) = serde_json::from_str::<Value>(&line) {
                    if let Some(id) = value.get("id").and_then(id_key) {
                        request_map.lock().unwrap().insert(id, client_id.clone());
                    }
                }
                if let Some(stdin) = stdin.lock().unwrap().as_mut() {
                    let _ = stdin.write_all(line.as_bytes());
                    let _ = stdin.write_all(b"\n");
                    diagnostics::log(format!(
                        "pool_request_forwarded client_id={} bytes={}",
                        client_id,
                        line.len()
                    ));
                }
            }
            Err(_) => break,
        }
    }

    clients.lock().unwrap().remove(&client_id);
}

fn route_response(
    line: &str,
    clients: &Arc<Mutex<HashMap<String, UnixStream>>>,
    request_map: &Arc<Mutex<HashMap<String, String>>>,
) {
    let mut target = None;
    if let Ok(value) = serde_json::from_str::<Value>(line) {
        if let Some(id) = value.get("id").and_then(id_key) {
            target = request_map.lock().unwrap().remove(&id);
        }
    }

    if let Some(client_id) = target {
        if let Some(stream) = clients.lock().unwrap().get_mut(&client_id) {
            let _ = stream.write_all(line.as_bytes());
            let _ = stream.write_all(b"\n");
            diagnostics::log(format!(
                "pool_response_routed client_id={} bytes={}",
                client_id,
                line.len()
            ));
        } else {
            broadcast_to_all(line, clients);
        }
    } else {
        broadcast_to_all(line, clients);
    }
}

fn broadcast_to_all(line: &str, clients: &Arc<Mutex<HashMap<String, UnixStream>>>) {
    let mut clients_guard = clients.lock().unwrap();
    for stream in clients_guard.values_mut() {
        let _ = stream.write_all(line.as_bytes());
        let _ = stream.write_all(b"\n");
    }
    diagnostics::log(format!(
        "pool_response_broadcast bytes={} clients={}",
        line.len(),
        clients_guard.len()
    ));
}

fn id_key(value: &Value) -> Option<String> {
    match value {
        Value::String(value) => Some(format!("s:{}", value)),
        Value::Number(value) => Some(format!("n:{}", value)),
        Value::Bool(value) => Some(format!("b:{}", value)),
        Value::Null => None,
        _ => Some(value.to_string()),
    }
}
