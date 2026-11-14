# Runs the Tauri app's smoke (src-tauri/src/smoke.rs) and prints its result.
#
#   .\native\tauri\scripts\smoke.ps1                         a new demo profile under %TEMP% (seed_demo), English
#   .\native\tauri\scripts\smoke.ps1 -Language ko            the same in Korean
#   .\native\tauri\scripts\smoke.ps1 -Profile <folder>       an existing profile, read-only: the result and the logs go to -Out,
#                                                            and every file of the profile (but -Out and tauri-logs) is compared
#                                                            before/after: any file added, removed or changed fails the run
#   .\native\tauri\scripts\smoke.ps1 -NoRuns                  without the run steps (the composer, cards and Mighty steps run a
#                                                            stand-in Claude, mighty-run's mighty-fake-cli, unless this is given)
#   .\native\tauri\scripts\smoke.ps1 -RealClaude              one real request to the installed claude CLI instead of the run steps
#                                                            (a new demo profile, writes off, a temporary workspace folder)
#   .\native\tauri\scripts\smoke.ps1 -NativeDump <file>       every native and text push also written to <file> (--native-dump; the
#                                                            parity capture, tools\parity\native.ps1)
#
# Exits with the app's code: 0 when the smoke passed.
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\target\debug\MightyCode.exe'),
    [string]$Language = 'en',
    [string]$Profile,
    [string]$Out,
    [int]$RemoteDebuggingPort = 0,
    [string]$NativeDump,
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

# One probe of what must not change, its failure written as its value (and so a difference) rather than stopping the script.
function Probe([scriptblock]$Read) {
    try { & $Read } catch { "probe failed: $($_.Exception.Message)" }
}
# Every file of the profile as "relative path" = "bytes time hash", leaving out -Out (when it is inside) and the logs.
function ProfileTree {
    $values = [ordered]@{}
    $root = $Profile.TrimEnd('\')
    $outFull = [IO.Path]::GetFullPath($Out).TrimEnd('\') + '\'
    $files = Get-ChildItem -LiteralPath $root -Recurse -Force -File -ErrorAction SilentlyContinue | Sort-Object FullName
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($root.Length + 1)
        if ($file.FullName.StartsWith($outFull, [StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($relative -like 'tauri-logs\*') { continue }
        $values[$relative] = Probe { "$($file.Length) $($file.LastWriteTimeUtc.ToString('o')) $((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)" }
    }
    $values
}
# The values under a registry key and all its subkeys, as "key\name" = "value" (a key without values as "key\" = '').
function RegistryTree([string]$Root, $Into) {
    if (-not (Test-Path $Root)) { $Into[$Root] = $null; return }
    foreach ($key in @(Get-Item -Path $Root) + @(Get-ChildItem -Path $Root -Recurse -ErrorAction Stop)) {
        foreach ($name in $key.GetValueNames()) {
            $Into["$($key.Name)\$name"] = Probe { "$($key.GetValue($name, $null, 'DoNotExpandEnvironmentNames'))" }
        }
        if ($key.ValueCount -eq 0) { $Into["$($key.Name)\"] = '' }
    }
}
# The user's settings outside any profile, which a smoke must never change (W6 plan §4.4): HKCU Software\MightyCode (shared with the
# installed C# app) and its subkeys, the app's toast registration, Claude's settings.json and omc's and Ouroboros's configs (their times
# and hashes).
function UserConfig {
    $values = [ordered]@{}
    foreach ($key in 'HKCU:\Software\MightyCode', 'HKCU:\Software\Classes\AppUserModelId\dev.mightycode.tauri') {
        try { RegistryTree $key $values } catch { $values[$key] = "probe failed: $($_.Exception.Message)" }
    }
    foreach ($file in (Join-Path $env:USERPROFILE '.claude\settings.json'), (Join-Path $env:USERPROFILE '.config\claude-omc\config.jsonc'), (Join-Path $env:USERPROFILE '.ouroboros\config.yaml')) {
        $values[$file] = Probe { if (Test-Path $file) { $info = Get-Item $file; "$($info.LastWriteTimeUtc.ToString('o')) $((Get-FileHash $file -Algorithm SHA256).Hash)" } else { $null } }
    }
    $values
}
# The names whose value differs between two probes (added and removed ones included).
function Changed($Before, $After) {
    @(@($Before.Keys) + @($After.Keys) | Sort-Object -Unique | Where-Object { $Before[$_] -ne $After[$_] })
}

$treeBefore = if ($readOnly) { ProfileTree } else { $null }; $userBefore = UserConfig
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
if ($NativeDump) { $arguments += @('--native-dump', [IO.Path]::GetFullPath($NativeDump)) }
$watch = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath $Exe -ArgumentList $arguments -PassThru -Wait -WindowStyle Normal
$watch.Stop()
$treeAfter = if ($readOnly) { ProfileTree } else { $null }; $userAfter = UserConfig
$userChanged = Changed $userBefore $userAfter
$exitCode = $process.ExitCode

Write-Output "profile:  $Profile"
Write-Output "result:   $result"
Write-Output ("exit:     {0} after {1:N1} s" -f $process.ExitCode, $watch.Elapsed.TotalSeconds)
if ($readOnly) {
    $profileChanged = Changed $treeBefore $treeAfter
    if ($profileChanged.Count -gt 0) {
        Write-Output "PROFILE CHANGED (a read-only run must leave every file alone; another writer, such as an app open on this profile, may be the cause):"
        foreach ($name in $profileChanged) { Write-Output "  $name`: $($treeBefore[$name]) -> $($treeAfter[$name])" }
        $exitCode = 1
    } else {
        Write-Output "profile unchanged: $($treeBefore.Count) files"
    }
}
if ($userChanged.Count -gt 0) {
    Write-Output "USER SETTINGS CHANGED (the smoke must leave them alone; another writer, such as the installed app or a Claude session, may be the cause):"
    foreach ($name in $userChanged) { Write-Output "  $name`: $($userBefore[$name]) -> $($userAfter[$name])" }
    $exitCode = 1
} else {
    Write-Output 'user settings unchanged: HKCU Software\MightyCode and its subkeys, the toast registration, ~\.claude\settings.json, omc and Ouroboros configs'
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
