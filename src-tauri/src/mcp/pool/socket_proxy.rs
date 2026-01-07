use std::collections::HashMap;
use std::fs::{create_dir_all, File};
use std::io;
use std::path::PathBuf;
use std::process::Stdio;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::{Arc, Mutex};

#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x08000000;

use serde_json::Value;
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::process::{ChildStderr, ChildStdin, ChildStdout, Command};
use tokio::sync::{mpsc, oneshot};
use tokio::time::{sleep, Duration, Instant};

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
    request_tx: Arc<Mutex<Option<mpsc::Sender<String>>>>,
    kill_tx: Arc<Mutex<Option<oneshot::Sender<()>>>>,
    listener: Mutex<Option<Arc<LocalListener>>>,
    clients: Arc<Mutex<HashMap<String, ClientSender>>>,
    request_map: Arc<Mutex<HashMap<String, String>>>,
    shutdown: Arc<AtomicBool>,
    started_at: Mutex<Option<Instant>>,
    total_connections: Arc<AtomicU32>,
    exit_complete_tx: Arc<Mutex<Option<oneshot::Sender<()>>>>,
    exit_complete_rx: Mutex<Option<oneshot::Receiver<()>>>,
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
            request_tx: Arc::new(Mutex::new(None)),
            kill_tx: Arc::new(Mutex::new(None)),
            listener: Mutex::new(None),
            clients: Arc::new(Mutex::new(HashMap::new())),
            request_map: Arc::new(Mutex::new(HashMap::new())),
            shutdown: Arc::new(AtomicBool::new(false)),
            started_at: Mutex::new(None),
            total_connections: Arc::new(AtomicU32::new(0)),
            exit_complete_tx: Arc::new(Mutex::new(None)),
            exit_complete_rx: Mutex::new(None),
        }
    }

    pub fn status(&self) -> ServerStatus {
        *self.status.lock().unwrap()
    }

    pub fn socket_path(&self) -> PathBuf {
        self.socket_path.clone()
    }

    pub fn name(&self) -> &str {
        &self.name
    }

    pub fn is_owned(&self) -> bool {
        self.owned
    }

    pub fn uptime_seconds(&self) -> Option<u64> {
        self.started_at.lock().unwrap().map(|start| start.elapsed().as_secs())
    }

    pub fn connection_count(&self) -> u32 {
        self.total_connections.load(Ordering::SeqCst)
    }

    /// Takes the exit completion receiver, allowing the caller to await process exit.
    /// Returns None if start() hasn't been called or receiver was already taken.
    pub fn take_exit_receiver(&self) -> Option<oneshot::Receiver<()>> {
        self.exit_complete_rx.lock().unwrap().take()
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
                .creation_flags(CREATE_NO_WINDOW)
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

        let (request_tx, request_rx) = mpsc::channel::<String>(1024);
        *self.request_tx.lock().unwrap() = Some(request_tx);

        let (kill_tx, kill_rx) = oneshot::channel();
        *self.kill_tx.lock().unwrap() = Some(kill_tx);

        // Create exit completion channel for callers to await process exit
        let (exit_tx, exit_rx) = oneshot::channel::<()>();
        *self.exit_complete_tx.lock().unwrap() = Some(exit_tx);
        *self.exit_complete_rx.lock().unwrap() = Some(exit_rx);

        if let Some(stderr) = stderr {
            self.spawn_stderr_logger(stderr)?;
        }

        if let Some(stdout) = stdout {
            self.spawn_stdout_router(stdout);
        }

        if let Some(stdin) = stdin {
            self.spawn_stdin_writer(stdin, request_rx);
        }

        let status = self.status.clone();
        let shutdown = self.shutdown.clone();
        let name = self.name.clone();
        let exit_complete_tx = self.exit_complete_tx.clone();

        tauri::async_runtime::spawn(async move {
            let exit = tokio::select! {
                res = child.wait() => res,
                signal = kill_rx => {
                    if signal.is_ok() {
                        let _ = child.start_kill();
                    }
                    child.wait().await
                }
            };

            *status.lock().unwrap() = ServerStatus::Stopped;
            shutdown.store(true, Ordering::SeqCst);

            // Signal exit completion to any waiting callers
            if let Some(tx) = exit_complete_tx.lock().unwrap().take() {
                let _ = tx.send(());
            }

            if let Ok(exit) = exit {
                diagnostics::log(format!(
                    "pool_child_exited name={} status={}",
                    name, exit
                ));
            }
        });

        let listener = Arc::new(transport::bind(&self.socket_path)?);
        *self.listener.lock().unwrap() = Some(listener.clone());

        self.spawn_accept_loop(listener);

        *self.status.lock().unwrap() = ServerStatus::Running;
        *self.started_at.lock().unwrap() = Some(Instant::now());
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
            if let Some(kill_tx) = self.kill_tx.lock().unwrap().take() {
                let _ = kill_tx.send(());
            }
            #[cfg(unix)]
            {
                let _ = std::fs::remove_file(&self.socket_path);
            }
        }

        *self.request_tx.lock().unwrap() = None;
        self.clients.lock().unwrap().clear();
        self.request_map.lock().unwrap().clear();
        *self.started_at.lock().unwrap() = None;
        // For non-owned servers, set status here since there's no background task.
        // For owned servers, the background task sets Stopped after process exits.
        if !self.owned {
            *self.status.lock().unwrap() = ServerStatus::Stopped;
        }
        Ok(())
    }

    fn spawn_stderr_logger(&self, stderr: ChildStderr) -> io::Result<()> {
        let log_dir = diagnostics::log_dir().unwrap_or_else(|| PathBuf::from("."));
        let pool_dir = log_dir.join("mcppool");
        create_dir_all(&pool_dir)?;
        let log_path = pool_dir.join(format!("{}_socket.log", self.name));
        diagnostics::log(format!(
            "pool_proxy_stderr_log name={} path={}",
            self.name,
            log_path.display()
        ));
        let file = File::create(log_path)?;
        tauri::async_runtime::spawn(async move {
            let mut file = tokio::fs::File::from_std(file);
            let mut reader = BufReader::new(stderr);
            let mut buffer = String::new();
            loop {
                buffer.clear();
                match reader.read_line(&mut buffer).await {
                    Ok(0) => break,
                    Ok(_) => {
                        if file.write_all(buffer.as_bytes()).await.is_err() {
                            break;
                        }
                    }
                    Err(_) => break,
                }
            }
        });
        Ok(())
    }

    fn spawn_accept_loop(&self, listener: Arc<LocalListener>) {
        let clients = self.clients.clone();
        let request_map = self.request_map.clone();
        let request_tx = self.request_tx.clone();
        let shutdown = self.shutdown.clone();
        let name = self.name.clone();
        let total_connections = self.total_connections.clone();

        tauri::async_runtime::spawn(async move {
            let mut counter = 0;
            loop {
                if shutdown.load(Ordering::SeqCst) {
                    break;
                }
                match listener.accept().await {
                    Ok(stream) => {
                        let client_id = format!("{}-client-{}", name, counter);
                        counter += 1;
                        total_connections.fetch_add(1, Ordering::SeqCst);
                        let (tx, rx) = mpsc::channel::<String>(128);
                        clients.lock().unwrap().insert(client_id.clone(), tx);
                        diagnostics::log(format!(
                            "pool_client_connected name={} client_id={}",
                            name, client_id
                        ));

                        let clients_for_drop = clients.clone();
                        let request_map_for_drop = request_map.clone();
                        let request_tx_for_client = request_tx.clone();
                        let shutdown_for_client = shutdown.clone();
                        let client_id_clone = client_id.clone();

                        tauri::async_runtime::spawn(async move {
                            handle_client(
                                stream,
                                client_id_clone,
                                request_tx_for_client,
                                request_map_for_drop,
                                clients_for_drop,
                                shutdown_for_client,
                                rx,
                            )
                            .await;
                        });
                    }
                    Err(err) => {
                        diagnostics::log(format!(
                            "pool_accept_error name={} error={}",
                            name, err
                        ));
                        sleep(Duration::from_millis(50)).await;
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
        tauri::async_runtime::spawn(async move {
            let mut reader = BufReader::new(stdout);
            let mut buffer = String::new();
            loop {
                if shutdown.load(Ordering::SeqCst) {
                    break;
                }
                buffer.clear();
                match reader.read_line(&mut buffer).await {
                    Ok(0) => break,
                    Ok(_) => {
                        let line = buffer.trim_end_matches('\n').to_string();
                        if line.is_empty() {
                            continue;
                        }
                        route_response(&line, &clients, &request_map).await;
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

    fn spawn_stdin_writer(&self, stdin: ChildStdin, mut rx: mpsc::Receiver<String>) {
        let shutdown = self.shutdown.clone();
        let name = self.name.clone();
        tauri::async_runtime::spawn(async move {
            let mut stdin = stdin;
            loop {
                if shutdown.load(Ordering::SeqCst) {
                    break;
                }
                tokio::select! {
                    message = rx.recv() => {
                        match message {
                            Some(line) => {
                                if let Err(err) = stdin.write_all(line.as_bytes()).await {
                                    diagnostics::log(format!(
                                        "pool_request_write_failed name={} error={}",
                                        name, err
                                    ));
                                    break;
                                }
                                if let Err(err) = stdin.write_all(b"\n").await {
                                    diagnostics::log(format!(
                                        "pool_request_write_failed name={} error={}",
                                        name, err
                                    ));
                                    break;
                                }
                            }
                            None => break,
                        }
                    }
                    _ = sleep(Duration::from_millis(50)) => {}
                }
            }
        });
    }
}

async fn handle_client(
    stream: LocalStream,
    client_id: String,
    request_tx: Arc<Mutex<Option<mpsc::Sender<String>>>>,
    request_map: Arc<Mutex<HashMap<String, String>>>,
    clients: Arc<Mutex<HashMap<String, ClientSender>>>,
    shutdown: Arc<AtomicBool>,
    mut rx: mpsc::Receiver<String>,
) {
    diagnostics::log(format!(
        "pool_handle_client_started client_id={}",
        client_id
    ));

    let (read_half, mut write_half) = tokio::io::split(stream);
    let mut reader = BufReader::new(read_half);
    let mut buffer = String::new();
    let mut parse_failures = 0u32;

    loop {
        if shutdown.load(Ordering::SeqCst) {
            break;
        }
        tokio::select! {
            read_result = reader.read_line(&mut buffer) => {
                match read_result {
                    Ok(0) => {
                        diagnostics::log(format!(
                            "pool_client_disconnected client_id={}",
                            client_id
                        ));
                        break;
                    }
                    Ok(_) => {
                        let line = buffer.trim_end_matches('\n').to_string();
                        buffer.clear();
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
                        let sender = request_tx.lock().unwrap().clone();
                        if let Some(sender) = sender {
                            if sender.send(line.clone()).await.is_err() {
                                diagnostics::log(format!(
                                    "pool_request_write_failed client_id={} error={}",
                                    client_id,
                                    "stdin channel closed"
                                ));
                                break;
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
                        diagnostics::log(format!(
                            "pool_client_read_error client_id={} error={}",
                            client_id, err
                        ));
                        break;
                    }
                }
            }
            message = rx.recv() => {
                match message {
                    Some(message) => {
                        let mut bytes = message.into_bytes();
                        bytes.push(b'\n');
                        if let Err(err) = write_half.write_all(&bytes).await {
                            diagnostics::log(format!(
                                "pool_client_write_failed client_id={} error={}",
                                client_id, err
                            ));
                            break;
                        }
                        if let Err(err) = write_half.flush().await {
                            diagnostics::log(format!(
                                "pool_client_flush_failed client_id={} error={}",
                                client_id, err
                            ));
                            break;
                        }
                    }
                    None => break,
                }
            }
            _ = sleep(Duration::from_millis(50)) => {}
        }
    }

    clients.lock().unwrap().remove(&client_id);
}

async fn route_response(
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
            if sender.send(line.to_string()).await.is_ok() {
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
            broadcast_to_all(line, clients).await;
        }
    } else {
        broadcast_to_all(line, clients).await;
    }
}

async fn broadcast_to_all(line: &str, clients: &Arc<Mutex<HashMap<String, ClientSender>>>) {
    let senders: Vec<ClientSender> = clients.lock().unwrap().values().cloned().collect();
    for sender in &senders {
        let _ = sender.send(line.to_string()).await;
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
