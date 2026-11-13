param(
    [ValidateRange(1, 20)][int]$Trials = 1,
    [string]$OutputDirectory = ('artifacts/startup-check-' + [Guid]::NewGuid().ToString('N'))
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory, $root)
New-Item -ItemType Directory -Force $output | Out-Null
if (Get-ChildItem -LiteralPath $output) { throw 'Use an empty output directory.' }
& dotnet build (Join-Path $root 'src/AgentTerm.App/AgentTerm.App.csproj') -c Release `
    -p:Platform=x64 -p:StartupSmoke=true --artifacts-path (Join-Path $output 'build')
if ($LASTEXITCODE -ne 0) { throw 'Startup smoke build failed.' }
$executable = Join-Path $output 'build/bin/AgentTerm.App/release_win-x64/AgentTerm.App.exe'
foreach ($trial in 1..$Trials) {
    foreach ($position in 'Left', 'Top') {
        $report = Join-Path $output "$position-$trial.txt"
        $start = [Diagnostics.ProcessStartInfo]::new($executable)
        $start.UseShellExecute = $false
        $start.WorkingDirectory = $root
        $start.ArgumentList.Add($report)
        $start.ArgumentList.Add($position)
        $process = [Diagnostics.Process]::Start($start)
        try {
            if (-not $process.WaitForExit(120000)) {
                $process.Kill($true)
                throw "Startup smoke exceeded two minutes for $position trial $trial."
            }
            if ($process.ExitCode -ne 0) { throw "Startup smoke failed. Inspect $report." }
        } finally { $process.Dispose() }
        Get-Content -LiteralPath $report
    }
}
