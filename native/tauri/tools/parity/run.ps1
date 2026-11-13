# Compares what the C# app (MightyCode.Core) and the Rust port (mighty-core) write, byte for byte, over a corpus of state files:
# the synthetic edge cases and the repository's fixtures (`parity make-corpus`), and copies (never the originals) of the real
# profile's state and backup and of release/perf and release/help-capture-final when they are present. Then the doubles, strings
# and times vectors. Exits 1 on any difference.
#
#   .\native\tauri\tools\parity\run.ps1 [-Work <folder>] [-NoReal]
#
# Needs the .NET 10 SDK (the C# tool is built from tools/parity/Parity.csproj) and the Rust toolchain (scripts/env.ps1).
param(
    [string]$Work = (Join-Path $env:TEMP 'mighty-parity'),
    [switch]$NoReal
)
$ErrorActionPreference = 'Stop'
$tauri = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$repo = (Resolve-Path (Join-Path $tauri '..\..')).Path
. (Join-Path $tauri 'scripts\env.ps1')

$csOut = Join-Path $tauri 'target\parity-cs'
Write-Host "building the C# tool -> $csOut"
dotnet build (Join-Path $PSScriptRoot 'Parity.csproj') -c Release -o $csOut --nologo -v quiet | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'the C# parity tool did not build' }
Push-Location $tauri
try {
    cargo build -p mighty-core --example parity --release --quiet
    if ($LASTEXITCODE -ne 0) { throw 'the Rust parity tool did not build' }
} finally { Pop-Location }
$rs = Join-Path $tauri 'target\release\examples\parity.exe'
$cs = Join-Path $csOut 'parity.dll'

if (Test-Path $Work) { Remove-Item -Recurse -Force $Work }
$corpus = Join-Path $Work 'corpus'
New-Item -ItemType Directory -Force $corpus | Out-Null
& $rs make-corpus $corpus --repo $repo
if ($LASTEXITCODE -ne 0) { throw 'make-corpus failed' }

# Copies of real profiles: the state and its backup each a case of their own (the files are copied first, then read).
function Add-Copy([string]$name, [string]$file) {
    if (-not (Test-Path -LiteralPath $file)) { return }
    $folder = Join-Path $corpus $name
    New-Item -ItemType Directory -Force $folder | Out-Null
    Copy-Item -LiteralPath $file (Join-Path $folder 'workspace-state.json')
    Write-Host "copied $file"
}
if (-not $NoReal) {
    $real = Join-Path $env:APPDATA 'MightyCode'
    Add-Copy 'real-state' (Join-Path $real 'workspace-state.json')
    Add-Copy 'real-backup' (Join-Path $real 'workspace-state.json.bak')
    Add-Copy 'real-state-theme' (Join-Path $real 'workspace-state.json')
    if (Test-Path (Join-Path $corpus 'real-state-theme')) { Set-Content -NoNewline -Encoding ascii (Join-Path $corpus 'real-state-theme\options.txt') '--theme light' }
    Add-Copy 'release-perf' (Join-Path $repo 'release\perf\state\workspace-state.json')
    Add-Copy 'release-help-capture-final' (Join-Path $repo 'release\help-capture-final\workspace-state.json')
}
$cases = (Get-ChildItem -Directory $corpus).Count
Write-Host "corpus: $cases cases"

$csSaved = Join-Path $Work 'cs'; $rsSaved = Join-Path $Work 'rs'
dotnet $cs save-corpus $corpus $csSaved
if ($LASTEXITCODE -ne 0) { throw 'the C# tool failed' }
& $rs save-corpus $corpus $rsSaved
if ($LASTEXITCODE -ne 0) { throw 'the Rust tool failed' }
& $rs compare-corpus $corpus $csSaved $rsSaved
$failed = $LASTEXITCODE -ne 0

$vectors = Join-Path $Work 'vectors'
& $rs make-vectors $vectors
foreach ($kind in 'doubles', 'strings', 'times') {
    $in = Join-Path $vectors "$kind.txt"
    dotnet $cs $kind $in (Join-Path $vectors "$kind.cs.txt")
    & $rs $kind $in (Join-Path $vectors "$kind.rs.txt")
    & $rs compare-lines (Join-Path $vectors "$kind.cs.txt") (Join-Path $vectors "$kind.rs.txt")
    if ($LASTEXITCODE -ne 0) { $failed = $true }
}
if ($failed) { Write-Host 'parity: DIFFERENCES' -ForegroundColor Red; exit 1 }
Write-Host 'parity: 0 differences' -ForegroundColor Green
