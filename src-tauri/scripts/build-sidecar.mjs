import { execFileSync } from "node:child_process";
import { copyFileSync, existsSync, mkdirSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(scriptDir, "..", "..");
const manifestPath = resolve(repoRoot, "src-tauri", "crates", "agentterm-mcp-proxy", "Cargo.toml");
const targetDir = process.env.CARGO_TARGET_DIR
  ? resolve(process.env.CARGO_TARGET_DIR)
  : resolve(repoRoot, "src-tauri", "target");
const binDir = resolve(repoRoot, "src-tauri", "bin");
const binBase = "agentterm-mcp-proxy";
const binName = process.platform === "win32" ? `${binBase}.exe` : binBase;
const builtPath = resolve(targetDir, "release", binName);
const targetTriple = resolveTargetTriple();
const targetSuffix = targetTriple ? `-${targetTriple}` : "";
const destName = process.platform === "win32"
  ? `${binBase}${targetSuffix}.exe`
  : `${binBase}${targetSuffix}`;
const destPath = resolve(binDir, destName);

execFileSync(
  "cargo",
  ["build", "--release", "--manifest-path", manifestPath],
  { stdio: "inherit" }
);

if (!existsSync(builtPath)) {
  throw new Error(`sidecar build missing: ${builtPath}`);
}

mkdirSync(binDir, { recursive: true });
copyFileSync(builtPath, destPath);

console.log(`sidecar copied to ${destPath}`);

function resolveTargetTriple() {
  const envTarget = process.env.TAURI_ENV_TARGET_TRIPLE || process.env.TARGET;
  if (envTarget) {
    return envTarget.trim();
  }
  try {
    const output = execFileSync("rustc", ["-vV"], { encoding: "utf8" });
    const match = output.match(/^host:\s*(.+)$/m);
    if (match) {
      return match[1].trim();
    }
  } catch {
    // fall through
  }
  return "";
}
