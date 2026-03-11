#define MyAppName "Babel Player"

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif

#ifndef MyAppPublisher
  #define MyAppPublisher "Babel Player"
#endif

#ifndef MyAppURL
  #define MyAppURL "https://github.com/mta1124-1629472/Babel-Player"
#endif

#ifndef MyPublishDir
  #error MyPublishDir define is required.
#endif

#ifndef MyOutputDir
  #error MyOutputDir define is required.
#endif

#ifndef MyOutputBaseFilename
  #define MyOutputBaseFilename "BabelPlayer-Setup"
#endif

[Setup]
AppId={{F3B8DCA5-6FCB-4C5D-B3E6-0E0EFA8241D9}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\Babel Player
DefaultGroupName=Babel Player
AllowNoIcons=yes
DisableDirPage=no
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\BabelPlayer.exe
SetupIconFile=..\src\Assets\BabelPlayer.ico
OutputDir={#MyOutputDir}
OutputBaseFilename={#MyOutputBaseFilename}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Babel Player"; Filename: "{app}\BabelPlayer.exe"
Name: "{autodesktop}\Babel Player"; Filename: "{app}\BabelPlayer.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\BabelPlayer.exe"; Description: "{cm:LaunchProgram,Babel Player}"; Flags: nowait postinstall skipifsilent
