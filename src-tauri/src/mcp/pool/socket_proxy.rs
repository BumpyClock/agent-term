use std::collections::{HashMap, VecDeque};
use std::fs::{create_dir_all, File};
use std::io::{self, BufRead, BufReader, Write};
use std::path::PathBuf;
use std::process::{Child, ChildStdin, ChildStdout, Command, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{mpsc, Arc, Mutex};
use std::thread;
use std::time::Duration;

use interprocess::local_socket::traits::{Listener as _, Stream as _};
use serde_json::Value;

use crate::diagnostics;
use super::transport::{self, LocalListener, LocalStream};
use super::types::ServerStatus;

type ClientSender = mpsc::Sender<String>;

pub struct SocketProxy {
    name: String,
    socket_path: PathBuf,
    command: String,
    args: Vec<String>,
    env: HashMap<String, String>,
    status: Arc<Mutex<ServerStatus>>,
    owned: bool,
    stdin: Arc<Mutex<Option<ChildStdin>>>,
    child: Arc<Mutex<Option<Child>>>,
    listener: Mutex<Option<Arc<LocalListener>>>,
    clients: Arc<Mutex<HashMap<String, ClientSender>>>,
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
            status: Arc::new(Mutex::new(ServerStatus::Stopped)),
            owned,
            stdin: Arc::new(Mutex::new(None)),
            child: Arc::new(Mutex::new(None)),
            listener: Mutex::new(None),
            clients: Arc::new(Mutex::new(HashMap::new())),
            request_map: Arc::new(Mutex::new(HashMap::new())),
            shutdown: Arc::new(AtomicBool::new(false)),
        }
    }

    pub fn status(&self) -> ServerStatus {
        *self.status.lock().unwrap()
    }

    pub fn socket_path(&self) -> PathBuf {
        self.socket_path.clone()
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

        diagnostics::log(format!(
            "pool_proxy_starting name={} command={} args={:?}",
            self.name, self.command, self.args
        ));

        // On Windows, commands like pnpx/npx/npm are .cmd batch files.
        // Rust's Command::new("pnpx") looks for pnpx.exe which doesn't exist.
        // We must spawn via "cmd /c pnpx ..." to resolve .cmd files.
        #[cfg(windows)]
        let mut child = {
            let mut cmd_args = vec!["/c".to_string(), self.command.clone()];
            cmd_args.extend(self.args.clone());
            Command::new("cmd")
                .args(&cmd_args)
                .envs(self.env.clone())
                .stdin(Stdio::piped())
                .stdout(Stdio::piped())
                .stderr(Stdio::piped())
                .spawn()?
        };

        #[cfg(not(windows))]
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

        #[cfg(unix)]
        {
            if Path::new(&self.socket_path).exists() {
                let _ = std::fs::remove_file(&self.socket_path);
            }
        }

        let listener = Arc::new(transport::bind(&self.socket_path)?);
        *self.listener.lock().unwrap() = Some(listener.clone());

        self.spawn_accept_loop(listener);

        *self.child.lock().unwrap() = Some(child);
        *self.status.lock().unwrap() = ServerStatus::Running;
        diagnostics::log(format!(
            "pool_proxy_started name={} socket={}",
            self.name,
            self.socket_path.display()
        ));
        self.spawn_child_monitor();
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
            #[cfg(unix)]
            {
                let _ = std::fs::remove_file(&self.socket_path);
            }
        }

        *self.stdin.lock().unwrap() = None;
        self.clients.lock().unwrap().clear();
        self.request_map.lock().unwrap().clear();
        *self.status.lock().unwrap() = ServerStatus::Stopped;
        Ok(())
    }


    fn spawn_stderr_logger(&self, stderr: std::process::ChildStderr) -> io::Result<()> {
        let log_dir = diagnostics::log_dir().unwrap_or_else(|| PathBuf::from("."));
        let pool_dir = log_dir.join("mcppool");
        create_dir_all(&pool_dir)?;
        let log_path = pool_dir.join(format!("{}_socket.log", self.name));
        diagnostics::log(format!(
            "pool_proxy_stderr_log name={} path={}",
            self.name,
            log_path.display()
        ));
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

    fn spawn_accept_loop(&self, listener: Arc<LocalListener>) {
        let clients = self.clients.clone();
        let request_map = self.request_map.clone();
        let stdin = self.stdin.clone();
        let shutdown = self.shutdown.clone();
        let name = self.name.clone();

        thread::spawn(move || {
            let mut counter = 0;
            while !shutdown.load(Ordering::SeqCst) {
                match listener.accept() {
                    Ok(stream) => {
                        if let Err(err) = stream.set_nonblocking(false) {
                            diagnostics::log(format!(
                                "pool_client_set_blocking_failed name={} error={}",
                                name, err
                            ));
                        }
                        let client_id = format!("{}-client-{}", name, counter);
                        counter += 1;
                        let (tx, rx) = mpsc::channel::<String>();
                        clients.lock().unwrap().insert(client_id.clone(), tx);
                        diagnostics::log(format!(
                            "pool_client_connected name={} client_id={}",
                            name, client_id
                        ));

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
                                rx,
                            );
                        });
                    }
                    Err(err) if err.kind() == io::ErrorKind::WouldBlock => {
                        thread::sleep(Duration::from_millis(50));
                    }
                    Err(err) => {
                        diagnostics::log(format!(
                            "pool_accept_error name={} error={}",
                            name, err
                        ));
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
        let name = self.name.clone();
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
                    Err(err) => {
                        diagnostics::log(format!(
                            "pool_stdout_read_error name={} error={}",
                            name, err
                        ));
                        break;
                    }
                }
            }
            diagnostics::log(format!("pool_stdout_closed name={}", name));
        });
    }

    fn spawn_child_monitor(&self) {
        let child = self.child.clone();
        let status = self.status.clone();
        let shutdown = self.shutdown.clone();
        let name = self.name.clone();
        thread::spawn(move || loop {
            {
                let mut guard = child.lock().unwrap();
                if let Some(proc) = guard.as_mut() {
                    if let Ok(Some(exit)) = proc.try_wait() {
                        *status.lock().unwrap() = ServerStatus::Stopped;
                        shutdown.store(true, Ordering::SeqCst);
                        diagnostics::log(format!(
                            "pool_child_exited name={} status={}",
                            name, exit
                        ));
                        break;
                    }
                } else {
                    break;
                }
            }
            thread::sleep(Duration::from_millis(250));
        });
    }
}

