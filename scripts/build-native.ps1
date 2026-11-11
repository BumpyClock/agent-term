$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$deps = Join-Path $root '.deps'
$manifest = Get-Content (Join-Path $root 'native-dependencies.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force $deps | Out-Null

function Get-VerifiedArchive($url, $hash, $path) {
    if (-not (Test-Path $path)) { Invoke-WebRequest $url -OutFile $path }
    if ((Get-FileHash $path -Algorithm SHA256).Hash -ne $hash) {
        throw "SHA256 mismatch for $path. Remove the file and retry."
    }
}

$sourceArchive = Join-Path $deps 'ghostty.tar.gz'
$zigArchive = Join-Path $deps 'zig.zip'
Get-VerifiedArchive $manifest.ghostty.url $manifest.ghostty.sha256 $sourceArchive
Get-VerifiedArchive $manifest.zig.windows.url $manifest.zig.windows.sha256 $zigArchive
$source = Join-Path $deps "libghostty-vt-$($manifest.ghostty.version)"
$zigDir = Join-Path $deps "zig-x86_64-windows-$($manifest.zig.version)"
if (-not (Test-Path $source)) {
    & tar -xzf $sourceArchive -C $deps
    if ($LASTEXITCODE -ne 0) { throw 'Failed to extract Ghostty source.' }
}
if (-not (Test-Path (Join-Path $zigDir 'zig.exe'))) {
    & tar -xf $zigArchive -C $deps
    if ($LASTEXITCODE -ne 0) { throw 'Failed to extract Zig.' }
}
$prefix = Join-Path $root 'artifacts/native/win-x64'
Push-Location $source
try {
    & (Join-Path $zigDir 'zig.exe') build -Demit-lib-vt -Doptimize=ReleaseFast -Dtarget=x86_64-windows-gnu --prefix $prefix
    if ($LASTEXITCODE -ne 0) { throw "Native build failed with exit code $LASTEXITCODE." }
} finally { Pop-Location }
if (-not (Test-Path (Join-Path $prefix 'bin/ghostty-vt.dll'))) { throw 'Native build did not produce ghostty-vt.dll.' }
