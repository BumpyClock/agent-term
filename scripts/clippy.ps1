#!/usr/bin/env pwsh

$ErrorActionPreference = "Stop"

# Check if a package flag is specified
$hasPackageFlag = $args -contains "-p" -or $args -contains "--package"

# Build the arguments list
# Note: Lint levels are configured in workspace Cargo.toml [workspace.lints.clippy]
# Crates inherit these via [lints] workspace = true
$clippy_args = @()
if (-not $hasPackageFlag) {
    $clippy_args += "--workspace"
}
$clippy_args += $args
$clippy_args += "--release"
$clippy_args += "--all-targets"

# Use CARGO env var if set, otherwise default to cargo
$cargo = if ($env:CARGO) { $env:CARGO } else { "cargo" }

Write-Host "Running: $cargo clippy $($clippy_args -join ' ')"
& $cargo clippy @clippy_args
