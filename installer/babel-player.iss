; Babel Player — Inno Setup installer script
; https://jrsoftware.org/isinfo.php

#define AppName      "Babel Player"
#define AppPublisher "Babel Player Contributors"
#define AppURL       "https://github.com/mta1124-1629472/Babel-Player"
#define AppExeName   "BabelPlayer.exe"
#define AppLicense   "..\LICENSE"
#define SourceDir    "..\publish"

[Setup]
AppId={{A7F3C2D1-8B4E-4F6A-9C2D-1E5B7A3F8C4D}
AppName={#AppName}
AppVersion={#SetupVersion}
AppVerName={#AppName} {#SetupVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
DefaultDirName={commonpf}\Babel Player
DefaultGroupName={#AppName}
AllowNoIcons=yes
LicenseFile={#AppLicense}
OutputDir=..\dist
OutputBaseFilename=Babel-Player-{#SetupVersion}-win-x64-setup
SetupIconFile=..\Assets\Icons\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
; Constrain wizard to standard width — prevents the license page from
; appearing awkwardly wide on high-DPI displays or large monitors.
WizardSizePercent=100
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon";   Description: "{cm:CreateDesktopIcon}";   GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "quicklaunch";   Description: "Pin to taskbar";            GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Everything from the publish folder
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}";           Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}";     Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
