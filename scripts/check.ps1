$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'build-native.ps1')
& dotnet build (Join-Path $root 'src/AgentTerm.App/AgentTerm.App.csproj') -c Release -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "App build failed with exit code $LASTEXITCODE." }
& dotnet run --project (Join-Path $root 'tests/AgentTerm.Terminal.Smoke/AgentTerm.Terminal.Smoke.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "Smoke tests failed with exit code $LASTEXITCODE." }
& dotnet run --project (Join-Path $root 'tests/AgentTerm.ConPty.Smoke/AgentTerm.ConPty.Smoke.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "ConPTY smoke tests failed with exit code $LASTEXITCODE." }
& dotnet run --project (Join-Path $root 'tests/AgentTerm.Renderer.Smoke/AgentTerm.Renderer.Smoke.csproj') -c Release -- (Join-Path $root 'artifacts/renderer-smoke.png')
if ($LASTEXITCODE -ne 0) { throw "Renderer smoke tests failed with exit code $LASTEXITCODE." }
