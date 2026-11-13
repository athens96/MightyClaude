# The write check on a real profile, on a COPY of it (the original is only read): the Tauri app started on the copy with
# --allow-state-write changes the theme through the page and quits; the C# tool saves the same change of the same file
# (StateStore.LoadAsync, UpdateAsync's normalize, SaveAsync); the two workspace-state.json files must be the same bytes.
#
#   .\native\tauri\tools\parity\run.ps1 builds the C# tool first; then
#   .\native\tauri\tools\parity\real-write.ps1 [-Source <profile folder>] [-Exe <MightyCode.exe>]
param(
    [string]$Source = (Join-Path $env:APPDATA 'MightyCode'),
    [string]$Exe = (Join-Path $PSScriptRoot '..\..\target\debug\MightyCode.exe'),
    [int]$Port = 9333
)
$ErrorActionPreference = 'Stop'
$tauri = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$tool = Join-Path $tauri 'target\parity-cs\parity.dll'
if (-not (Test-Path $tool)) { throw "build the C# tool first (run.ps1): $tool" }
$Exe = (Resolve-Path $Exe).Path

$work = Join-Path $env:TEMP ('mighty-real-write-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$profile = Join-Path $work 'profile'
New-Item -ItemType Directory -Force $profile | Out-Null
$sourceHash = (Get-FileHash (Join-Path $Source 'workspace-state.json') -Algorithm SHA256).Hash
foreach ($name in 'workspace-state.json', 'workspace-state.json.bak') {
    $file = Join-Path $Source $name
    if (Test-Path $file) { Copy-Item $file (Join-Path $profile $name) }
}
$input = Join-Path $work 'input.json'
Copy-Item (Join-Path $profile 'workspace-state.json') $input
$current = (Get-Content $input -Raw | ConvertFrom-Json).theme
$theme = if ($current -eq 'light') { 'dark' } else { 'light' }
Write-Output "copy: $profile (theme $current -> $theme)"

# C#: the same file, the same change.
$cs = Join-Path $work 'cs'
dotnet $tool save $input $cs --theme $theme --lang en
Get-Content (Join-Path $cs 'report.json')

# Rust: the app on the copy, writing on.
$logs = Join-Path $work 'logs'
$app = Start-Process -FilePath $Exe -ArgumentList @('--profile', $profile, '--allow-state-write', '--log-dir', $logs, '--remote-debugging-port', "$Port") -PassThru
node (Join-Path $PSScriptRoot 'theme.mjs') $Port $theme
$changed = $LASTEXITCODE -eq 0
Start-Sleep -Milliseconds 500
$null = $app.CloseMainWindow()
if (-not $app.WaitForExit(30000)) { $app.Kill(); throw 'the app did not quit' }
Write-Output "app exit: $($app.ExitCode); theme changed in the page: $changed"

$saved = Join-Path $profile 'workspace-state.json'
$expected = Join-Path $cs 'saved.json'
$same = (Get-FileHash $saved -Algorithm SHA256).Hash -eq (Get-FileHash $expected -Algorithm SHA256).Hash
Write-Output ("Rust saved {0} bytes, C# saved {1} bytes: {2}" -f (Get-Item $saved).Length, (Get-Item $expected).Length, $(if ($same) { 'the same bytes' } else { 'DIFFERENT' }))
Write-Output "files in the copy: $((Get-ChildItem $profile | ForEach-Object Name) -join ', ')"
Write-Output "original untouched: $((Get-FileHash (Join-Path $Source 'workspace-state.json') -Algorithm SHA256).Hash -eq $sourceHash -or 'changed by the running C# app, not by this check')"
Get-Content (Join-Path $logs 'host.log') | Select-Object -First 3
# The copy's WebView2 data (Paths::webview_dir and browser_dir) goes with it.
$name = $profile -replace '[^A-Za-z0-9-]', '_'
if ($name.Length -gt 80) { $name = $name.Substring($name.Length - 80) }
foreach ($kind in 'webview', 'browser') { Remove-Item -Recurse -Force (Join-Path $env:LOCALAPPDATA "dev.mightycode.tauri\$kind\$name") -ErrorAction SilentlyContinue }
if (-not ($same -and $changed)) { exit 1 }
