import { useEffect, useRef, useCallback } from "react";
import { Terminal as XTerm, type IDisposable } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { WebLinksAddon } from "@xterm/addon-web-links";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { useTerminalStore } from "../store/terminalStore";
import { useShallow } from "zustand/shallow";
import "@xterm/xterm/css/xterm.css";

interface TerminalProps {
  id: string;
  sessionId: string;
  cwd: string;
  isActive: boolean;
}

export function Terminal({ sessionId, cwd, isActive }: TerminalProps) {
  const terminalRef = useRef<HTMLDivElement>(null);
  const xtermRef = useRef<XTerm | null>(null);
  const fitAddonRef = useRef<FitAddon | null>(null);
  const initialCwdRef = useRef<string | null>(null);
  const lastSentSizeRef = useRef<{ rows: number; cols: number } | null>(null);
  const isActiveRef = useRef(isActive);
  const { lastKnownRows, lastKnownCols, setLastKnownSize } = useTerminalStore(
    useShallow((state) => ({
      lastKnownRows: state.lastKnownRows,
      lastKnownCols: state.lastKnownCols,
      setLastKnownSize: state.setLastKnownSize,
    })),
  );

  const syncSize = useCallback(() => {
    const fitAddon = fitAddonRef.current;
    if (!fitAddon) return;
    fitAddon.fit();
    const dims = fitAddon.proposeDimensions();
    if (dims && dims.rows > 0 && dims.cols > 0) {
      const lastSent = lastSentSizeRef.current;
      if (lastSent && lastSent.rows === dims.rows && lastSent.cols === dims.cols) {
        return;
      }
      invoke("resize_session", {
        id: sessionId,
        rows: dims.rows,
        cols: dims.cols,
      }).catch(console.error);
      lastSentSizeRef.current = { rows: dims.rows, cols: dims.cols };
      if (dims.rows !== lastKnownRows || dims.cols !== lastKnownCols) {
        setLastKnownSize(dims.rows, dims.cols);
      }
    }
  }, [sessionId, setLastKnownSize, lastKnownRows, lastKnownCols]);

  const initTerminal = useCallback(
    (container: HTMLDivElement) => {
      if (initialCwdRef.current === null) {
        initialCwdRef.current = cwd;
      }
      const decoder = new TextDecoder("utf-8");

      const xterm = new XTerm({
        cursorBlink: true,
        fontSize: 14,
        fontFamily:
          '"FiraCode Nerd Font", Menlo, Monaco, "Courier New", monospace',
        allowTransparency: true,
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
      const initialDims = fitAddon.proposeDimensions();
      const initialRows =
        initialDims && initialDims.rows > 0 ? initialDims.rows : lastKnownRows;
      const initialCols =
        initialDims && initialDims.cols > 0 ? initialDims.cols : lastKnownCols;

      let inputDisposable: IDisposable | null = null;
      let resizeObserver: ResizeObserver | null = null;
      let unlistenOutput: (() => void) | null = null;
      let unlistenExit: (() => void) | null = null;
      let cancelled = false;
      let disposed = false;

      const teardown = () => {
        if (disposed) return;
        disposed = true;
        unlistenOutput?.();
        unlistenExit?.();
        resizeObserver?.disconnect();
        inputDisposable?.dispose();
        invoke("stop_session", { id: sessionId }).catch(console.error);
        xterm.dispose();
      };

      const decodeBase64 = (input: string) => {
        const binary = atob(input);
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i += 1) {
          bytes[i] = binary.charCodeAt(i);
        }
        return bytes;
      };

      const start = async () => {
        const logPrefix = `[terminal ${sessionId}]`;
        let outputEvents = 0;
        // Set up event listeners BEFORE starting the session to avoid missing early output
        inputDisposable = xterm.onData((data) => {
          invoke("write_session_input", {
            id: sessionId,
            data,
          }).catch(console.error);
        });

        unlistenOutput = await listen<{
          sessionId: string;
          dataBase64: string;
        }>("session-output", (event) => {
          if (event.payload.sessionId === sessionId) {
            const bytes = decodeBase64(event.payload.dataBase64);
            const text = decoder.decode(bytes, { stream: true });
            if (text.length > 0) {
              xterm.write(text);
            }
            outputEvents += 1;
            if (outputEvents <= 3) {
              console.debug(`${logPrefix} output`, {
                bytes: bytes.length,
                events: outputEvents,
              });
            }
          }
        });

        unlistenExit = await listen<{ sessionId: string }>(
          "session-exit",
          (event) => {
            if (event.payload.sessionId === sessionId) {
              const tail = decoder.decode();
              if (tail.length > 0) {
                xterm.write(tail);
              }
              xterm.write("\r\n\x1b[33m[Process exited]\x1b[0m\r\n");
              console.info(`${logPrefix} session-exit`);
            }
          },
        );

        resizeObserver = new ResizeObserver(() => {
          if (!isActiveRef.current) return;
          if (container.clientWidth === 0 || container.clientHeight === 0) {
            return;
          }
          syncSize();
        });

        resizeObserver.observe(container);

        // Now start the session after listeners are ready
        try {
          console.info(`${logPrefix} start_session`, {
            rows: initialDims?.rows ?? 24,
            cols: initialDims?.cols ?? 80,
          });
          await invoke("start_session", {
            id: sessionId,
            rows: initialRows ?? 24,
            cols: initialCols ?? 80,
          });
          console.info(`${logPrefix} start_session ok`);
        } catch (err) {
          console.error("Failed to start session:", err);
          xterm.write(
            `\r\n\x1b[31mFailed to start session: ${err}\x1b[0m\r\n`,
          );
          return;
        }

        if (cancelled) {
          teardown();
          return;
        }
      };

      start().catch(console.error);

      return () => {
        cancelled = true;
        teardown();
      };
    },
    [sessionId, syncSize, lastKnownRows, lastKnownCols],
  );

  useEffect(() => {
    if (!terminalRef.current) return;
    return initTerminal(terminalRef.current);
  }, [initTerminal]);

  useEffect(() => {
    if (!isActive) return;
    const raf = requestAnimationFrame(() => {
      if (xtermRef.current) {
        xtermRef.current.focus();
      }
      syncSize();
      invoke("acknowledge_session", { id: sessionId }).catch(console.error);
    });
    return () => cancelAnimationFrame(raf);
  }, [isActive, sessionId, syncSize]);

  useEffect(() => {
    isActiveRef.current = isActive;
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
