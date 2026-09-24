# Launch only the specified application with a fresh, isolated profile.
param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [string]$ProfileDirectory = (Join-Path ([IO.Path]::GetTempPath()) "mighty-windows-smoke-$([Guid]::NewGuid().ToString('N'))"),
    [ValidateRange(15, 600)][int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
# CI logs need a signed-in reader, check-run annotations do not. A failed run
# says why in one annotation: the app's own smoke error and the checks that
# passed before it. Nothing else from the profile is copied.
function Write-SmokeAnnotation([string]$Message) {
    if (-not $env:GITHUB_ACTIONS) { return }
    $text = if ($Message.Length -gt 1500) { $Message.Substring(0, 1500) } else { $Message }
    $text = $text.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
    Write-Output "::error title=Windows GUI smoke::$text"
}
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
    if (-not $finished) { Write-SmokeAnnotation "GUI 검증 제한 시간 초과 (${TimeoutSeconds}초)"; throw "GUI 검증 제한 시간 초과 (${TimeoutSeconds}초)" }
    $resultPath = Join-Path $ProfileDirectory 'smoke-result.json'
    if (-not (Test-Path $resultPath -PathType Leaf)) { Write-SmokeAnnotation "GUI 결과가 없습니다. ExitCode=$($process.ExitCode)"; throw "GUI 결과가 없습니다. ExitCode=$($process.ExitCode)" }
    $result = Get-Content -Raw $resultPath | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or $result.passed -ne $true) {
        $reached = @($result.PSObject.Properties | Where-Object { $_.Value -is [bool] -and $_.Value -and $_.Name -ne 'passed' } | ForEach-Object Name) -join ', '
        $trace = @("$($result.exception)" -split "`n" | Select-Object -First 8) -join "`n"
        Write-SmokeAnnotation "ExitCode=$($process.ExitCode)`nerror: $($result.error)`ntype: $($result.exceptionType)`npassed before it: $reached`n$trace"
        throw "GUI 검증 실패: ExitCode=$($process.ExitCode), 결과: $resultPath"
    }
    $screenshot = Join-Path $ProfileDirectory 'smoke-window.png'
    if (-not (Test-Path $screenshot -PathType Leaf) -or (Get-Item $screenshot).Length -eq 0) { throw 'GUI 스크린샷이 없습니다.' }
    $scanned = $result.localeKeyLeakScanned
    if ($null -eq $scanned -or ($scanned -isnot [int] -and $scanned -isnot [long]) -or [int]$scanned -lt 50) {
        $msg = "로케일 키 누수 검사가 실행되지 않았거나 검사 수가 너무 적습니다: localeKeyLeakScanned=$scanned"
        Write-SmokeAnnotation $msg; throw $msg
    }
    $leaks = $result.localeKeyLeaks
    if ($leaks -and @($leaks).Count -gt 0) {
        $msg = "로케일 키가 화면에 그대로 노출됩니다: $($leaks -join ', ')"
        Write-SmokeAnnotation $msg; throw $msg
    }
    # Outcomes that are neither pass nor fail (a check that had to be skipped on
    # this runner) are published as a notice so they can be read without the log.
    $outcomes = @($result.PSObject.Properties | Where-Object { $_.Value -is [pscustomobject] -and $_.Value.status -is [string] } |
        ForEach-Object { "$($_.Name)=$($_.Value.status)$(if ($_.Value.reason) { " ($($_.Value.reason))" })" }) -join '; '
    if ($outcomes -and $env:GITHUB_ACTIONS) {
        $text = if ($outcomes.Length -gt 800) { $outcomes.Substring(0, 800) } else { $outcomes }
        Write-Output "::notice title=Windows GUI smoke outcomes::$($text.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A'))"
    }
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