fn handle_client(
    stream: LocalStream,
    client_id: String,
    stdin: Arc<Mutex<Option<ChildStdin>>>,
    request_map: Arc<Mutex<HashMap<String, String>>>,
    clients: Arc<Mutex<HashMap<String, ClientSender>>>,
    shutdown: Arc<AtomicBool>,
    rx: mpsc::Receiver<String>,
) {
    diagnostics::log(format!(
        "pool_handle_client_started client_id={}",
        client_id
    ));
    if let Err(err) = stream.set_nonblocking(true) {
        diagnostics::log(format!(
            "pool_handle_client_set_nonblocking_failed client_id={} error={}",
            client_id, err
        ));
    }
    let mut reader = BufReader::new(stream);
    let mut buffer = String::new();
    let mut parse_failures = 0u32;
    let mut pending: VecDeque<Vec<u8>> = VecDeque::new();
    let mut pending_offset: usize = 0;
    while !shutdown.load(Ordering::SeqCst) {
        let mut did_work = false;
        buffer.clear();
        match reader.read_line(&mut buffer) {
            Ok(0) => {
                diagnostics::log(format!(
                    "pool_client_disconnected client_id={}",
                    client_id
                ));
                break;
            }
            Ok(_) => {
                did_work = true;
                let line = buffer.trim_end_matches('\n').to_string();
                if line.is_empty() {
                    continue;
                }
                if let Ok(value) = serde_json::from_str::<Value>(&line) {
                    if let Some(id) = value.get("id").and_then(id_key) {
                        request_map.lock().unwrap().insert(id, client_id.clone());
                    }
                } else if parse_failures < 3 {
                    parse_failures += 1;
                    diagnostics::log(format!(
                        "pool_request_parse_failed client_id={} bytes={}",
                        client_id,
                        line.len()
                    ));
                }
                if let Some(stdin) = stdin.lock().unwrap().as_mut() {
                    if let Err(err) = stdin.write_all(line.as_bytes()) {
                        diagnostics::log(format!(
                            "pool_request_write_failed client_id={} error={}",
                            client_id, err
                        ));
                    }
                    if let Err(err) = stdin.write_all(b"\n") {
                        diagnostics::log(format!(
                            "pool_request_write_failed client_id={} error={}",
                            client_id, err
                        ));
                    }
                    diagnostics::log(format!(
                        "pool_request_forwarded client_id={} bytes={}",
                        client_id,
                        line.len()
                    ));
                } else {
                    diagnostics::log(format!(
                        "pool_stdin_missing client_id={}",
                        client_id
                    ));
                }
            }
            Err(err) => {
                if err.kind() != io::ErrorKind::WouldBlock {
                    diagnostics::log(format!(
                        "pool_client_read_error client_id={} error={}",
                        client_id, err
                    ));
                    break;
                }
            }
        }

        while let Ok(message) = rx.try_recv() {
            did_work = true;
            let mut bytes = message.into_bytes();
            bytes.push(b'\n');
            pending.push_back(bytes);
        }

        let mut wrote_any = false;
        while let Some(front) = pending.front() {
            let slice = &front[pending_offset..];
            if slice.is_empty() {
                pending.pop_front();
                pending_offset = 0;
                continue;
            }
            match reader.get_mut().write(slice) {
                Ok(0) => {
                    break;
                }
                Ok(n) => {
                    wrote_any = true;
                    did_work = true;
                    pending_offset += n;
                    if pending_offset >= front.len() {
                        pending.pop_front();
                        pending_offset = 0;
                    }
                }
                Err(err) if err.kind() == io::ErrorKind::WouldBlock => {
                    break;
                }
                Err(err) => {
                    diagnostics::log(format!(
                        "pool_client_write_failed client_id={} error={}",
                        client_id, err
                    ));
                    pending.clear();
                    pending_offset = 0;
                    break;
                }
            }
        }

        if !did_work {
            thread::sleep(Duration::from_millis(10));
        } else if wrote_any {
            let _ = reader.get_mut().flush();
        }
    }

    clients.lock().unwrap().remove(&client_id);
}

