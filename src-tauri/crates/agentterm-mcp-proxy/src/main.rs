use std::env;
use std::io::{self, Write};
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

    diagnostics::log(format!(
        "mcp_proxy_start name={} endpoint={}",
        args.name,
        endpoint.display()
    ));

    let mut stream = match connect_with_retry(&endpoint) {
        Ok(stream) => stream,
        Err(err) => {
            diagnostics::log(format!(
                "mcp_proxy_connect_failed name={} error={}",
                args.name, err
            ));
            std::process::exit(1);
        }
    };

    diagnostics::log(format!("mcp_proxy_connected name={}", args.name));

    let mut writer = match stream.try_clone() {
        Ok(writer) => writer,
        Err(err) => {
            diagnostics::log(format!(
                "mcp_proxy_stream_clone_failed name={} error={}",
                args.name, err
            ));
            std::process::exit(1);
        }
    };

    let stdin_thread = thread::spawn(move || {
        let mut stdin = io::stdin();
        let _ = io::copy(&mut stdin, &mut writer);
    });

    let mut stdout = io::stdout();
    let _ = io::copy(&mut stream, &mut stdout);
    let _ = stdout.flush();
    let _ = stdin_thread.join();
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
