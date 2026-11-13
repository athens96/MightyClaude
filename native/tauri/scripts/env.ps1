# Loads the MSVC developer environment and Rust into the current PowerShell session, so cargo, rustc and the Tauri CLI find
# link.exe and the Windows SDK. Dot-source it once per session:  . .\native\tauri\scripts\env.ps1
#
# The VS 2022 Build Tools on this PC are not registered with vswhere, so rustc cannot find the linker on its own: the
# environment vcvars64.bat sets is imported here instead. Running it again in the same session does nothing.
if ($env:MIGHTY_TAURI_ENV -eq '1') { return }

$vcvars = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) {
    # Any other VS 2022 edition, when the Build Tools are not where this PC has them.
    $vcvars = Get-ChildItem -Path (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio\2022'), (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022') `
        -Filter vcvars64.bat -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $vcvars) { throw 'vcvars64.bat (Visual Studio 2022 C++ build tools) was not found.' }

# vcvars64.bat asks vswhere.exe first and says on stderr that it is missing; that is expected here and silenced.
$lines = cmd /c "`"$vcvars`" >nul 2>nul && set"
if ($LASTEXITCODE -ne 0) { throw "vcvars64.bat failed ($LASTEXITCODE)." }
foreach ($line in $lines) {
    $at = $line.IndexOf('=')
    if ($at -gt 0) { [Environment]::SetEnvironmentVariable($line.Substring(0, $at), $line.Substring($at + 1), 'Process') }
}

$cargo = Join-Path $env:USERPROFILE '.cargo\bin'
if (($env:Path -split ';') -notcontains $cargo) { $env:Path = "$cargo;$env:Path" }
$env:MIGHTY_TAURI_ENV = '1'
