import { useEffect, useRef, useCallback } from "react";
import { Terminal as XTerm, type IDisposable } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { WebLinksAddon } from "@xterm/addon-web-links";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import "@xterm/xterm/css/xterm.css";

interface TerminalProps {
  id: string;
  cwd: string;
  isActive: boolean;
}

export function Terminal({ id, cwd, isActive }: TerminalProps) {
  const terminalRef = useRef<HTMLDivElement>(null);
  const xtermRef = useRef<XTerm | null>(null);
  const fitAddonRef = useRef<FitAddon | null>(null);
  const initialCwdRef = useRef<string | null>(null);
  const initTerminal = useCallback(
    (container: HTMLDivElement) => {
      const sessionId =
        typeof crypto !== "undefined" && "randomUUID" in crypto
          ? crypto.randomUUID()
          : `${id}-${Date.now()}-${Math.random()}`;
      if (initialCwdRef.current === null) {
        initialCwdRef.current = cwd;
      }
      const initialCwd = initialCwdRef.current || "";
      console.debug("[terminal] init", { id, sessionId, cwd: initialCwd });
      const decoder = new TextDecoder("utf-8");

      const xterm = new XTerm({
        cursorBlink: true,
        fontSize: 14,
        fontFamily:
          '"FiraCode Nerd Font", Menlo, Monaco, "Courier New", monospace',
        theme: {
          background: "transparent",
          foreground: "#d4d4d4",
          cursor: "#d4d4d4",
          cursorAccent: "#1e1e1e",
          selectionBackground: "#264f78",
          black: "#000000",
          red: "#cd3131",
          green: "#0dbc79",
          yellow: "#e5e510",
          blue: "#2472c8",
          magenta: "#bc3fbc",
          cyan: "#11a8cd",
          white: "#e5e5e5",
          brightBlack: "#666666",
          brightRed: "#f14c4c",
          brightGreen: "#23d18b",
          brightYellow: "#f5f543",
          brightBlue: "#3b8eea",
          brightMagenta: "#d670d6",
          brightCyan: "#29b8db",
          brightWhite: "#e5e5e5",
        },
      });

      const fitAddon = new FitAddon();
      const webLinksAddon = new WebLinksAddon();

      xterm.loadAddon(fitAddon);
      xterm.loadAddon(webLinksAddon);

      xterm.open(container);
      fitAddon.fit();

      xtermRef.current = xterm;
      fitAddonRef.current = fitAddon;
      console.debug("[terminal] xterm_ready", { id, sessionId });

      const initialDims = fitAddon.proposeDimensions();

      let inputDisposable: IDisposable | null = null;
      let resizeObserver: ResizeObserver | null = null;
      let unlistenOutput: (() => void) | null = null;
      let unlistenExit: (() => void) | null = null;
      let cancelled = false;
      let disposed = false;

      const teardown = () => {
        if (disposed) return;
        disposed = true;
        console.debug("[terminal] cleanup", { id });
        unlistenOutput?.();
        unlistenExit?.();
        resizeObserver?.disconnect();
        inputDisposable?.dispose();
        invoke("close_terminal", { terminalId: id }).catch(console.error);
        xterm.dispose();
      };

      const start = async () => {
        try {
          const payload: Record<string, unknown> = {
            terminalId: id,
            sessionId,
            rows: initialDims?.rows ?? 24,
            cols: initialDims?.cols ?? 80,
          };
          if (initialCwd) {
            payload.cwd = initialCwd;
          }
          await invoke("create_terminal", payload);
          console.debug("[terminal] create_terminal_done", { id, sessionId });
        } catch (err) {
          console.error("Failed to create terminal:", err);
          xterm.write(
            `\r\n\x1b[31mFailed to create terminal: ${err}\x1b[0m\r\n`,
          );
          return;
        }

        if (cancelled) {
          teardown();
          return;
        }

        if (initialDims) {
          invoke("resize_terminal", {
            terminalId: id,
            rows: initialDims.rows,
            cols: initialDims.cols,
          }).catch(console.error);
        }

        const decodeBase64 = (input: string) => {
          const binary = atob(input);
          const bytes = new Uint8Array(binary.length);
          for (let i = 0; i < binary.length; i += 1) {
            bytes[i] = binary.charCodeAt(i);
          }
          return bytes;
        };

        inputDisposable = xterm.onData((data) => {
          console.debug("[terminal] input", { id, size: data.length });
          invoke("write_terminal", {
            terminalId: id,
            data,
          }).catch(console.error);
        });

        unlistenOutput = await listen<{
          terminal_id: string;
          session_id: string;
          data_base64: string;
        }>(
          "terminal-output",
          (event) => {
            if (
              event.payload.terminal_id === id &&
              event.payload.session_id === sessionId
            ) {
              const bytes = decodeBase64(event.payload.data_base64);
              const text = decoder.decode(bytes, { stream: true });
              if (text.length > 0) {
                xterm.write(text);
              }
              console.debug("[terminal] output", {
                id,
                sessionId,
                size: bytes.length,
              });
            }
          },
        );

        unlistenExit = await listen<{ terminal_id: string; session_id: string }>(
          "terminal-exit",
          (event) => {
            if (
              event.payload.terminal_id === id &&
              event.payload.session_id === sessionId
            ) {
              const tail = decoder.decode();
              if (tail.length > 0) {
                xterm.write(tail);
              }
              console.debug("[terminal] exit", { id, sessionId });
              xterm.write("\r\n\x1b[33m[Process exited]\x1b[0m\r\n");
            }
          },
        );

        resizeObserver = new ResizeObserver(() => {
          if (fitAddonRef.current) {
            fitAddonRef.current.fit();
            const dims = fitAddonRef.current.proposeDimensions();
            if (dims) {
              console.debug("[terminal] resize", {
                id,
                rows: dims.rows,
                cols: dims.cols,
              });
              invoke("resize_terminal", {
                terminalId: id,
                rows: dims.rows,
                cols: dims.cols,
              }).catch(console.error);
            }
          }
        });

        resizeObserver.observe(container);
      };

      start().catch(console.error);

      return () => {
        cancelled = true;
        teardown();
      };
    },
    [id],
  );

  useEffect(() => {
    if (!terminalRef.current) return;
    return initTerminal(terminalRef.current);
  }, [initTerminal]);

  useEffect(() => {
    if (isActive && xtermRef.current) {
      xtermRef.current.focus();
      fitAddonRef.current?.fit();
    }
  }, [isActive]);

  return (
    <div
      ref={terminalRef}
      style={{
        width: "100%",
        height: "100%",
        display: isActive ? "block" : "none",
      }}
    />
  );
}
