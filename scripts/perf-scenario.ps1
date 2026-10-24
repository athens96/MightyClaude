# Runs the Windows app's timing scenario (MainWindow.PerfScenario.cs) in fresh profiles and keeps each run's
# perf-result.json and perf.log under release/perf/<timestamp>-<page|winui-web|winui-richedit>-<n>/. Windows PowerShell 5.1.
# By default the run measures the web shell (the page drives every op; each phase adds pageLongTasks). -WinUI measures the
# old WinUI screens (the profile gets ui-winui), and -Web with it gives those screens the web transcript.
# The app opens its window for the run: leave the PC alone while it runs, as input and other programs change the numbers.
param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [string]$State,
    [switch]$WinUI,
    [switch]$Web,
    [int]$Runs = 1
)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path $Executable).Path
if ($State) {
    $State = (Resolve-Path $State).Path
    if (-not (Test-Path (Join-Path $State 'workspace-state.json'))) { throw "No workspace-state.json in $State" }
}
$repo = Split-Path -Parent $PSScriptRoot
if ($Web -and -not $WinUI) { throw '-Web picks the WinUI screens'' transcript: use it with -WinUI.' }
$mode = if (-not $WinUI) { 'page' } elseif ($Web) { 'winui-web' } else { 'winui-richedit' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$rows = @()
for ($n = 1; $n -le $Runs; $n++) {
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("mighty-perf-" + [guid]::NewGuid().ToString('N'))
    # The app wants an empty profile; it creates the folder itself.
    $env:MIGHTY_PERF_STATE = if ($State) { $State } else { '' }
    $env:MIGHTY_PERF_WINUI = if ($WinUI) { '1' } else { '' }
    $env:MIGHTY_PERF_WEB = if ($Web) { '1' } else { '' }
    Write-Host "Run $n/$Runs ($mode): $dir"
    $process = Start-Process -FilePath $Executable -ArgumentList @('--smoke-test', '--perf-scenario', '--smoke-exit', '--profile', "`"$dir`"") -PassThru
    if (-not $process.WaitForExit(1800000)) { $process.Kill(); Write-Warning "Run $n did not end within 30 minutes and was stopped." }
    $out = Join-Path $repo ("release\perf\{0}-{1}-{2}" -f $stamp, $mode, $n)
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    foreach ($name in @('perf-result.json', 'perf.log', 'smoke-result.json', 'startup.log')) {
        $file = Join-Path $dir $name
        if (Test-Path $file) { Copy-Item $file $out }
    }
    $resultFile = Join-Path $out 'perf-result.json'
    if (-not (Test-Path $resultFile)) { Write-Warning "Run $n wrote no perf-result.json (see $out)."; continue }
    $result = Get-Content -Raw -Encoding UTF8 $resultFile | ConvertFrom-Json
    if (-not $result.completed) { Write-Warning "Run $n did not finish every phase (see $out)." }
    foreach ($phase in $result.phases.PSObject.Properties) {
        $rows += [pscustomobject]@{
            Run         = $n
            Phase       = $phase.Name
            OpMedian    = $phase.Value.opMedian
            OpP95       = $phase.Value.opP95
            OpMax       = $phase.Value.opMax
            StallSum    = $phase.Value.stall.lateBeyond33SumMs
            'Stalls>250' = $phase.Value.stall.over250
            Capped      = $phase.Value.capped
            Renders     = $phase.Value.fullRenders
            InPlace     = [int]$phase.Value.tabSwitchesInPlace + [int]$phase.Value.paneAddsInPlace
            LongTasks   = if ($phase.Value.pageLongTasks) { $phase.Value.pageLongTasks.count } else { '' }
            LongTaskMax = if ($phase.Value.pageLongTasks) { $phase.Value.pageLongTasks.maxMs } else { '' }
        }
    }
    Write-Host ("  {0} panes, {1} log entries, {2} graph runs, UI {3}, transcript {4} -> {5}" -f $result.paneCount, $result.totalLogEntries, $result.graphRuns, $result.uiMode, $result.transcriptMode, $out)
}
Remove-Item Env:MIGHTY_PERF_STATE -ErrorAction SilentlyContinue
Remove-Item Env:MIGHTY_PERF_WEB -ErrorAction SilentlyContinue
Remove-Item Env:MIGHTY_PERF_WINUI -ErrorAction SilentlyContinue
$rows | Format-Table -AutoSize
