; FrameForge Windows installer (Inno Setup).
;
; Builds from the already-published win-x64 output. The installer is per-user by default
; and does not require administrator rights.
;
; Compile with:
;   ISCC /DMyAppVersion=<version> /DMyAppSourceDir=<publish dir> /DMyAppOutputDir=<dist> FrameForge.iss

#ifndef MyAppVersion
  #error MyAppVersion is required; use scripts/publish-win.ps1 -Installer
#endif
#ifndef MyAppSourceDir
  #error MyAppSourceDir must identify the published win-x64 directory
#endif
#ifndef MyAppOutputDir
  #define MyAppOutputDir "..\..\dist"
#endif

#if !FileExists(MyAppSourceDir + "\FrameForge.exe")
  #error Publish output must contain FrameForge.exe
#endif

[Setup]
AppId={{A0F52D83-2896-4A3B-BA5E-33E4BE389A4E}
AppName=FrameForge
AppVersion={#MyAppVersion}
AppVerName=FrameForge {#MyAppVersion}
AppPublisher=FrameForge
DefaultDirName={localappdata}\Programs\FrameForge
DefaultGroupName=FrameForge
UsePreviousAppDir=yes
UsePreviousTasks=yes
DisableDirPage=auto
UninstallLogMode=append
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#MyAppOutputDir}
OutputBaseFilename=FrameForge-Setup-{#MyAppVersion}
SetupIconFile=..\..\assets\branding\frameforge-icon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\FrameForge.exe
UninstallDisplayName=FrameForge
VersionInfoVersion={#MyAppVersion}.0
VersionInfoDescription=FrameForge {#MyAppVersion}
VersionInfoProductName=FrameForge
VersionInfoProductVersion={#MyAppVersion}.0
CloseApplications=yes
CloseApplicationsFilter=FrameForge.exe
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\FrameForge"; Filename: "{app}\FrameForge.exe"; WorkingDir: "{app}"; IconFilename: "{app}\FrameForge.exe"; Comment: "FrameForge {#MyAppVersion}"
Name: "{autodesktop}\FrameForge"; Filename: "{app}\FrameForge.exe"; WorkingDir: "{app}"; IconFilename: "{app}\FrameForge.exe"; Comment: "FrameForge {#MyAppVersion}"; Tasks: desktopicon

[Run]
Filename: "{app}\FrameForge.exe"; Description: "{cm:LaunchProgram,FrameForge}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
