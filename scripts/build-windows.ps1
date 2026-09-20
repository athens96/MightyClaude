# Requires PowerShell 7 and the .NET 10 SDK on Windows.
param(
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'WinUI 패키지 빌드는 Windows의 PowerShell 7에서 실행하세요.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
foreach ($icon in @('MightyClaude.ico', 'mightyclaude.png')) {
    if (-not (Test-Path (Join-Path $projectRoot "assets/icons/$icon"))) { throw "기본 아이콘이 없습니다: assets/icons/$icon" }
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot "release/native-windows-$Architecture" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
# Never delete a caller-supplied directory or mix old runtime files into a new build.
if (Test-Path $OutputDirectory) {
    if (-not (Test-Path $OutputDirectory -PathType Container) -or (Get-ChildItem -Force $OutputDirectory | Select-Object -First 1)) {
        throw "빌드 출력에는 새 폴더나 빈 폴더를 지정하세요: $OutputDirectory"
    }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$platform = if ($Architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
Push-Location $projectRoot
try {
    # A failed check only says "exit code 1" in the job summary and its log needs a
    # signed-in reader, so the tail of the output is also published as an annotation.
    $coreLines = [Collections.Generic.List[string]]::new()
    dotnet run --project native/windows/MightyClaude.Core.Tests/MightyClaude.Core.Tests.csproj --configuration $Configuration 2>&1 | ForEach-Object { $coreLines.Add("$_"); Write-Output $_ }
    if ($LASTEXITCODE -ne 0) {
        if ($env:GITHUB_ACTIONS) {
            $tail = @($coreLines | Where-Object { $_ -notmatch '^PASS |^SKIP ' } | Select-Object -Last 12) -join "`n"
            if ($tail.Length -gt 1500) { $tail = $tail.Substring($tail.Length - 1500) }
            Write-Output "::error title=Windows Core verification::$($tail.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A'))"
        }
        throw 'Core 검증 실패'
    }
    dotnet publish native/windows/MightyClaude.WinUI/MightyClaude.WinUI.csproj --configuration $Configuration --runtime "win-$Architecture" --self-contained true -p:WindowsAppSDKSelfContained=true "-p:Platform=$platform" --output $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'WinUI 빌드 실패' }
    foreach ($required in @('LICENSE.txt', 'MightyClaude.exe', 'MightyClaude.dll', 'MightyClaude.runtimeconfig.json', 'resources.pri', 'coreclr.dll', 'hostfxr.dll', 'Microsoft.UI.Xaml.dll', 'Assets/MightyClaude.ico', 'Assets/mightyclaude.png', 'claude-mods/.claude-plugin/plugin.json')) {
        if (-not (Test-Path (Join-Path $OutputDirectory $required) -PathType Leaf)) { throw "배포 파일 누락: $required" }
    }
    $sdkVersion = (dotnet --version).Trim()
    [xml]$project = Get-Content native/windows/MightyClaude.WinUI/MightyClaude.WinUI.csproj
    $appSdk = ($project.Project.ItemGroup.PackageReference | Where-Object Include -eq 'Microsoft.WindowsAppSDK').Version
    @{
        architecture = $Architecture; configuration = $Configuration; dotnetSdk = $sdkVersion
        windowsAppSdk = $appSdk; sourceCommit = $env:GITHUB_SHA
        deployment = 'unpackaged-self-contained-folder'; signed = $false
    } | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'build-info.json') -Encoding utf8
    # ZipFile includes dot directories such as the required Claude plugin manifest.
    $archive = Join-Path (Split-Path -Parent $OutputDirectory) "MightyClaude-windows-$Architecture.zip"
    if (Test-Path $archive) { throw "기존 ZIP을 덮어쓰지 않습니다: $archive" }
    [IO.Compression.ZipFile]::CreateFromDirectory($OutputDirectory, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
    $hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($archive))" | Set-Content "$archive.sha256" -Encoding ascii
    Write-Output "Windows 네이티브 앱: $OutputDirectory/MightyClaude.exe"
    Write-Output "배포 ZIP: $archive"
} finally { Pop-Location }
