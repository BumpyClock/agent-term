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

  const dev = spawn("pnpm", ["dev"], { cwd: rootDir, stdio: "inherit" });
  dev.on("exit", (exitCode) => process.exit(exitCode ?? 0));
});
