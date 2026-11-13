$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$deps = Join-Path $root '.deps'
$manifest = Get-Content (Join-Path $root 'native-dependencies.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force $deps | Out-Null

# Archives and extractions are named by version and stamped with the archive hash, so a pin change can never
# reuse an older download or source tree.
function Get-VerifiedArchive($url, $hash, $path) {
    if ((Test-Path $path) -and (Get-FileHash $path -Algorithm SHA256).Hash -ne $hash) { Remove-Item $path -Force }
    if (-not (Test-Path $path)) {
        $partial = "$path.partial"
        Invoke-WebRequest $url -OutFile $partial
        $actual = (Get-FileHash $partial -Algorithm SHA256).Hash
        if ($actual -ne $hash) {
            Remove-Item $partial -Force
            throw "SHA256 mismatch for $url. Expected $hash, got $actual."
        }
        Move-Item $partial $path -Force
    }
}

function Confirm-Signature($archive, $signatureUrl, $publicKey) {
    $minisign = Get-Command minisign -ErrorAction SilentlyContinue
    if (-not $minisign) {
        Write-Warning 'minisign is not installed; relying on the pinned SHA256 only. Install it with: winget install jedisct1.minisign'
        return
    }
    $signature = "$archive.minisig"
    if (-not (Test-Path $signature)) { Invoke-WebRequest $signatureUrl -OutFile $signature }
    & $minisign.Source -Vm $archive -x $signature -P $publicKey | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Remove-Item $signature -Force
        throw "Signature verification failed for $archive."
    }
}

function Expand-Stamped($archive, $hash, $directory, $extract) {
    $stamp = Join-Path $directory '.agentterm-archive-sha256'
    if ((Test-Path $stamp) -and (Get-Content $stamp -Raw).Trim() -eq $hash) { return }
    if (Test-Path $directory) { Remove-Item $directory -Recurse -Force }
    $staging = Join-Path $deps ('extract-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $staging | Out-Null
    try {
        & $extract $archive $staging
        $expanded = Join-Path $staging (Split-Path $directory -Leaf)
        if (-not (Test-Path $expanded)) { throw "Archive $archive did not contain $(Split-Path $directory -Leaf)." }
        Move-Item $expanded $directory
        Set-Content $stamp $hash -NoNewline
    } finally { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue }
}

$ghostty = $manifest.ghostty
$sourceArchive = Join-Path $deps "libghostty-vt-$($ghostty.version).tar.gz"
$zigArchive = Join-Path $deps "zig-x86_64-windows-$($manifest.zig.version).zip"
Get-VerifiedArchive $ghostty.url $ghostty.sha256 $sourceArchive
if ($ghostty.signature) { Confirm-Signature $sourceArchive $ghostty.signature $ghostty.minisignPublicKey }
Get-VerifiedArchive $manifest.zig.windows.url $manifest.zig.windows.sha256 $zigArchive

$source = Join-Path $deps "libghostty-vt-$($ghostty.version)"
$zigDir = Join-Path $deps "zig-x86_64-windows-$($manifest.zig.version)"
Expand-Stamped $sourceArchive $ghostty.sha256 $source {
    param($archive, $destination)
    & tar -xzf $archive -C $destination
    if ($LASTEXITCODE -ne 0) { throw 'Failed to extract Ghostty source.' }
}
Expand-Stamped $zigArchive $manifest.zig.windows.sha256 $zigDir {
    param($archive, $destination)
    & tar -xf $archive -C $destination
    if ($LASTEXITCODE -ne 0) { throw 'Failed to extract Zig.' }
}

$prefix = if ($env:AGENTTERM_NATIVE_PREFIX) { $env:AGENTTERM_NATIVE_PREFIX } else { Join-Path $root 'artifacts/native/win-x64' }
Push-Location $source
try {
    & (Join-Path $zigDir 'zig.exe') build -Demit-lib-vt -Doptimize=ReleaseFast -Dtarget=x86_64-windows-gnu --prefix $prefix
    if ($LASTEXITCODE -ne 0) { throw "Native build failed with exit code $LASTEXITCODE." }
} finally { Pop-Location }
$dll = Join-Path $prefix 'bin/ghostty-vt.dll'
if (-not (Test-Path $dll)) { throw 'Native build did not produce ghostty-vt.dll.' }
[ordered]@{
    ghostty = $ghostty.version
    sourceSha256 = $ghostty.sha256
    zig = $manifest.zig.version
    dllSha256 = (Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant()
} | ConvertTo-Json | Set-Content (Join-Path $prefix 'native-build.json')
