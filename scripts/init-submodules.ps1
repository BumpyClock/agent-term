# ABOUTME: PowerShell script to initialize and update git submodules
# ABOUTME: Mirrors the functionality of init-submodules.sh for Windows environments

$ErrorActionPreference = 'Stop'

try {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    Set-Location $repoRoot

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Write-Error "error: git is not installed or not on PATH" -ErrorAction Stop
    }

    Write-Host "Initializing/updating git submodules..."
    git submodule sync --recursive
    git submodule update --init --recursive

    Write-Host "Done."
}
catch {
    Write-Error $_.Exception.Message -ErrorAction Stop
}
