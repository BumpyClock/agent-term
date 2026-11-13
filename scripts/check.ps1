$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'build-native.ps1')
& dotnet build (Join-Path $root 'src/AgentTerm.App/AgentTerm.App.csproj') -c Release -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "App build failed with exit code $LASTEXITCODE." }
foreach ($mode in 'enabled', 'disabled', 'storage-failure') {
    & dotnet run --project (Join-Path $root 'tests/AgentTerm.PerformanceCapture.Smoke/AgentTerm.PerformanceCapture.Smoke.csproj') -c Release -- $mode
    if ($LASTEXITCODE -ne 0) { throw "Performance capture checks failed for $mode." }
}
& dotnet run --project (Join-Path $root 'tests/AgentTerm.Icons.Smoke/AgentTerm.Icons.Smoke.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "Icon smoke tests failed with exit code $LASTEXITCODE." }
& dotnet run --project (Join-Path $root 'tests/AgentTerm.AgentUi.Smoke/AgentTerm.AgentUi.Smoke.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "Native agent UI checks failed with exit code $LASTEXITCODE." }
& dotnet run --project (Join-Path $root 'tests/AgentTerm.Terminal.Smoke/AgentTerm.Terminal.Smoke.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "Smoke tests failed with exit code $LASTEXITCODE." }
& dotnet run --project (Join-Path $root 'tests/AgentTerm.ConPty.Smoke/AgentTerm.ConPty.Smoke.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "ConPTY smoke tests failed with exit code $LASTEXITCODE." }
& dotnet run --project (Join-Path $root 'tests/AgentTerm.Renderer.Smoke/AgentTerm.Renderer.Smoke.csproj') -c Release -- (Join-Path $root 'artifacts/renderer-smoke.png')
if ($LASTEXITCODE -ne 0) { throw "Renderer smoke tests failed with exit code $LASTEXITCODE." }

$cacheChurn = Join-Path $root ('artifacts/glyph-cache-churn-' + [Guid]::NewGuid().ToString('N') + '.json')
& dotnet run --project (Join-Path $root 'tests/AgentTerm.Renderer.Perf/AgentTerm.Renderer.Perf.csproj') -c Release -- unused unused $cacheChurn --cache-churn-only
if ($LASTEXITCODE -ne 0) { throw "Glyph cache checks failed with exit code $LASTEXITCODE." }
$transitions = Join-Path $root ('artifacts/retained-transitions-' + [Guid]::NewGuid().ToString('N') + '.json')
& dotnet run --project (Join-Path $root 'tests/AgentTerm.Renderer.Perf/AgentTerm.Renderer.Perf.csproj') -c Release -- unused (Join-Path $root 'scripts/terminal-perf-workload.py') $transitions --verify-transitions --windowed
if ($LASTEXITCODE -ne 0) { throw "Retained raster transition checks failed with exit code $LASTEXITCODE." }
& (Join-Path $PSScriptRoot 'check-startup.ps1')

& dotnet run --project (Join-Path $root 'tests/AgentTerm.Agents.Smoke/AgentTerm.Agents.Smoke.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "Agent behavior checks failed with exit code $LASTEXITCODE." }

& dotnet run --project (Join-Path $root 'tests/AgentTerm.Copilot.Smoke/AgentTerm.Copilot.Smoke.csproj') -c Release -- --normalization-only
if ($LASTEXITCODE -ne 0) { throw "Copilot normalization checks failed with exit code $LASTEXITCODE." }
