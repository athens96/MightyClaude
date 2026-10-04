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
        Write-SmokeAnnotation "ExitCode=$($process.ExitCode)`nphase: $($result.phase) [$($result.stepState)]`nerror: $($result.error)`ntype: $($result.exceptionType)`npassed before it: $reached`n$trace"
        throw "GUI 검증 실패: ExitCode=$($process.ExitCode), 결과: $resultPath"
    }
    foreach ($key in @('exifOrientation', 'svgRaster', 'tiffRaster')) {
        if ($result.agentImages.$key -ne $true) { throw "Windows native image smoke failed: agentImages.$key" }
    }
    $transcriptActions = $result.transcriptActions
    foreach ($key in @('exactCodeCopy', 'nativeToolToggle', 'unknownActionRefused')) {
        if ($transcriptActions.$key -ne $true) { throw "Windows transcript action smoke failed: transcriptActions.$key" }
    }
    $styles = $result.styles
    if ($null -eq $styles -or [int]$styles.bundledCount -ne 3 -or [int]$styles.guidedActions -lt 1) {
        throw 'Windows style smoke did not load the bundled registry and guided actions.'
    }
    foreach ($key in @('picker', 'fullApprovalContents', 'localRunPermissionSnapshot')) {
        if ($styles.$key -ne $true) { throw "Windows style smoke failed: styles.$key" }
    }
    foreach ($key in @('liveSessionPopover', 'contextRingAndIdentifiers', 'attachmentMenu', 'imageExternalVerifiedSnapshot', 'agentUrlCardsNonmodalAndBounded', 'conversationResetPreservesDraftAndHistory')) {
        if ($result.smallParity.$key -ne $true) { throw "Windows pane parity smoke failed: smallParity.$key" }
    }
    $companion = $result.companion
    foreach ($key in @('bundledAtlasDecoded', 'allAnimationRowsRendered', 'nonActivatingWindow', 'pointerActionBoundToCard', 'selectedPetPreview', 'contextMenuNonActivating', 'contentDrivenHeightAndEdgeResize', 'keyboardAccessibleAgentStatus', 'renderedOverlaySnapshots')) {
        if ($companion.$key -ne $true) { throw "Windows companion smoke failed: companion.$key" }
    }
    foreach ($key in @('syntheticGpuCropEncoded', 'displayBoundsEnumerated')) {
        if ($result.screenCapture.$key -ne $true) { throw "Windows screen capture smoke failed: screenCapture.$key" }
    }
    if ($result.screenTapMarkerNoFocus -ne $true) { throw 'Windows screen tap marker stole focus or intercepted input.' }
    foreach ($key in @('packagedWebViewBridge', 'h264Negotiated', 'screenAndOverviewTracksDecoded', 'perPeerResolutionLimit', 'unicodeControlRoundTrip', 'hostDataRoundTrip', 'haltClosedChannel')) {
        if ($result.screenTransport.$key -ne $true) { throw "Windows screen transport smoke failed: screenTransport.$key" }
    }
    foreach ($key in @('trustedRendererLoaded', 'conptyUtf8RoundTrip', 'resizeBridge', 'externalNavigationBlocked', 'processClosed', 'restartPreservesRenderer', 'footerMetadata')) {
        if ($result.nativeTerminal.$key -ne $true) { throw "Windows native terminal smoke failed: nativeTerminal.$key" }
    }
    foreach ($key in @('dashboardRetainsPane', 'clockPreservesDashboardControls', 'dashboardWorkspaceActions', 'dashboardProviderMarks', 'settingsLoadedBeforeCapture', 'allSettingsCategories', 'bothThemes')) {
        if ($result.desktopSurfaces.$key -ne $true) { throw "Windows desktop design smoke failed: desktopSurfaces.$key" }
    }
    $screenshot = Join-Path $ProfileDirectory 'smoke-window.png'
    if (-not (Test-Path $screenshot -PathType Leaf) -or (Get-Item $screenshot).Length -eq 0) { throw 'GUI 스크린샷이 없습니다.' }
    $scanned = $result.localeKeyLeakScanned
    if ($null -eq $scanned -or ($scanned -isnot [int] -and $scanned -isnot [long]) -or [int]$scanned -lt 50) {
        $msg = "로케일 키 누수 검사가 실행되지 않았거나 검사 수가 너무 적습니다: localeKeyLeakScanned=$scanned"
        Write-SmokeAnnotation $msg; throw $msg
    }
    if ($result.componentsSection -ne $true) {
        $msg = "구성 요소 칸 스모크가 실행되지 않았거나 통과하지 못했습니다: componentsSection=$($result.componentsSection)"
        Write-SmokeAnnotation $msg; throw $msg
    }
    $bp = $result.browserPane
    if ($null -eq $bp) {
        $msg = "browserPane 스모크가 실행되지 않았습니다: browserPane 키가 없습니다"
        Write-SmokeAnnotation $msg; throw $msg
    }
    foreach ($key in @('runtimeVersion', 'navigated', 'backWorked', 'profileUnderTemp', 'popupBlocked')) {
        $val = $bp.$key
        if (-not $val) {
            $msg = "browserPane.$key 값이 없거나 false입니다: $val"
            Write-SmokeAnnotation $msg; throw $msg
        }
    }
    # Pane auto-titles: 자동 in the rename dialog hands the title back to the latest request.
    if ($result.rename.automaticFollowsLatestRequest -ne $true) {
        $msg = "rename.automaticFollowsLatestRequest 값이 없거나 false입니다: $($result.rename.automaticFollowsLatestRequest)"
        Write-SmokeAnnotation $msg; throw $msg
    }
    # Inline agent pictures: a cached picture and a Markdown picture drawn into the transcript.
    $ai = $result.agentImages
    if ($null -eq $ai) {
        $msg = "agentImages 스모크가 실행되지 않았습니다: agentImages 키가 없습니다"
        Write-SmokeAnnotation $msg; throw $msg
    }
    foreach ($key in @('loadingPlaceholderFirst', 'inlinePicture', 'markdownPicture', 'sourceCaption', 'remoteLinkOnly', 'outsideRefused', 'referencesOnlySaved')) {
        if ($ai.$key -ne $true) {
            $msg = "agentImages.$key 값이 없거나 false입니다: $($ai.$key)"
            Write-SmokeAnnotation $msg; throw $msg
        }
    }
    $fp = $result.filesPane
    if ($null -eq $fp) {
        $msg = "filesPane 스모크가 실행되지 않았습니다: filesPane 키가 없습니다"
        Write-SmokeAnnotation $msg; throw $msg
    }
    foreach ($key in @('paneOpened', 'placedLeft', 'noiseCollapsed', 'folderOpened', 'markdown', 'swift', 'png', 'reopenFocusesSamePane', 'neverSaved')) {
        if ($fp.$key -ne $true) {
            $msg = "filesPane.$key 값이 없거나 false입니다: $($fp.$key)"
            Write-SmokeAnnotation $msg; throw $msg
        }
    }
    if ($fp.shortcut -ne 'Ctrl+Shift+E') {
        $msg = "filesPane.shortcut 값이 Ctrl+Shift+E가 아닙니다: $($fp.shortcut)"
        Write-SmokeAnnotation $msg; throw $msg
    }
    # The composer model button and picker read family plus version (macOS ModelLabel).
    $ml = $result.modelLabel
    if ($null -eq $ml -or $ml.valuesUnchanged -ne $true -or $ml.reportedAlias -ne 'Opus 5.5') {
        $msg = "modelLabel 스모크 값이 없거나 false입니다: $($ml | ConvertTo-Json -Compress)"
        Write-SmokeAnnotation $msg; throw $msg
    }
    # Codex and Gemini agent rows and tabs carry the 베타 capsule after their titles.
    $bb = $result.betaBadge
    if ($null -eq $bb -or $bb.betaRowsCarryBadge -ne $true -or @($bb.providers).Count -ne 2) {
        $msg = "betaBadge 스모크 값이 없거나 false입니다: $($bb | ConvertTo-Json -Compress)"
        Write-SmokeAnnotation $msg; throw $msg
    }
    $sh = $result.sessionHistory
    if ($null -eq $sh) {
        $msg = "sessionHistory 스모크가 실행되지 않았습니다: sessionHistory 키가 없습니다"
        Write-SmokeAnnotation $msg; throw $msg
    }
    foreach ($key in @('choiceOffered', 'nestedRunHidden', 'resumedInNewPane', 'latestLoadedOnOpen', 'olderLoadedAboveWithoutMoving', 'openSessionNotOffered')) {
        if ($sh.$key -ne $true) {
            $msg = "sessionHistory.$key 값이 없거나 false입니다: $($sh.$key)"
            Write-SmokeAnnotation $msg; throw $msg
        }
    }
    # The 창 추가 menu ends with 프로젝트 폴더 열기…; the sidebar open-folder button shows only
    # when no workspace is listed; Gemini and Ctrl+N start without asking (macOS WorkspaceView).
    $ap = $result.addPaneMenu
    if ($null -eq $ap) {
        $msg = "addPaneMenu 스모크가 실행되지 않았습니다: addPaneMenu 키가 없습니다"
        Write-SmokeAnnotation $msg; throw $msg
    }
    foreach ($key in @('openProjectLast', 'openFolderButtonOnlyWhenNoneListed', 'geminiStartsAtOnce', 'ctrlNStartsAtOnce')) {
        if ($ap.$key -ne $true) {
            $msg = "addPaneMenu.$key 값이 없거나 false입니다: $($ap.$key)"
            Write-SmokeAnnotation $msg; throw $msg
        }
    }
    if ($ap.shortcuts -ne 'Ctrl+O, Ctrl+N') {
        $msg = "addPaneMenu.shortcuts 값이 'Ctrl+O, Ctrl+N'이 아닙니다: $($ap.shortcuts)"
        Write-SmokeAnnotation $msg; throw $msg
    }
    foreach ($key in @("modePersists", "groupAndRowExpansion", "sameTranscriptActions", "resultFilesOpenSharedPreview", "unchangedRefreshKeepsControls", "diagramAndDraftPreserved", "historyAffordance", "narrowToolbar")) {
        if ($result.mightyTimeline.$key -ne $true) { throw "Mighty timeline smoke failed: mightyTimeline.$key" }
    }
    $mg = $result.mightyGraph
    if ($null -eq $mg) {
        $msg = "mighty 그래프 스모크가 실행되지 않았습니다: mightyGraph 키가 없습니다"
        Write-SmokeAnnotation $msg; throw $msg
    }
    if ([int]$mg.blocks -lt 3) {
        $msg = "mighty 그래프 블록이 3개 미만입니다: blocks=$($mg.blocks)"
        Write-SmokeAnnotation $msg; throw $msg
    }
    if ([int]$mg.edges -lt 2) {
        $msg = "mighty 그래프 엣지가 2개 미만입니다: edges=$($mg.edges)"
        Write-SmokeAnnotation $msg; throw $msg
    }
    $zoom = @($mg.zoom)
    if ($zoom.Count -lt 3 -or [int]$zoom[0] -ne 50 -or [int]$zoom[1] -ne 100 -or [int]$zoom[2] -ne 150) {
        $msg = "mighty 그래프 줌이 [50,100,150]이 아닙니다: zoom=$($mg.zoom)"
        Write-SmokeAnnotation $msg; throw $msg
    }
    if ($mg.modeRestored -ne $true) {
        $msg = "mighty 그래프 모드 복원이 실패했습니다: modeRestored=$($mg.modeRestored)"
        Write-SmokeAnnotation $msg; throw $msg
    }
    # The Mighty result box shrinks with the pane and grows back to its saved size.
    $rf = $mg.resultFit
    if ($null -eq $rf) {
        $msg = "결과 박스 스모크가 실행되지 않았습니다: mightyGraph.resultFit 키가 없습니다"
        Write-SmokeAnnotation $msg; throw $msg
    }
    foreach ($key in 'fitButton', 'grewToSaved', 'shrank', 'grewBack', 'zoomedInside', 'fitCleared') {
        if ($rf.$key -ne $true) {
            $msg = "mightyGraph.resultFit.$key 값이 없거나 false입니다: $($rf.$key)"
            Write-SmokeAnnotation $msg; throw $msg
        }
    }
    # A new result shows right above the composer at its content height under the saved size.
    $rr = $mg.resultReveal
    if ($null -eq $rr) {
        $msg = "새 결과 위치 스모크가 실행되지 않았습니다: mightyGraph.resultReveal 키가 없습니다"
        Write-SmokeAnnotation $msg; throw $msg
    }
    foreach ($key in 'revealed', 'contentFit', 'savedKept', 'followsResize', 'userPanStops') {
        if ($rr.$key -ne $true) {
            $msg = "mightyGraph.resultReveal.$key 값이 없거나 false입니다: $($rr.$key)"
            Write-SmokeAnnotation $msg; throw $msg
        }
    }
    # Request block headers carry the pane's agent mark before its name; other headers none.
    $rm = $mg.requestMarks
    if ($null -eq $rm -or [int]$rm.requestHeaders -lt 1 -or [string]::IsNullOrEmpty([string]$rm.provider)) {
        $msg = "요청 블록 마크 스모크가 실행되지 않았습니다: mightyGraph.requestMarks=$($rm | ConvertTo-Json -Compress)"
        Write-SmokeAnnotation $msg; throw $msg
    }
    # Running blocks march a dashed outline (solid with animations off); waiting blocks stay amber.
    $ol = $mg.outlines
    if ($null -eq $ol -or $ol.running -ne 'marching' -or $ol.reducedMotion -ne 'solid' -or $ol.waiting -ne 'waiting') {
        $msg = "실행 표시 테두리 스모크가 실패했습니다: mightyGraph.outlines=$($ol | ConvertTo-Json -Compress)"
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
