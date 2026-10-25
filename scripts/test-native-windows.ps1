# Launch only the specified application with a fresh, isolated profile.
param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [string]$ProfileDirectory = (Join-Path ([IO.Path]::GetTempPath()) "mighty-windows-smoke-$([Guid]::NewGuid().ToString('N'))"),
    [ValidateRange(15, 600)][int]$TimeoutSeconds = 300,
    # The web shell's smoke (MIGHTY_SMOKE_WEB=1, MainWindow.WebShell.cs, the full check) instead of the WinUI fallback's
    # minimal smoke (MainWindow.Smoke.cs: starts, shows a Claude and a shell pane, opens and closes Settings).
    [switch]$Web
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
# The app writes ui-winui into the smoke profile unless MIGHTY_SMOKE_WEB=1 (Program.cs PrepareWebShellProfile).
if ($Web) { $start.Environment['MIGHTY_SMOKE_WEB'] = '1' } else { [void]$start.Environment.Remove('MIGHTY_SMOKE_WEB') }
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
        Write-SmokeAnnotation "ExitCode=$($process.ExitCode)`nphase: $($result.phase) [$($result.stepState)]`nerror: $($result.error)`ntype: $($result.exceptionType)`npassed before it: $reached`n$trace"
        throw "GUI 검증 실패: ExitCode=$($process.ExitCode), 결과: $resultPath"
    }
    function Fail([string]$Message) { Write-SmokeAnnotation $Message; throw $Message }
    function Require-Keys($Value, [string]$Name, [string[]]$Keys) {
        foreach ($key in $Keys) { if ($Value.$key -ne $true) { Fail "Windows smoke failed: $Name.$key" } }
    }
    if ($Web) {
        if ($result.webShell -ne $true) { Write-SmokeAnnotation 'The web smoke ran without the web shell (no WebView2 runtime?)'; throw "웹 셸 검증이 웹 셸 없이 실행됐습니다: $resultPath" }
        # The page's browser pane goes back through its own button and refuses window.open() (MainWindow.WebShell.Panes.cs).
        Require-Keys $result.panes 'panes' @('browserNavigated', 'browserBack', 'browserPopupBlocked')
        # The live steps beside the page (MainWindow.WebShell.LiveSmoke.cs): every one ran and passed.
        foreach ($key in @('settingsSections', 'phaseModelsSection', 'phaseModelsActions', 'componentsSection', 'setupTools', 'mobileDirect', 'accountUsage', 'appUpdateSection', 'claudePluginList', 'codexPluginList', 'pluginMarketplace', 'automaticUpdates', 'remoteWorkspaces', 'companion', 'screenCapture', 'screenTransport', 'screenTapMarkerNoFocus', 'completionNotification', 'pictureDecode', 'delegation', 'localeKeyLeakScan')) {
            if ($result.live.$key -ne $true) { Fail "Windows web smoke live step failed: $key $($result."${key}Error")" }
        }
        Require-Keys $result.pictureDecode 'pictureDecode' @('exifOrientation', 'tiffRaster')
        Require-Keys $result.automaticUpdates 'automaticUpdates' @('defaultAndPersistedPreference', 'sameProviderLocalAndMobileHeld', 'releasedQueuePreservesOrder', 'stopCancelsHeldRequests', 'manualUpdateRefusesWithoutConsumingDrafts', 'manualPluginMutationBlocksBothProviders')
        Require-Keys $result.companion 'companion' @('bundledAtlasDecoded', 'allAnimationRowsRendered', 'nonActivatingWindow', 'pointerActionBoundToCard', 'selectedPetPreview', 'contextMenuNonActivating', 'contentDrivenHeightAndEdgeResize', 'planBubbleAndWindow', 'renderedOverlaySnapshots')
        Require-Keys $result.screenCapture 'screenCapture' @('syntheticGpuCropEncoded', 'displayBoundsEnumerated')
        if ($result.screenTapMarkerNoFocus -ne $true) { Fail 'Windows screen tap marker stole focus or intercepted input.' }
        Require-Keys $result.screenTransport 'screenTransport' @('packagedWebViewBridge', 'h264Negotiated', 'screenAndOverviewTracksDecoded', 'perPeerResolutionLimit', 'unicodeControlRoundTrip', 'hostDataRoundTrip', 'haltClosedChannel')
        # 필요한 도구 over fixed probes (MainWindow.SetupSmoke.cs), opened in the page: rows, picks, plan order, the confirmation read with no terminal opened.
        foreach ($key in @('firstRunDecision', 'welcomeButton', 'rows', 'actions', 'cancelledWithoutTerminal', 'blockedPaneInstall', 'passed')) {
            if ($result.setupTools.$key -ne $true) { Fail "Windows setup screen smoke failed: setupTools.$key" }
        }
        # Remote workspaces' settings half (MainWindow.RemoteWorkspacesSmoke.cs): a host and a client in this one process over loopback.
        Require-Keys $result.remoteWorkspaces 'remoteWorkspaces' @('settingsPage', 'sharing', 'connected', 'passed')
        # The direct path's rows in Mobile remote over fixed endpoints and a recording firewall runner (MainWindow.MobileDirectSmoke.cs): no netsh runs.
        foreach ($key in @('rows', 'status', 'firewallRow', 'addCommand', 'removeCommand', 'outcomes', 'passed')) {
            if ($result.mobileDirect.$key -ne $true) { Fail "Windows mobile direct smoke failed: mobileDirect.$key" }
        }
        # Parent -> child delegation's close paths, driven with the app's scripted Claude (MainWindow.DelegationSmoke.cs).
        Require-Keys $result.delegation 'delegation' @('childrenListed', 'sidebarTree', 'heldRowPinned', 'windowCloseMergedChild', 'phoneCloseMergedChild', 'phoneSendReleasedHeldNotice', 'windowParentCloseAsks', 'phoneParentCloseKeepsChild', 'removeRefusedKeptWorktree', 'removedAfterDiscard')
        if ($result.delegation.removeRefusedOpenChild -ne 'workspace_has_children') { Fail "Windows delegation smoke failed: delegation.removeRefusedOpenChild=$($result.delegation.removeRefusedOpenChild)" }
        # A child of another agent (step 9): the worktree menu's pick of Claude on a read-only Codex pane starts a Claude child in plan mode.
        $cross = $result.delegation.otherAgentChildStarted
        if ($null -eq $cross -or $cross.parent -ne 'codex' -or $cross.child -ne 'claude' -or $cross.mode -ne 'plan' -or $cross.state -eq 'failed' -or $cross.state -eq 'creating') { Fail "Windows delegation smoke failed: delegation.otherAgentChildStarted=$($cross | ConvertTo-Json -Compress)" }
        $scan = $result.localeKeyLeakScan
        if ($null -eq $scan -or [int]$scan.scanned -lt 50) { Fail "로케일 키 누수 검사가 실행되지 않았거나 검사 수가 너무 적습니다: localeKeyLeakScan.scanned=$($scan.scanned)" }
        if (@($scan.leaks).Count -gt 0) { Fail "로케일 키가 화면에 그대로 노출됩니다: $($scan.leaks -join ', ')" }
        # Outcomes that are neither pass nor fail (a check that had to be skipped on
        # this runner) are published as a notice so they can be read without the log.
        $outcomes = @($result.PSObject.Properties | Where-Object { $_.Value -is [pscustomobject] -and $_.Value.status -is [string] } |
            ForEach-Object { "$($_.Name)=$($_.Value.status)$(if ($_.Value.reason) { " ($($_.Value.reason))" })" }) -join '; '
        if ($outcomes -and $env:GITHUB_ACTIONS) {
            $text = if ($outcomes.Length -gt 800) { $outcomes.Substring(0, 800) } else { $outcomes }
            Write-Output "::notice title=Windows GUI smoke outcomes::$($text.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A'))"
        }
        Write-Output "Windows web shell PASS: $resultPath"
        return
    }
    # The WinUI fallback's smoke (MainWindow.Smoke.cs): the window starts on the WinUI screens, a Claude pane and a shell pane
    # show, Settings opens and closes, and nothing throws.
    if ($result.winuiFallback -ne $true) { Fail "The WinUI smoke did not run on the WinUI screens: $resultPath" }
    Require-Keys $result.panes 'panes' @('claude', 'shell')
    Require-Keys $result.settings 'settings' @('opened', 'closed')
    $screenshot = Join-Path $ProfileDirectory 'smoke-window.png'
    if (-not (Test-Path $screenshot -PathType Leaf) -or (Get-Item $screenshot).Length -eq 0) { throw 'GUI 스크린샷이 없습니다.' }
    Write-Output "Windows WinUI fallback PASS: $resultPath"
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
