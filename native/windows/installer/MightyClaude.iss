; The Windows setup program: one per architecture, built by scripts/build-windows.ps1 -Installer from the publish folder
; it has just made and verified. It installs for this user only, without elevation, into %LOCALAPPDATA%\Programs\MightyClaude,
; because the app updates itself there afterwards: the update helper renames the install folder aside and copies the new one
; in (docs/windows-app-update.md), which needs a folder this user can write and no administrator.
;
;   ISCC.exe /DAppVersion=0.4.34 /DArch=x64 /DSourceDir=<publish folder> /DOutputDir=<release folder> MightyClaude.iss

#ifndef AppVersion
  #error AppVersion is required (/DAppVersion=x.y.z)
#endif
#ifndef Arch
  #error Arch is required (/DArch=x64 or /DArch=arm64)
#endif
#ifndef SourceDir
  #error SourceDir is required (/DSourceDir=<publish folder>)
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
#if Arch != "x64" && Arch != "arm64"
  #error Arch must be x64 or arm64
#endif

[Setup]
; Never change AppId: it names the uninstall entry, and the app keeps that entry's version in step after it updates itself
; (InstallerRegistration in MightyClaude.Core).
AppId={{F2F99AEF-FB35-4476-A3EE-49453FA0527F}
AppName=Mighty Claude
AppVersion={#AppVersion}
AppVerName=Mighty Claude {#AppVersion}
AppPublisher=Mighty Claude
AppPublisherURL=https://github.com/athens96/MightyClaude
AppSupportURL=https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude/help/
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Mighty Claude
PrivilegesRequired=lowest
DefaultDirName={userpf}\MightyClaude
; The folder is fixed: setup empties it before installing and uninstall removes it whole, which must never reach a folder
; the user picked for something else. Hiding the page is not enough (/DIR= and the previous folder still set {app}), so
; PrepareToInstall below refuses any other folder.
DisableDirPage=yes
UsePreviousAppDir=no
; The uninstaller lives outside the install folder: the app's own update replaces that folder with the ZIP's contents,
; which would take unins000.exe with it.
UninstallFilesDir={localappdata}\MightyClaudeSetup
DisableProgramGroupPage=yes
DefaultGroupName=Mighty Claude
UninstallDisplayName=Mighty Claude
UninstallDisplayIcon={app}\MightyClaude.exe
SetupIconFile=..\..\..\assets\icons\MightyClaude.ico
OutputDir={#OutputDir}
OutputBaseFilename=MightyClaude-windows-{#Arch}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Windows 10 2004, as the app itself requires.
MinVersion=10.0.19041
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
; A running Mighty Claude is asked to close before its files are replaced.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
#if FileExists(AddBackslash(CompilerPath) + "Languages\Unofficial\ChineseSimplified.isl")
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\Unofficial\ChineseSimplified.isl"
#endif

[CustomMessages]
; The unprefixed line is every language's fallback (English, and Chinese when its file is present).
OnlyOwnFolder=Mighty Claude can only be installed into %1.
korean.OnlyOwnFolder=Mighty Claude는 %1 에만 설치할 수 있습니다.
japanese.OnlyOwnFolder=Mighty Claude は %1 にのみインストールできます。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; An install over an older one starts from an empty folder, as the update helper's copy does, so no file the new version
; dropped is left behind.
Type: filesandordirs; Name: "{app}\*"

[Files]
; The whole publish folder, dot folders included (claude-mods\.claude-plugin\plugin.json): the exe alone does not run.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Mighty Claude"; Filename: "{app}\MightyClaude.exe"
Name: "{autodesktop}\Mighty Claude"; Filename: "{app}\MightyClaude.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MightyClaude.exe"; Description: "{cm:LaunchProgram,Mighty Claude}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The app replaces its own files when it updates, so the uninstall log no longer lists them all: remove the folder whole,
; and any backup a stopped update left beside it. Settings and conversations live elsewhere and stay.
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{app}.backup-*"

[Code]
// Runs before [InstallDelete] empties {app}: any folder but the fixed one is refused, whatever /DIR= said.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Expected: String;
begin
  Result := '';
  Expected := ExpandConstant('{userpf}\MightyClaude');
  if CompareText(RemoveBackslashUnlessRoot(ExpandConstant('{app}')), Expected) <> 0 then
    Result := FmtMessage(CustomMessage('OnlyOwnFolder'), [Expected]);
end;
