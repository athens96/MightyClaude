# Launch only the specified application with a fresh, isolated profile.
param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [string]$ProfileDirectory = (Join-Path ([IO.Path]::GetTempPath()) "mighty-windows-smoke-$([Guid]::NewGuid().ToString('N'))"),
    [ValidateRange(15, 600)][int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'WinUI GUI 검증에는 Windows와 PowerShell 7이 필요합니다.' }
$Executable = [IO.Path]::GetFullPath($Executable)
$ProfileDirectory = [IO.Path]::GetFullPath($ProfileDirectory)
if (-not (Test-Path $Executable -PathType Leaf)) { throw "앱이 없습니다: $Executable" }
if ((Test-Path $ProfileDirectory) -and (-not (Test-Path $ProfileDirectory -PathType Container) -or (Get-ChildItem -Force $ProfileDirectory | Select-Object -First 1))) {
    throw '스모크 프로필은 새 폴더이거나 비어 있어야 합니다.'
}
foreach ($library in @('vcruntime140.dll', 'msvcp140.dll')) {
    $localPath = Join-Path (Split-Path -Parent $Executable) $library
    $systemPath = Join-Path ([Environment]::SystemDirectory) $library
    if (-not (Test-Path $localPath -PathType Leaf) -and -not (Test-Path $systemPath -PathType Leaf)) {
        throw "Visual C++ v14 Redistributable이 필요합니다: $library (https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist)"
    }
}
$start = [Diagnostics.ProcessStartInfo]::new($Executable)
$start.WorkingDirectory = Split-Path -Parent $Executable
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in @('--smoke-test', '--smoke-exit', '--profile', $ProfileDirectory)) { $start.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
$started = $false
$launchTime = Get-Date
try {
    if (-not $process.Start()) { throw '앱 프로세스를 시작하지 못했습니다.' }
    $started = $true
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $finished = $process.WaitForExit($TimeoutSeconds * 1000)
    if (-not $finished) { $process.Kill($true); $process.WaitForExit() }
    [IO.Directory]::CreateDirectory($ProfileDirectory) | Out-Null
    [IO.File]::WriteAllText((Join-Path $ProfileDirectory 'stdout.log'), $stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $ProfileDirectory 'stderr.log'), $stderr.GetAwaiter().GetResult())
    if (-not $finished) { throw "GUI 검증 제한 시간 초과 (${TimeoutSeconds}초)" }
    $resultPath = Join-Path $ProfileDirectory 'smoke-result.json'
    if (-not (Test-Path $resultPath -PathType Leaf)) { throw "GUI 결과가 없습니다. ExitCode=$($process.ExitCode)" }
    $result = Get-Content -Raw $resultPath | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or $result.passed -ne $true) { throw "GUI 검증 실패: ExitCode=$($process.ExitCode), 결과: $resultPath" }
    $screenshot = Join-Path $ProfileDirectory 'smoke-window.png'
    if (-not (Test-Path $screenshot -PathType Leaf) -or (Get-Item $screenshot).Length -eq 0) { throw 'GUI 스크린샷이 없습니다.' }
    Write-Output "Windows GUI PASS: $resultPath"
} finally {
    if ($started -and $process.HasExited -and $process.ExitCode -ne 0) {
        # Native WinUI fail-fast can bypass managed exception handlers. Read only
        # Application events naming this app from this launch, never unrelated logs.
        [IO.Directory]::CreateDirectory($ProfileDirectory) | Out-Null
        try {
            Start-Sleep -Seconds 2
            $appName = [IO.Path]::GetFileNameWithoutExtension($Executable)
            $nativeEvents = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $launchTime; Id = @(1000, 1001, 1026) } -ErrorAction SilentlyContinue |
                Where-Object { $_.Message -and $_.Message.Contains($appName, [StringComparison]::OrdinalIgnoreCase) } |
                Select-Object -First 12 TimeCreated, Id, ProviderName, Message)
            ConvertTo-Json -InputObject $nativeEvents -Depth 4 | Set-Content (Join-Path $ProfileDirectory 'native-crash-events.json') -Encoding utf8
        } catch {
            $_.Exception.Message | Set-Content (Join-Path $ProfileDirectory 'native-crash-events-error.txt') -Encoding utf8
        }
        Get-ChildItem -File -Recurse (Split-Path -Parent $Executable) |
            Select-Object @{Name='Path';Expression={[IO.Path]::GetRelativePath((Split-Path -Parent $Executable), $_.FullName)}}, Length |
            ConvertTo-Json -Depth 3 | Set-Content (Join-Path $ProfileDirectory 'published-files.json') -Encoding utf8
        @{ processId = $process.Id; exitCode = $process.ExitCode; launchedAt = $launchTime.ToUniversalTime().ToString('O') } |
            ConvertTo-Json | Set-Content (Join-Path $ProfileDirectory 'native-process.json') -Encoding utf8
    }
    if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    $process.Dispose()
}
