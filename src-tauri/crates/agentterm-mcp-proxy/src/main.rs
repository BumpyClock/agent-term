use std::env;
use std::io::{self, Read, Write};
use std::path::PathBuf;
use std::thread;
use std::time::Duration;

use interprocess::TryClone;
use agentterm_shared::diagnostics;
use agentterm_shared::socket_path::socket_path_for;
use agentterm_shared::transport;

fn main() {
    let args = parse_args();
    if args.debug {
        env::set_var("AGENT_TERM_DIAG", "1");
    }
    let endpoint = args
        .endpoint
        .map(PathBuf::from)
        .unwrap_or_else(|| socket_path_for(&args.name));

    let name = args.name.clone();
    diagnostics::log(format!(
        "mcp_proxy_start name={} endpoint={}",
        name,
        endpoint.display()
    ));

    let mut stream = match connect_with_retry(&endpoint) {
        Ok(stream) => stream,
        Err(err) => {
            diagnostics::log(format!(
                "mcp_proxy_connect_failed name={} error={}",
                name, err
            ));
            std::process::exit(1);
        }
    };

    diagnostics::log(format!("mcp_proxy_connected name={}", name));

    let mut writer = match stream.try_clone() {
        Ok(writer) => writer,
        Err(err) => {
            diagnostics::log(format!(
                "mcp_proxy_stream_clone_failed name={} error={}",
                name, err
            ));
            std::process::exit(1);
        }
    };

    diagnostics::log(format!("mcp_proxy_pump_start name={} dir=stdin->socket", name));
    let stdin_name = name.clone();
    let stdin_thread = thread::spawn(move || {
        let mut stdin = io::stdin();
        pump(&mut stdin, &mut writer, "stdin->socket", &stdin_name, false);
    });

    diagnostics::log(format!("mcp_proxy_pump_start name={} dir=socket->stdout", name));
    let mut stdout = io::stdout();
    pump(&mut stream, &mut stdout, "socket->stdout", &name, true);
    let _ = stdin_thread.join();
    diagnostics::log(format!("mcp_proxy_exit name={}", name));
}

struct ProxyArgs {
    name: String,
    endpoint: Option<String>,
    debug: bool,
}

fn parse_args() -> ProxyArgs {
    let mut name = None;
    let mut endpoint = None;
    let mut debug = false;
    let mut iter = env::args().skip(1);
    while let Some(arg) = iter.next() {
        match arg.as_str() {
            "--name" => name = iter.next(),
            "--endpoint" => endpoint = iter.next(),
            "--debug" => debug = true,
            _ => {}
        }
    }

    let Some(name) = name else {
        eprintln!("usage: agentterm-mcp-proxy --name <mcp-name> [--endpoint <path>] [--debug]");
        std::process::exit(2);
    };

    ProxyArgs { name, endpoint, debug }
}

fn connect_with_retry(path: &PathBuf) -> io::Result<transport::LocalStream> {
    let mut last_err = None;
    for _ in 0..30 {
        match transport::connect(path) {
            Ok(stream) => return Ok(stream),
            Err(err) => {
                last_err = Some(err);
                thread::sleep(Duration::from_millis(100));
            }
        }
    }
    Err(last_err.unwrap_or_else(|| {
        io::Error::new(io::ErrorKind::Other, "mcp proxy connect failed")
    }))
}

fn pump<R: Read, W: Write>(
    reader: &mut R,
    writer: &mut W,
    direction: &str,
    name: &str,
    flush_after_write: bool,
) {
    let mut buf = [0u8; 8192];
    let mut total: u64 = 0;
    loop {
        match reader.read(&mut buf) {
            Ok(0) => {
                diagnostics::log(format!(
                    "mcp_proxy_eof name={} dir={} total_bytes={}",
                    name, direction, total
                ));
                break;
            }
            Ok(n) => {
                total += n as u64;
                diagnostics::log(format!(
                    "mcp_proxy_read name={} dir={} bytes={}",
                    name, direction, n
                ));
                if let Err(err) = writer.write_all(&buf[..n]) {
                    diagnostics::log(format!(
                        "mcp_proxy_write_failed name={} dir={} error={}",
                        name, direction, err
                    ));
                    break;
                }
                if flush_after_write {
                    if let Err(err) = writer.flush() {
                        diagnostics::log(format!(
                            "mcp_proxy_flush_failed name={} dir={} error={}",
                            name, direction, err
                        ));
                        break;
                    }
                }
                diagnostics::log(format!(
                    "mcp_proxy_write name={} dir={} bytes={}",
                    name, direction, n
                ));
            }
            Err(err) => {
                diagnostics::log(format!(
                    "mcp_proxy_read_failed name={} dir={} error={}",
                    name, direction, err
                ));
                break;
            }
        }
    }
    diagnostics::log(format!(
        "mcp_proxy_pump_done name={} dir={} total_bytes={}",
        name, direction, total
    ));
}
