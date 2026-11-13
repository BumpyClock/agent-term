param(
    [string]$BaselineRevision = '5606a46',
    [string]$OutputDirectory = 'artifacts/perf-comparison'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$native = Join-Path $root 'artifacts/native/win-x64/bin/ghostty-vt.dll'
if (-not (Test-Path -LiteralPath $native)) { throw 'Run scripts/build-native.ps1 before building the performance fixtures.' }
$output = [IO.Path]::GetFullPath($OutputDirectory, $root)
if (-not $output.StartsWith([IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output must stay inside the repository so the exported baseline inherits Directory.Build files and native binaries.'
}
New-Item -ItemType Directory -Force $output | Out-Null
if (Get-ChildItem -LiteralPath $output) { throw 'Use an empty output directory.' }
$revision = git -C $root rev-parse --verify "$BaselineRevision^{commit}"
if ($LASTEXITCODE -ne 0) { throw 'Baseline revision is not a commit.' }

$sources = Join-Path $output 'baseline-src'
New-Item -ItemType Directory $sources | Out-Null
$paths = 'src/AgentTerm.Terminal', 'src/AgentTerm.Agents', 'src/AgentTerm.App/TerminalSurface.cs'
$archive = Join-Path $output 'baseline-src.tar'
git -C $root archive --format=tar -o $archive $revision @paths
if ($LASTEXITCODE -ne 0) { throw 'Could not export baseline sources.' }
[Formats.Tar.TarFile]::ExtractToDirectory($archive, $sources, $false)
Remove-Item $archive

$project = Join-Path $root 'tests/AgentTerm.Renderer.Perf/AgentTerm.Renderer.Perf.csproj'
$baselineSurface = Join-Path $sources 'src/AgentTerm.App/TerminalSurface.cs'
$baselineTerminal = Join-Path $sources 'src/AgentTerm.Terminal/AgentTerm.Terminal.csproj'
& dotnet build $project -c Release --artifacts-path (Join-Path $output 'baseline') `
    "-p:TerminalSurfaceSource=$baselineSurface" "-p:TerminalProject=$baselineTerminal"
if ($LASTEXITCODE -ne 0) { throw 'Baseline fixture build failed.' }
& dotnet build $project -c Release --artifacts-path (Join-Path $output 'current')
if ($LASTEXITCODE -ne 0) { throw 'Current fixture build failed.' }

$current = git -C $root rev-parse HEAD
$dirty = @(git -C $root status --porcelain -- src/AgentTerm.Terminal src/AgentTerm.Agents src/AgentTerm.App/TerminalSurface.cs tests/AgentTerm.Renderer.Perf scripts/terminal-perf-workload.py)
$hash = { param($path) (Get-FileHash $path -Algorithm SHA256).Hash }
$exe = 'bin/AgentTerm.Renderer.Perf/release_win-x64/AgentTerm.Renderer.Perf.exe'
[pscustomobject]@{
    baselineRevision = $revision
    baselineSources = $paths
    baselineRenderer = & $hash $baselineSurface
    baselineAdapter = & $hash (Join-Path $sources 'src/AgentTerm.Terminal/GhosttyTerminal.Render.cs')
    currentHead = $current
    currentWorkingTreeDirty = $dirty.Count -gt 0
    currentRenderer = & $hash (Join-Path $root 'src/AgentTerm.App/TerminalSurface.cs')
    currentAdapter = & $hash (Join-Path $root 'src/AgentTerm.Terminal/GhosttyTerminal.Render.cs')
    currentSession = & $hash (Join-Path $root 'src/AgentTerm.Terminal/TerminalSession.cs')
    nativeLibrary = & $hash $native
    baselineExe = Join-Path $output "baseline/$exe"
    currentExe = Join-Path $output "current/$exe"
} | ConvertTo-Json | Set-Content (Join-Path $output 'build-inputs.json')
Get-Content (Join-Path $output 'build-inputs.json')
