# Runs the Tauri app's smoke (src-tauri/src/smoke.rs) and prints its result.
#
#   .\native\tauri\scripts\smoke.ps1                         a new demo profile under %TEMP% (seed_demo), English
#   .\native\tauri\scripts\smoke.ps1 -Language ko            the same in Korean
#   .\native\tauri\scripts\smoke.ps1 -Profile <folder>       an existing profile, read-only: the result and the logs go to -Out,
#                                                            and workspace-state.json's hash and time are compared before/after
#   .\native\tauri\scripts\smoke.ps1 -NoRuns                  without the run steps (the composer, cards and Mighty steps run a
#                                                            stand-in Claude, mighty-run's mighty-fake-cli, unless this is given)
#   .\native\tauri\scripts\smoke.ps1 -RealClaude              one real request to the installed claude CLI instead of the run steps
#                                                            (a new demo profile, writes off, a temporary workspace folder)
#
# Exits with the app's code: 0 when the smoke passed.
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\target\debug\MightyCode.exe'),
    [string]$Language = 'en',
    [string]$Profile,
    [string]$Out,
    [int]$RemoteDebuggingPort = 0,
    [switch]$NoRuns,
    [switch]$RealClaude
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env.ps1')
$Exe = (Resolve-Path $Exe).Path

if (-not $Profile) {
    $Profile = Join-Path $env:TEMP ('mighty-tauri-smoke-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    Push-Location (Join-Path $PSScriptRoot '..')
    try { cargo run -q -p mighty-core --example seed_demo -- $Profile $Language; if ($LASTEXITCODE -ne 0) { throw 'seed_demo failed' } }
    finally { Pop-Location }
    if (-not $Out) { $Out = $Profile }
    $readOnly = $false
} else {
    $Profile = (Resolve-Path $Profile).Path
    if (-not $Out) { $Out = Join-Path $env:TEMP ('mighty-tauri-smoke-out-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
    $readOnly = $true
}
New-Item -ItemType Directory -Force $Out | Out-Null
$result = Join-Path $Out 'smoke-result.json'
$state = Join-Path $Profile 'workspace-state.json'

function Fingerprint {
    if (-not (Test-Path $state)) { return $null }
    $item = Get-Item $state
    [pscustomobject]@{ Hash = (Get-FileHash $state -Algorithm SHA256).Hash; Bytes = $item.Length; Modified = $item.LastWriteTimeUtc.ToString('o') }
}
function Listing { Get-ChildItem -Force $Profile | ForEach-Object { $_.Name } | Sort-Object }
# The user's settings outside any profile, which a smoke must never change (W6 plan §4.4): HKCU Software\MightyCode (shared with the
# installed C# app), the app's toast registration, Claude's settings.json and omc's and Ouroboros's configs (their times and hashes).
function UserConfig {
    $values = [ordered]@{}
    foreach ($key in 'HKCU:\Software\MightyCode', 'HKCU:\Software\Classes\AppUserModelId\dev.mightycode.tauri') {
        $item = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        $values[$key] = if ($item) { ($item.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ';' } else { $null }
    }
    foreach ($file in (Join-Path $env:USERPROFILE '.claude\settings.json'), (Join-Path $env:USERPROFILE '.config\claude-omc\config.jsonc'), (Join-Path $env:USERPROFILE '.ouroboros\config.yaml')) {
        $values[$file] = if (Test-Path $file) { $info = Get-Item $file; "$($info.LastWriteTimeUtc.ToString('o')) $((Get-FileHash $file -Algorithm SHA256).Hash)" } else { $null }
    }
    [pscustomobject]$values
}

$before = Fingerprint; $namesBefore = Listing; $userBefore = UserConfig
$arguments = @('--smoke-test', '--smoke-exit', '--profile', $Profile, '--smoke-out', $result)
if ($RealClaude) {
    $arguments += '--smoke-real-claude'
} elseif (-not $NoRuns) {
    # The run steps' stand-in Claude: mighty-run's fake CLI in its --replay-cli mode (asks a permission and a plan, then replays).
    Push-Location (Join-Path $PSScriptRoot '..')
    try { cargo build -q -p mighty-run --bin mighty-fake-cli; if ($LASTEXITCODE -ne 0) { throw 'building mighty-fake-cli failed' } }
    finally { Pop-Location }
    $arguments += @('--smoke-fake-cli', (Resolve-Path (Join-Path (Split-Path $Exe) 'mighty-fake-cli.exe')).Path)
}
if ($readOnly) { $arguments += @('--log-dir', (Join-Path $Out 'tauri-logs')) }
if ($RemoteDebuggingPort -gt 0) { $arguments += @('--remote-debugging-port', "$RemoteDebuggingPort") }
$watch = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath $Exe -ArgumentList $arguments -PassThru -Wait -WindowStyle Normal
$watch.Stop()
$after = Fingerprint; $namesAfter = Listing; $userAfter = UserConfig
$userChanged = @($userBefore.PSObject.Properties | Where-Object { $userAfter.($_.Name) -ne $_.Value } | ForEach-Object { $_.Name })
$exitCode = $process.ExitCode

Write-Output "profile:  $Profile"
Write-Output "result:   $result"
Write-Output ("exit:     {0} after {1:N1} s" -f $process.ExitCode, $watch.Elapsed.TotalSeconds)
if ($readOnly) {
    Write-Output "state before: $($before | ConvertTo-Json -Compress)"
    Write-Output "state after:  $($after | ConvertTo-Json -Compress)"
    $same = ($before.Hash -eq $after.Hash) -and ($before.Modified -eq $after.Modified)
    Write-Output "state unchanged: $same"
    $added = $namesAfter | Where-Object { $namesBefore -notcontains $_ }
    Write-Output "files added to the profile: $(if ($added) { $added -join ', ' } else { 'none' })"
    if (-not $same) { $exitCode = 1 }
}
if ($userChanged.Count -gt 0) {
    Write-Output "USER SETTINGS CHANGED (the smoke must leave them alone): $($userChanged -join ', ')"
    $exitCode = 1
} else {
    Write-Output 'user settings unchanged: HKCU Software\MightyCode, the toast registration, ~\.claude\settings.json, omc and Ouroboros configs'
}
if (Test-Path $result) { Get-Content $result -Raw -Encoding utf8 } else { Write-Output 'no smoke-result.json' }
if (-not $readOnly) {
    # The seeded profile's WebView2 data (Paths::webview_dir and browser_dir: <local data>\webview|browser\<the profile path as a
    # folder name>) goes with it.
    $name = $Profile -replace '[^A-Za-z0-9-]', '_'
    if ($name.Length -gt 80) { $name = $name.Substring($name.Length - 80) }
    foreach ($kind in 'webview', 'browser', 'image-cache', 'file-originals', 'provider-catalog') {
        Remove-Item -Recurse -Force (Join-Path $env:LOCALAPPDATA "dev.mightycode.tauri\$kind\$name") -ErrorAction SilentlyContinue
    }
}
exit $exitCode