fn route_response(
    line: &str,
    clients: &Arc<Mutex<HashMap<String, ClientSender>>>,
    request_map: &Arc<Mutex<HashMap<String, String>>>,
) {
    let mut target = None;
    let parsed = serde_json::from_str::<Value>(line);
    if let Ok(value) = parsed {
        if let Some(id) = value.get("id").and_then(id_key) {
            target = request_map.lock().unwrap().remove(&id);
        }
    } else {
        diagnostics::log(format!(
            "pool_response_parse_failed bytes={}",
            line.len()
        ));
    }

    if let Some(client_id) = target {
        let sender = clients.lock().unwrap().get(&client_id).cloned();
        if let Some(sender) = sender {
            if sender.send(line.to_string()).is_ok() {
                diagnostics::log(format!(
                    "pool_response_routed client_id={} bytes={}",
                    client_id,
                    line.len()
                ));
            } else {
                diagnostics::log(format!(
                    "pool_response_send_failed client_id={}",
                    client_id
                ));
            }
        } else {
            broadcast_to_all(line, clients);
        }
    } else {
        broadcast_to_all(line, clients);
    }
}

fn broadcast_to_all(line: &str, clients: &Arc<Mutex<HashMap<String, ClientSender>>>) {
    let senders: Vec<ClientSender> = clients.lock().unwrap().values().cloned().collect();
    for sender in &senders {
        let _ = sender.send(line.to_string());
    }
    diagnostics::log(format!(
        "pool_response_broadcast bytes={} clients={}",
        line.len(),
        senders.len()
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
#[cfg(unix)]
use std::path::Path;
