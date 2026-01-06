import { useEffect, useMemo } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { Minus, Square, X } from "lucide-react";
import "./TitleBar.css";

function detectPlatform() {
  const ua = navigator.userAgent.toLowerCase();
  const platform = (navigator.platform || "").toLowerCase();
  const isMac = platform.includes("mac") || ua.includes("mac os");
  const isWindows = ua.includes("windows");
  return { isMac, isWindows };
}

export function TitleBar() {
  const { isMac, isWindows } = detectPlatform();
  const appWindow = useMemo(() => getCurrentWindow(), []);

  useEffect(() => {
    document.documentElement.style.setProperty(
      "--titlebar-height",
      isMac ? "18px" : "40px",
    );
	    document.documentElement.style.setProperty(
	      "--sidebar-top",
	      isMac ? "8px" : "calc(var(--titlebar-height) + 8px)",
	    );
    document.documentElement.style.setProperty(
      "--sidebar-content-top-padding",
      isMac ? "18px" : "0px",
    );
	    return () => {
	      document.documentElement.style.setProperty("--titlebar-height", "40px");
	      document.documentElement.style.setProperty(
	        "--sidebar-top",
	        "calc(var(--titlebar-height) + 8px)",
	      );
	      document.documentElement.style.setProperty(
	        "--sidebar-content-top-padding",
	        "0px",
      );
    };
  }, [isMac]);

  const handleDoubleClick = () => {
    if (!isWindows) return;
    void appWindow.toggleMaximize();
  };

  return (
    <div
      className="titlebar"
      data-platform={isMac ? "mac" : isWindows ? "windows" : "other"}
    >
      <div
        className="titlebar-drag"
        data-tauri-drag-region
        onDoubleClick={handleDoubleClick}
      >
        <div className="titlebar-title" data-tauri-drag-region>
          {isMac ? null : "Agent Term"}
        </div>
      </div>

      {isWindows ? (
        <div className="titlebar-controls">
          <button
            className="titlebar-btn"
            type="button"
            aria-label="Minimize"
            onClick={() => void appWindow.minimize()}
          >
            <Minus size={14} />
          </button>
          <button
            className="titlebar-btn"
            type="button"
            aria-label="Maximize"
            onClick={() => void appWindow.toggleMaximize()}
          >
            <Square size={12} />
          </button>
          <button
            className="titlebar-btn titlebar-btn-close"
            type="button"
            aria-label="Close"
            onClick={() => void appWindow.close()}
          >
            <X size={14} />
          </button>
        </div>
      ) : null}
    </div>
  );
}
