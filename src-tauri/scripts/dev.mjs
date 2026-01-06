// ABOUTME: Builds the MCP sidecar and then starts the frontend development server.
// ABOUTME: Uses npm to run the dev script to ensure cross-platform compatibility on Windows.
import { spawn } from "node:child_process";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDir = dirname(fileURLToPath(import.meta.url));
const rootDir = resolve(scriptDir, "..", "..");

const build = spawn("node", [resolve(scriptDir, "build-sidecar.mjs")], {
  cwd: rootDir,
  stdio: "inherit",
});

build.on("exit", (code) => {
  if (code !== 0) {
    process.exit(code ?? 1);
    return;
  }
  // If the dev server is already running on the configured port, skip starting another one.
  const devUrl = "http://localhost:1420";
  const timeoutMs = 1000;

  const checkDevServer = async () => {
    try {
      const controller = new AbortController();
      const t = setTimeout(() => controller.abort(), timeoutMs);
      const res = await fetch(devUrl, { signal: controller.signal });
      clearTimeout(t);
      return res.ok || (res.status >= 200 && res.status < 500);
    } catch (_) {
      return false;
    }
  };

  checkDevServer().then((running) => {
    if (running) {
      console.log(`Dev server already running at ${devUrl}; skipping start.`);
      process.exit(0);
      return;
    }

    const dev = spawn("npm", ["run", "dev"], { cwd: rootDir, stdio: "inherit", shell: true });
    dev.on("exit", (exitCode) => process.exit(exitCode ?? 0));
  });
});
