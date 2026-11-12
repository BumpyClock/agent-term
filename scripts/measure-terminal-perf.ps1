param(
    [Parameter(Mandatory)][string]$BaselineExe,
    [Parameter(Mandatory)][string]$FixedExe,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PythonExe = (python -c 'import sys; print(sys.executable)'),
    [ValidateRange(1, 240)][int]$Hz = 60,
    [ValidateRange(1, 60)][int]$Seconds = 12,
    [ValidateRange(1, 20)][int]$Trials = 5,
    [ValidateSet('agent', 'dense', 'cjk')][string]$Pattern = 'agent',
    [ValidateSet('timer', 'composition', 'demand')][string]$BaselineSchedule = 'timer',
    [ValidateSet('timer', 'composition', 'demand')][string]$FixedSchedule = 'demand',
    [ValidateRange(1, 1000)][int]$BaselineRefreshMs = 33,
    [ValidateRange(1, 1000)][int]$FixedRefreshMs = 33,
    [string]$PresentMonExe = '',
    [switch]$RequireFrameBudget
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $output | Out-Null
if (Get-ChildItem -LiteralPath $output) { throw 'Use an empty output directory.' }
$results = @()
foreach ($trial in 1..$Trials) {
    foreach ($side in 'baseline', 'fixed') {
        $executable = if ($side -eq 'baseline') { $BaselineExe } else { $FixedExe }
        $schedule = if ($side -eq 'baseline') { $BaselineSchedule } else { $FixedSchedule }
        $interval = if ($side -eq 'baseline') { $BaselineRefreshMs } else { $FixedRefreshMs }
        $resultPath = Join-Path $output "$side-$trial.json"
        $start = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($executable))
        $start.UseShellExecute = $false
        $start.WorkingDirectory = $root
        foreach ($value in @($PythonExe, (Join-Path $PSScriptRoot 'terminal-perf-workload.py'), $resultPath,
            '--hz', "$Hz", '--seconds', "$Seconds", '--pattern', $Pattern, '--schedule', $schedule, '--refresh-ms', "$interval")) {
            $start.ArgumentList.Add($value)
        }
        if ($PresentMonExe) {
            $start.ArgumentList.Add('--presentmon')
            $start.ArgumentList.Add($PresentMonExe)
        }
        $process = [Diagnostics.Process]::Start($start)
        try {
            if (-not $process.WaitForExit(60000)) {
                $process.Kill($true)
                throw "Fixture exceeded 60 seconds for $side trial $trial"
            }
            if ($process.ExitCode -ne 0) { throw "Fixture failed for $side trial $trial. Inspect $resultPath.error.txt" }
        } finally { $process.Dispose() }
        $result = Get-Content $resultPath -Raw | ConvertFrom-Json
        if ($result.draws -lt 10 -or $result.workload.ticks -ne ($Hz * $Seconds) -or $result.nonblankCells -lt 1000) {
            throw "Workload validity failed for $side trial $trial"
        }
        $results += [pscustomobject]@{ side = $side; trial = $trial; schedule = $schedule; result = $result }
        $frameWork = if ($result.drawWorkAvailability -eq 'not-available') { 'n/a' } else { "$($result.frameWorkDurations.p95Ms) ms" }
        Write-Output "$side $trial ($schedule). UI heartbeat p95 $($result.heartbeatIntervals.p95Ms) ms. Frame work p95 $frameWork. ReadFrame $($result.readFrames). Idle ReadFrame $($result.idle.readFrames). GC pause $($result.gcPauseMs) ms."
    }
}
$geometry = $results | ForEach-Object { "$($_.result.columns)x$($_.result.rows):$($_.result.font):$($_.result.cellWidth):$($_.result.cellHeight):$($_.result.dpi)" } | Sort-Object -Unique
$hashes = $results.result.frameHash | Sort-Object -Unique
$results | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $output 'results.json')

function Median([double[]]$values) {
    if ($values.Count -eq 0) { return $null }
    $sorted = $values | Sort-Object
    $sorted[[int][Math]::Floor(($sorted.Count - 1) / 2)]
}
$summary = foreach ($side in 'baseline', 'fixed') {
    $runs = @($results | Where-Object side -eq $side).result
    $instrumented = @($runs | Where-Object drawWorkAvailability -ne 'not-available')
    [pscustomobject]@{
        side = $side
        schedule = @($results | Where-Object side -eq $side)[0].schedule
        trials = $runs.Count
        medianHeartbeatP95Ms = Median $runs.heartbeatIntervals.p95Ms
        medianRefreshP95Ms = Median $runs.refreshDurations.p95Ms
        medianFrameWorkP95Ms = if ($instrumented.Count) { Median $instrumented.frameWorkDurations.p95Ms } else { 'n/a' }
        maxFrameWorkP95Ms = if ($instrumented.Count) { ($instrumented.frameWorkDurations.p95Ms | Measure-Object -Maximum).Maximum } else { 'n/a' }
        medianDraws = Median $runs.draws
        medianReadFrames = Median $runs.readFrames
        medianIdleReadFrames = Median $runs.idle.readFrames
        medianCpuSeconds = Median $runs.cpuSeconds
        medianGcPauseMs = Median $runs.gcPauseMs
    }
}
[pscustomobject]@{
    hz = $Hz; seconds = $Seconds; pattern = $Pattern; geometry = $geometry; finalFrameHashes = $hashes
    baselineRefreshMs = $BaselineRefreshMs; fixedRefreshMs = $FixedRefreshMs; sides = $summary
} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output 'summary.json')
$summary | Format-Table -AutoSize | Out-String | Write-Output
if (@($geometry).Count -ne 1 -or @($hashes).Count -ne 1) { throw 'Geometry or final raster differs. Do not report a matched speedup.' }
if ($RequireFrameBudget) {
    $invalid = $results | Where-Object { $_.side -eq 'fixed' -and
        ($_.result.drawWorkAvailability -eq 'not-available' -or $_.result.frameWorkDurations.p95Ms -ge 16.6) }
    if ($invalid) { throw 'Fixed renderer did not satisfy the p95 16.6 ms UI-work budget in every trial. Results saved.' }
}
