// ABOUTME: Renders the xterm.js terminal instance bound to a backend session.
// ABOUTME: Manages lifecycle of terminal IPC, sizing, and teardown handling.

import { useEffect, useRef, useCallback } from "react";
import { Terminal as XTerm, type IDisposable } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { WebLinksAddon } from "@xterm/addon-web-links";
import { WebglAddon } from "@xterm/addon-webgl";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { useTerminalStore } from "../store/terminalStore";
import { useTerminalSettings } from "../store/terminalSettingsStore";
import { getTerminalTheme, type TerminalColorSchemeId } from "@/lib/terminalThemes";
import "@xterm/xterm/css/xterm.css";

/**
 * Get the resolved app theme (light or dark) from the document class.
 */
function getResolvedAppTheme(): "light" | "dark" {
  return document.documentElement.classList.contains("dark") ? "dark" : "light";
}

const RESIZE_DEBOUNCE_MS = 150;

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
  const webglAddonRef = useRef<WebglAddon | null>(null);
  const initialCwdRef = useRef<string | null>(null);
  const lastSentSizeRef = useRef<{ rows: number; cols: number } | null>(null);
  const isActiveRef = useRef(isActive);
  const resizeDebounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const prevUseWebGLRef = useRef(useTerminalSettings.getState().useWebGL);
  const prevTerminalColorSchemeRef = useRef(useTerminalSettings.getState().terminalColorScheme);
  const setLastKnownSize = useTerminalStore((state) => state.setLastKnownSize);
  const logPrefix = `[terminal ${sessionId}]`;

  const syncSize = useCallback(() => {
    const fitAddon = fitAddonRef.current;
    if (!fitAddon) return;
    fitAddon.fit();
    const dims = fitAddon.proposeDimensions();
    if (dims && dims.rows > 0 && dims.cols > 0) {
      const { lastKnownRows, lastKnownCols } = useTerminalStore.getState();
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
  }, [sessionId, setLastKnownSize]);

  const initTerminal = useCallback(
    (container: HTMLDivElement) => {
      if (initialCwdRef.current === null) {
        initialCwdRef.current = cwd;
      }
      const decoder = new TextDecoder("utf-8");

      const termSettings = useTerminalSettings.getState();
      const appTheme = getResolvedAppTheme();
      const initialTerminalTheme = getTerminalTheme(termSettings.terminalColorScheme, appTheme);

      const xterm = new XTerm({
        cursorBlink: true,
        fontSize: termSettings.fontSize,
        fontFamily: termSettings.fontFamily,
        lineHeight: termSettings.lineHeight,
        letterSpacing: termSettings.letterSpacing,
        allowTransparency: true,
        theme: initialTerminalTheme,
      });

      const fitAddon = new FitAddon();
      const webLinksAddon = new WebLinksAddon();

      xterm.loadAddon(fitAddon);
      xterm.loadAddon(webLinksAddon);

      xterm.open(container);

      // WebGL addon loading/unloading functions
      const loadWebGL = () => {
        if (webglAddonRef.current) return; // Already loaded
        const webglAddon = new WebglAddon();
        webglAddon.onContextLoss(() => {
          webglAddon.dispose();
          webglAddonRef.current = null;
        });
        xterm.loadAddon(webglAddon);
        webglAddonRef.current = webglAddon;
      };

      const unloadWebGL = () => {
        if (webglAddonRef.current) {
          webglAddonRef.current.dispose();
          webglAddonRef.current = null;
        }
      };

      // Load WebGL addon if enabled in settings
      if (termSettings.useWebGL) {
        loadWebGL();
      }

      fitAddon.fit();

      xtermRef.current = xterm;
      fitAddonRef.current = fitAddon;
      const initialDims = fitAddon.proposeDimensions();
      const { lastKnownRows, lastKnownCols } = useTerminalStore.getState();
      const initialRows =
        initialDims && initialDims.rows > 0 ? initialDims.rows : lastKnownRows;
      const initialCols =
        initialDims && initialDims.cols > 0 ? initialDims.cols : lastKnownCols;

      let inputDisposable: IDisposable | null = null;
      let titleDisposable: IDisposable | null = null;
      let resizeObserver: ResizeObserver | null = null;
      let themeObserver: MutationObserver | null = null;
      let unlistenOutput: (() => void) | null = null;
      let unlistenExit: (() => void) | null = null;
      let unsubscribeSettings: (() => void) | null = null;
      let cancelled = false;
      let disposed = false;

      // Helper to update terminal theme
      const updateTerminalTheme = (schemeId: TerminalColorSchemeId) => {
        const currentAppTheme = getResolvedAppTheme();
        const newTheme = getTerminalTheme(schemeId, currentAppTheme);
        xterm.options.theme = newTheme;
      };

      // Subscribe to terminal settings changes
      unsubscribeSettings = useTerminalSettings.subscribe((state) => {
        xterm.options.fontSize = state.fontSize;
        xterm.options.fontFamily = state.fontFamily;
        xterm.options.lineHeight = state.lineHeight;
        xterm.options.letterSpacing = state.letterSpacing;
        fitAddon.fit();

        // Handle WebGL toggle
        if (state.useWebGL !== prevUseWebGLRef.current) {
          if (state.useWebGL) {
            loadWebGL();
          } else {
            unloadWebGL();
          }
          prevUseWebGLRef.current = state.useWebGL;
        }

        // Handle terminal color scheme change
        if (state.terminalColorScheme !== prevTerminalColorSchemeRef.current) {
          updateTerminalTheme(state.terminalColorScheme);
          prevTerminalColorSchemeRef.current = state.terminalColorScheme;
        }
      });

      // Watch for app theme changes (light/dark class on document)
      themeObserver = new MutationObserver((mutations) => {
        for (const mutation of mutations) {
          if (mutation.attributeName === "class") {
            // App theme changed, update terminal theme
            const currentScheme = useTerminalSettings.getState().terminalColorScheme;
            updateTerminalTheme(currentScheme);
            break;
          }
        }
      });

      themeObserver.observe(document.documentElement, {
        attributes: true,
        attributeFilter: ["class"],
      });

      const teardown = () => {
        if (disposed) return;
        disposed = true;
        unlistenOutput?.();
        unlistenExit?.();
        unsubscribeSettings?.();
        themeObserver?.disconnect();
        resizeObserver?.disconnect();
        inputDisposable?.dispose();
        titleDisposable?.dispose();
        unloadWebGL();
        if (resizeDebounceRef.current) {
          clearTimeout(resizeDebounceRef.current);
          resizeDebounceRef.current = null;
        }
        console.debug(`${logPrefix} teardown`, {
          cancelled,
          disposed,
          active: isActiveRef.current,
        });
        invoke("stop_session", { id: sessionId })
          .then(() => {
            console.debug(`${logPrefix} stop_session sent`);
          })
          .catch((err) => {
            console.error(`${logPrefix} stop_session failed`, err);
          });
        xterm.dispose();
      };

      const start = async () => {
        if (cancelled) return;

        let outputEvents = 0;
        // Set up event listeners BEFORE starting the session to avoid missing early output
        inputDisposable = xterm.onData((data) => {
          if (cancelled) return;
          invoke("write_session_input", {
            id: sessionId,
            data,
          }).catch((err) => console.error(`${logPrefix} write_session_input error:`, err));
        });

        // Listen for OSC title changes (e.g., when apps set terminal title via escape sequences)
        titleDisposable = xterm.onTitleChange((newTitle: string) => {
          if (cancelled || !newTitle.trim()) return;
          // Update dynamic title in store (respects isCustomTitle lock)
          useTerminalStore.getState().updateDynamicTitle(sessionId, newTitle);
        });

        if (cancelled) { teardown(); return; }

        // Setup event listeners with try/catch
        try {
          unlistenOutput = await listen<{
            sessionId: string;
            data: number[];
          }>("session-output", (event) => {
            if (event.payload.sessionId !== sessionId) return;
            const bytes = new Uint8Array(event.payload.data);
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
          });
        } catch (err) {
          console.error(`${logPrefix} Failed to listen to session-output:`, err);
          xterm.write(`\r\n\x1b[31mFailed to connect to session output\x1b[0m\r\n`);
          teardown();
          return;
        }

        if (cancelled) { teardown(); return; }

        try {
          unlistenExit = await listen<{ sessionId: string }>(
            "session-exit",
            (event) => {
              if (event.payload.sessionId !== sessionId) return;
              const tail = decoder.decode();
              if (tail.length > 0) {
                xterm.write(tail);
              }
              xterm.write("\r\n\x1b[33m[Process exited]\x1b[0m\r\n");
              console.info(`${logPrefix} session-exit`);
            },
          );
        } catch (err) {
          console.error(`${logPrefix} Failed to listen to session-exit:`, err);
        }

        if (cancelled) { teardown(); return; }

        resizeObserver = new ResizeObserver(() => {
          if (!isActiveRef.current) return;
          if (container.clientWidth === 0 || container.clientHeight === 0) {
            return;
          }
          // Debounce resize events to prevent IPC flooding during rapid window resizing
          if (resizeDebounceRef.current) {
            clearTimeout(resizeDebounceRef.current);
          }
          resizeDebounceRef.current = setTimeout(() => {
            resizeDebounceRef.current = null;
            syncSize();
          }, RESIZE_DEBOUNCE_MS);
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
          if (!cancelled) {
            console.error(`${logPrefix} Failed to start session:`, err);
            xterm.write(
              `\r\n\x1b[31mFailed to start session: ${err}\x1b[0m\r\n`,
            );
          }
          teardown();
          return;
        }

        if (cancelled) {
          teardown();
          return;
        }
      };

      start().catch((err) => {
        if (!cancelled) {
          console.error(`${logPrefix} start() error:`, err);
          xterm.write(`\r\n\x1b[31mTerminal initialization failed: ${err}\x1b[0m\r\n`);
        }
      });

      return () => {
        cancelled = true;
        teardown();
      };
    },
    [sessionId, syncSize],
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
