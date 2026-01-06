# ABOUTME: Runs the Tauri dev server with diagnostics enabled on Windows PowerShell.
# ABOUTME: Temporarily sets AGENT_TERM_DIAG=1, starts 'pnpm tauri dev', then restores the previous value.

param()

# Ensure we're in the project root (where this script resides)
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $scriptDir

# Optional: check that pnpm is available
if (-not (Get-Command pnpm -ErrorAction SilentlyContinue)) {
    Write-Error "'pnpm' was not found. Please install pnpm: https://pnpm.io/installation"
    Pop-Location
    exit 1
}

# Preserve any existing value
$prev = $env:AGENT_TERM_DIAG

try {
    $env:AGENT_TERM_DIAG = "1"

    # Start the Tauri dev server
    pnpm tauri dev
}
finally {
    # Restore prior environment state
    if ($null -ne $prev) {
        $env:AGENT_TERM_DIAG = $prev
    } else {
        Remove-Item Env:AGENT_TERM_DIAG -ErrorAction SilentlyContinue
    }

    Pop-Location
}