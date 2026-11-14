# The native forms against C# byte for byte (plan W6 §1.8): C#'s web smoke captured over WebView2's DevTools protocol
# (native-capture.mjs), the Tauri smoke's own pushes (--native-dump), and `native-diff` over both.
#
#   .\native\tauri\tools\parity\native.ps1                         C#'s app from native\tauri\target\cs-publish (build it with
#                                                                  dotnet publish ... --output native\tauri\target\cs-publish)
#   .\native\tauri\tools\parity\native.ps1 -CsExe <MightyCode.exe> another C# build
#   .\native\tauri\tools\parity\native.ps1 -CsCapture <cs.jsonl>   an earlier capture instead of running C# again
#
# C# runs with MIGHTY_SMOKE_WEB=1 in an empty temporary profile, as scripts\test-native-windows.ps1 -Web runs it. Its smoke is stopped
# once its automatic-updates step reported, before its live steps reach the completion notification (which registers the build with
# Windows' notifications in HKCU and shows a toast): every native form W6 draws is pushed before then. The Tauri smoke runs in a
# new demo profile (smoke.ps1 -NoRuns). HKCU Software\MightyCode, the notification registrations and %APPDATA%\MightyCode are
# compared before and after (the profile's logs left out: a running installed app appends to them); any change fails the run.
#
# Writes cs.jsonl, rs.jsonl and diff.txt to -Out and exits with native-diff's code (0 when every view is the same).
param(
    [string]$CsExe = (Join-Path $PSScriptRoot '..\..\target\cs-publish\MightyCode.exe'),
    [string]$CsCapture,
    [string]$Exe = (Join-Path $PSScriptRoot '..\..\target\debug\MightyCode.exe'),
    [int]$Port = 9334,
    [string]$Language = 'ko',
    [string]$Out,
    [int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
$tauri = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $tauri 'scripts\env.ps1')
if (-not $Out) { $Out = Join-Path $env:TEMP ('mighty-native-parity-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$cs = Join-Path $Out 'cs.jsonl'
$rs = Join-Path $Out 'rs.jsonl'

# What neither app may change: HKCU Software\MightyCode's values, the notification registrations' names, the C# profile's files.
function UserState {
    $values = [ordered]@{}
    foreach ($root in 'HKCU:\Software\MightyCode', 'HKCU:\Software\Classes\AppUserModelId') {
        if (-not (Test-Path $root)) { $values[$root] = $null; continue }
        foreach ($key in @(Get-Item -Path $root) + @(Get-ChildItem -Path $root -Recurse -ErrorAction SilentlyContinue)) {
            $values["$($key.Name)\"] = ''
            foreach ($name in $key.GetValueNames()) { $values["$($key.Name)\$name"] = "$($key.GetValue($name, $null, 'DoNotExpandEnvironmentNames'))" }
        }
    }
    $profile = Join-Path $env:APPDATA 'MightyCode'
    if (Test-Path $profile) {
        # Its logs left out: an installed Mighty Code that is running (this very session's host, perhaps) appends to them.
        foreach ($file in Get-ChildItem -LiteralPath $profile -Recurse -Force -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -ne '.log' }) {
            $values[$file.FullName] = "$($file.Length) $($file.LastWriteTimeUtc.ToString('o'))"
        }
    }
    $values
}
$before = UserState

$masks = @()
if ($CsCapture) {
    Copy-Item -LiteralPath $CsCapture -Destination $cs -Force
} else {
    $CsExe = (Resolve-Path $CsExe).Path
    $csProfile = Join-Path $Out 'cs-profile'
    $masks += $csProfile
    $capture = Start-Process -FilePath node -ArgumentList @((Join-Path $PSScriptRoot 'native-capture.mjs'), "$Port", "`"$cs`"", "$($TimeoutSeconds + 60)") `
        -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $Out 'capture.log') -RedirectStandardError (Join-Path $Out 'capture.err')
    $held = @{ smoke = $env:MIGHTY_SMOKE_WEB; args = $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS }
    $env:MIGHTY_SMOKE_WEB = '1'
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$Port"
    try {
        $app = Start-Process -FilePath $CsExe -ArgumentList @('--smoke-test', '--smoke-exit', '--profile', "`"$csProfile`"") -WorkingDirectory (Split-Path $CsExe) -PassThru
    } finally {
        $env:MIGHTY_SMOKE_WEB = $held.smoke
        $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = $held.args
    }
    $trace = Join-Path $csProfile 'startup.log'
    $end = (Get-Date).AddSeconds($TimeoutSeconds)
    $stopped = 'timed out'
    while ((Get-Date) -lt $end) {
        if ($app.HasExited) { $stopped = "exited ($($app.ExitCode))"; break }
        if ((Test-Path $trace) -and (Select-String -LiteralPath $trace -Pattern 'smoke:automaticUpdates:(passed|failed)' -Quiet)) {
            # A moment for the capture to drain the last pushes.
            Start-Sleep -Milliseconds 800
            $stopped = 'stopped after automaticUpdates'
            break
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue; $app.WaitForExit(10000) | Out-Null }
    if (-not $capture.WaitForExit(15000)) { Stop-Process -Id $capture.Id -Force -ErrorAction SilentlyContinue }
    Write-Output "C#:       $stopped; $(Get-Content (Join-Path $Out 'capture.log') -Raw -ErrorAction SilentlyContinue)"
    $errors = Get-Content (Join-Path $Out 'capture.err') -Raw -ErrorAction SilentlyContinue
    if ($errors) { Write-Output "capture:  $errors" }
}

$smoke = & (Join-Path $tauri 'scripts\smoke.ps1') -Exe $Exe -Language $Language -NoRuns -NativeDump $rs
$smokeExit = $LASTEXITCODE
$rsProfile = ($smoke | Where-Object { $_ -like 'profile:*' } | Select-Object -First 1) -replace '^profile:\s*', ''
if ($rsProfile) { $masks += $rsProfile }
Write-Output "Tauri:    exit $smokeExit, profile $rsProfile"

$after = UserState
$changed = @(@($before.Keys) + @($after.Keys) | Sort-Object -Unique | Where-Object { $before[$_] -ne $after[$_] })

$maskArgs = @()
foreach ($mask in $masks) { $maskArgs += @('--mask', $mask) }
Push-Location $tauri
try {
    $diff = cargo run -q -p mighty-native --example native-diff -- $cs $rs @maskArgs 2>&1 | ForEach-Object { "$_" }
    $diffExit = $LASTEXITCODE
} finally { Pop-Location }
$diff | Set-Content -LiteralPath (Join-Path $Out 'diff.txt') -Encoding utf8
$diff
Write-Output "out:      $Out"
if ($changed.Count -gt 0) {
    Write-Output 'USER STATE CHANGED:'
    foreach ($name in $changed) { Write-Output "  $name`: $($before[$name]) -> $($after[$name])" }
    exit 1
}
Write-Output 'user state unchanged: HKCU Software\MightyCode, the notification registrations, %APPDATA%\MightyCode'
exit $diffExit
