# ABOUTME: PowerShell packaging script for Agent Term, focused on Windows installers and build targets.
# ABOUTME: Builds x64/x86/arm64 binaries and produces MSI installers via cargo-wix.

[CmdletBinding()]
param(
	[Parameter(Position = 0)]
	[string]$Command = "help",

	# For commands that accept a Rust target triple directly (advanced usage)
	[string]$Target,

	# Optional path used by some future commands (kept for parity with bash script style)
	[string]$Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$APP_NAME = "agentterm"
$DISPLAY_NAME = "Agent Term"
$CARGO_TOML = Join-Path $PSScriptRoot "..\Cargo.toml"

function Write-Info([string]$Message) { Write-Host "[INFO] $Message" -ForegroundColor Green }
function Write-Warn([string]$Message) { Write-Host "[WARN] $Message" -ForegroundColor Yellow }
function Write-Err([string]$Message)  { Write-Host "[ERROR] $Message" -ForegroundColor Red; exit 1 }

function Get-PackageVersion {
	if (-not (Test-Path -LiteralPath $CARGO_TOML)) {
		return "0.0.0"
	}

	$lines = Get-Content -LiteralPath $CARGO_TOML
	$inPackage = $false
	foreach ($line in $lines) {
		$trim = $line.Trim()
		if ($trim -eq "[package]") {
			$inPackage = $true
			continue
		}
		if ($inPackage -and $trim.StartsWith("[")) {
			break
		}
		if ($inPackage) {
			$m = [regex]::Match($trim, '^version\s*=\s*"(?<v>[^"]+)"\s*$')
			if ($m.Success) {
				return $m.Groups['v'].Value
			}
		}
	}

	return "0.0.0"
}

$VERSION = Get-PackageVersion

function Assert-Tool([string]$Exe, [string]$InstallHint) {
	if (-not (Get-Command $Exe -ErrorAction SilentlyContinue)) {
		Write-Err "$Exe is required but not installed. $InstallHint"
	}
}

function Assert-WixToolset {
	# cargo-wix shells out to WiX Toolset (v3) executables: candle.exe and light.exe.
	$hasCandle = $null -ne (Get-Command "candle" -ErrorAction SilentlyContinue)
	$hasLight  = $null -ne (Get-Command "light"  -ErrorAction SilentlyContinue)

	if (-not ($hasCandle -and $hasLight)) {
		Write-Error "WiX Toolset executables (candle/light) not found on PATH."
		Write-Error "Install WiX Toolset v3 (commonly via: choco install wixtoolset) and reopen your terminal."
		throw "WiX Toolset validation failed"
	}
}

function Ensure-RustTarget([string]$RustTarget) {
	if ([string]::IsNullOrWhiteSpace($RustTarget)) {
		return
	}

	if (Get-Command rustup -ErrorAction SilentlyContinue) {
		Write-Info "Ensuring Rust target is installed: $RustTarget"
		# rustup target add is idempotent (returns success if already installed)
		& rustup target add $RustTarget | Out-Host
	}
	else {
		Write-Warn "rustup not found; cannot auto-install Rust target '$RustTarget'."
		Write-Warn "If the build fails, install rustup or add the target from your Rust toolchain settings."
	}
}

function Build-Release([string]$RustTarget) {
	Write-Info "Building release binary..."

	if ([string]::IsNullOrWhiteSpace($RustTarget)) {
		& cargo build --release | Out-Host
		$exitCode = $LASTEXITCODE
		if ($exitCode -ne 0) {
			Write-Info "cargo build failed (target: default)"
			Write-Error "cargo build --release failed with exit code $exitCode"
			exit $exitCode
		}
		return
	}

	Ensure-RustTarget -RustTarget $RustTarget
	& cargo build --release --target $RustTarget | Out-Host
	$exitCode = $LASTEXITCODE
	if ($exitCode -ne 0) {
		Write-Info "cargo build failed (target: $RustTarget)"
		Write-Error "cargo build --release --target $RustTarget failed with exit code $exitCode"
		exit $exitCode
	}
}

function Package-WindowsMsi([string]$RustTarget) {
	Write-Info "Packaging for Windows (.msi)..."

	Assert-Tool -Exe "cargo" -InstallHint "Install Rust and Cargo from https://rustup.rs/"
	Assert-Tool -Exe "cargo-wix" -InstallHint "Install with: cargo install cargo-wix"
	Assert-WixToolset

	if (-not (Test-Path -LiteralPath "wix")) {
		Write-Info "Initializing WiX configuration (creates ./wix)..."
		& cargo wix init | Out-Host
		$exitCode = $LASTEXITCODE
		if ($exitCode -ne 0) {
			Write-Info "cargo wix init failed"
			Write-Error "cargo wix init failed with exit code $exitCode"
			exit $exitCode
		}
	}

	if (-not [string]::IsNullOrWhiteSpace($RustTarget)) {
		Ensure-RustTarget -RustTarget $RustTarget
		& cargo wix --nocapture --target $RustTarget | Out-Host
		$exitCode = $LASTEXITCODE
		if ($exitCode -ne 0) {
			Write-Info "cargo wix failed (target: $RustTarget)"
			Write-Error "cargo wix --nocapture --target $RustTarget failed with exit code $exitCode"
			exit $exitCode
		}
		Write-Info "Windows .msi installer created in: target\$RustTarget\wix\"
	}
	else {
		& cargo wix --nocapture | Out-Host
		$exitCode = $LASTEXITCODE
		if ($exitCode -ne 0) {
			Write-Info "cargo wix failed (target: default)"
			Write-Error "cargo wix --nocapture failed with exit code $exitCode"
			exit $exitCode
		}
		Write-Info "Windows .msi installer created in: target\wix\"
	}
}

function Show-Help {
	Write-Host "Agent Term Packaging Script (PowerShell)"
	Write-Host ""
	Write-Host "Usage: .\\scripts\\package.ps1 <command>"
	Write-Host ""
	Write-Host "Commands:"
	Write-Host "  windows           Build Windows .msi for host target"
	Write-Host "  windows-x64       Build Windows x64 .msi (x86_64-pc-windows-msvc)"
	Write-Host "  windows-x86       Build Windows x86 .msi (i686-pc-windows-msvc)"
	Write-Host "  windows-arm64     Build Windows arm64 .msi (aarch64-pc-windows-msvc)"
	Write-Host "  all-windows       Build .msi for x64, x86, and arm64"
	Write-Host "  build-x64         Build release binary for x64"
	Write-Host "  build-x86         Build release binary for x86"
	Write-Host "  build-arm64       Build release binary for arm64"
	Write-Host "  install-tools     Install cargo-wix (and print WiX guidance)"
	Write-Host "  help              Show this help"
	Write-Host ""
	Write-Host "Notes:"
	Write-Host "  - Version detected from Cargo.toml: $VERSION"
	Write-Host "  - WiX Toolset v3 is required on PATH for MSI creation (candle.exe/light.exe)."
}

function Install-Tools {
	Write-Info "Installing packaging tools..."

	Assert-Tool -Exe "cargo" -InstallHint "Install Rust and Cargo from https://rustup.rs/"

	& cargo install cargo-wix | Out-Host

	Write-Info "Installed cargo-wix."
	Write-Warn "You still need WiX Toolset (candle/light) available on PATH for MSI builds."
	Write-Warn "Common install: choco install wixtoolset"
}

# Main entry point
switch ($Command.ToLowerInvariant()) {
	"windows" {
		Package-WindowsMsi -RustTarget $Target
	}

	"windows-x64" {
		Package-WindowsMsi -RustTarget "x86_64-pc-windows-msvc"
	}

	"windows-x86" {
		Package-WindowsMsi -RustTarget "i686-pc-windows-msvc"
	}

	"windows-arm64" {
		Package-WindowsMsi -RustTarget "aarch64-pc-windows-msvc"
	}

	"all-windows" {
		Package-WindowsMsi -RustTarget "x86_64-pc-windows-msvc"
		Package-WindowsMsi -RustTarget "i686-pc-windows-msvc"
		Package-WindowsMsi -RustTarget "aarch64-pc-windows-msvc"
	}

	"build-x64" {
		Build-Release -RustTarget "x86_64-pc-windows-msvc"
	}

	"build-x86" {
		Build-Release -RustTarget "i686-pc-windows-msvc"
	}

	"build-arm64" {
		Build-Release -RustTarget "aarch64-pc-windows-msvc"
	}

	"install-tools" {
		Install-Tools
	}

	{ $_ -in @("help", "-h", "--help") } {
		Show-Help
	}

	default {
		Write-Err "Unknown command: $Command. Run '.\\scripts\\package.ps1 help' for usage."
	}
}