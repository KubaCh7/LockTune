; Inno Setup script for LockTune (Windows). Built by .github/workflows/build-windows.yml:
;   iscc /DAppVersion=1.0.N /DSourceExe=path\to\LockTune.exe LockTune.iss
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\..\out\LockTune.exe"
#endif

[Setup]
AppId={{8C5A3E6B-4C1D-4E59-9C3A-6E2B7A1F0D42}
AppName=LockTune
AppVersion={#AppVersion}
AppVerName=LockTune {#AppVersion}
AppPublisher=LockTune
AppPublisherURL=https://github.com/KubaCh7/LockTune
AppSupportURL=https://github.com/KubaCh7/LockTune/issues
; Per-user install (no admin prompt); also lets the in-app updater replace the exe.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\LockTune
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir=.
OutputBaseFilename=LockTune-Setup
SetupIconFile=..\LockTune\Assets\LockTune.ico
UninstallDisplayIcon={app}\LockTune.exe
UninstallDisplayName=LockTune
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
CloseApplications=force
MinVersion=10.0.17763

[Tasks]
Name: "autostart"; Description: "Start LockTune when I sign in to Windows"
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\LockTune"; Filename: "{app}\LockTune.exe"
Name: "{autodesktop}\LockTune"; Filename: "{app}\LockTune.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "LockTune"; \
  ValueData: """{app}\LockTune.exe"" --background"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\LockTune.exe"; Description: "Open LockTune"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/im LockTune.exe /f"; Flags: runhidden; RunOnceId: "StopLockTune"

[UninstallDelete]
Type: files; Name: "{app}\LockTune.old.exe"
Type: files; Name: "{app}\LockTune.new.exe"
