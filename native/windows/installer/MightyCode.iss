; The Windows setup program: one per architecture, built by scripts/build-windows.ps1 -Installer from the publish folder
; it has just made and verified. It installs for this user only, without elevation, into %LOCALAPPDATA%\Programs\MightyCode,
; because the app updates itself there afterwards: the update helper renames the install folder aside and copies the new one
; in (docs/windows-app-update.md), which needs a folder this user can write and no administrator.
;
;   ISCC.exe /DAppVersion=0.4.34 /DArch=x64 /DSourceDir=<publish folder> /DOutputDir=<release folder> MightyCode.iss

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
; (InstallerRegistration in MightyCode.Core).
AppId={{F2F99AEF-FB35-4476-A3EE-49453FA0527F}
AppName=Mighty Code
AppVersion={#AppVersion}
AppVerName=Mighty Code {#AppVersion}
AppPublisher=Mighty Code
AppPublisherURL=https://github.com/athens96/MightyCode
AppSupportURL=https://pub-b705901d18c940159d40f58683d758ad.r2.dev/mightycode/help/
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Mighty Code
PrivilegesRequired=lowest
DefaultDirName={userpf}\MightyCode
; The folder is fixed: setup empties it before installing and uninstall removes it whole, which must never reach a folder
; the user picked for something else. Hiding the page is not enough (/DIR= and the previous folder still set {app}), so
; PrepareToInstall below refuses any other folder. An install made before the rename sits in ...\Programs\MightyClaude (the
; app updates itself in place, so that folder now holds Mighty Code): setup over it keeps using that folder rather than
; leaving a second copy beside it, and PrepareToInstall accepts it as the one other folder.
DisableDirPage=yes
UsePreviousAppDir=yes
; The uninstaller lives outside the install folder: the app's own update replaces that folder with the ZIP's contents,
; which would take unins000.exe with it.
UninstallFilesDir={localappdata}\MightyCodeSetup
DisableProgramGroupPage=yes
DefaultGroupName=Mighty Code
UninstallDisplayName=Mighty Code
UninstallDisplayIcon={app}\MightyCode.exe
SetupIconFile=..\..\..\assets\icons\MightyCode.ico
OutputDir={#OutputDir}
OutputBaseFilename=MightyCode-windows-{#Arch}-setup
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
; A running Mighty Code is asked to close before its files are replaced.
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
OnlyOwnFolder=Mighty Code can only be installed into %1.
korean.OnlyOwnFolder=Mighty Code는 %1 에만 설치할 수 있습니다.
japanese.OnlyOwnFolder=Mighty Code は %1 にのみインストールできます。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; An install over an older one starts from an empty folder, as the update helper's copy does, so no file the new version
; dropped is left behind.
Type: filesandordirs; Name: "{app}\*"
; The Start menu shortcut of an install from before the rename; this one makes it again under the new name. A desktop
; shortcut was the user's own choice: it is removed too, and made again under the new name below (HadOldDesktopShortcut).
Type: files; Name: "{autoprograms}\Mighty Claude.lnk"
Type: files; Name: "{autodesktop}\Mighty Claude.lnk"

[Files]
; The whole publish folder, dot folders included (claude-mods\.claude-plugin\plugin.json): the exe alone does not run.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Mighty Code"; Filename: "{app}\MightyCode.exe"
Name: "{autodesktop}\Mighty Code"; Filename: "{app}\MightyCode.exe"; Tasks: desktopicon
Name: "{autodesktop}\Mighty Code"; Filename: "{app}\MightyCode.exe"; Check: HadOldDesktopShortcut

[Run]
Filename: "{app}\MightyCode.exe"; Description: "{cm:LaunchProgram,Mighty Code}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The app replaces its own files when it updates, so the uninstall log no longer lists them all: remove the folder whole,
; and any backup a stopped update left beside it. Settings and conversations live elsewhere and stay.
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{app}.backup-*"

[Code]
var
  OldDesktopShortcut: Boolean;

function HadOldDesktopShortcut: Boolean;
begin
  Result := OldDesktopShortcut;
end;

// Runs before [InstallDelete] removes the old desktop shortcut, so the new one can take its place.
function InitializeSetup: Boolean;
begin
  OldDesktopShortcut := FileExists(ExpandConstant('{autodesktop}\Mighty Claude.lnk'));
  Result := True;
end;

// Runs before [InstallDelete] empties {app}: any folder but the fixed one, or the one an install from before the rename used,
// is refused, whatever /DIR= said.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Expected, Folder: String;
begin
  Result := '';
  Expected := ExpandConstant('{userpf}\MightyCode');
  Folder := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  if (CompareText(Folder, Expected) <> 0) and (CompareText(Folder, ExpandConstant('{userpf}\MightyClaude')) <> 0) then
    Result := FmtMessage(CustomMessage('OnlyOwnFolder'), [Expected]);
end;
