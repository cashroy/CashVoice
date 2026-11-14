; CashVoice installer (Inno Setup 6). Built by .github/workflows/build.yml
[Setup]
AppName=CashVoice
AppVersion=0.1.0
AppPublisher=CashVoice
DefaultDirName={autopf}\CashVoice
DefaultGroupName=CashVoice
OutputDir=Output
OutputBaseFilename=CashVoice-Setup
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\CashVoice.exe
AppMutex=CashVoice.SingleInstance
DisableProgramGroupPage=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
; audiodg.exe is native, so the APO must match the OS architecture:
; an x64 DLL can never load into the audio engine on Windows on ARM.
Source: "..\publish\x64\CashVoice.exe"; DestDir: "{app}"; Flags: ignoreversion; Check: not IsArm64
Source: "..\apo\build\x64\Release\CashVoiceAPO.dll"; DestDir: "{app}"; Flags: ignoreversion; Check: not IsArm64
Source: "..\publish\arm64\CashVoice.exe"; DestDir: "{app}"; Flags: ignoreversion; Check: IsArm64
Source: "..\apo\build\arm64\Release\CashVoiceAPO.dll"; DestDir: "{app}"; Flags: ignoreversion; Check: IsArm64
Source: "CashVoice.cer"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\CashVoice"; Filename: "{app}\CashVoice.exe"
Name: "{autodesktop}\CashVoice"; Filename: "{app}\CashVoice.exe"; Tasks: desktopicon

[Run]
; Trust our (self-signed) certificate so Windows will load the signed APO.
Filename: "{sys}\certutil.exe"; Parameters: "-addstore -f Root ""{app}\CashVoice.cer"""; Flags: runhidden
Filename: "{sys}\certutil.exe"; Parameters: "-addstore -f TrustedPublisher ""{app}\CashVoice.cer"""; Flags: runhidden
; Register the APO COM server + audio-engine registration.
Filename: "{sys}\regsvr32.exe"; Parameters: "/s ""{app}\CashVoiceAPO.dll"""; Flags: runhidden
Filename: "{app}\CashVoice.exe"; Description: "Launch CashVoice"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Put each microphone's original driver effect back, restart audio, then unregister.
Filename: "{app}\CashVoice.exe"; Parameters: "--uninstall-apo"; Flags: runhidden waituntilterminated; RunOnceId: "RestoreMics"
Filename: "{sys}\regsvr32.exe"; Parameters: "/u /s ""{app}\CashVoiceAPO.dll"""; Flags: runhidden; RunOnceId: "UnregAPO"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var rc: Integer;
begin
  { release the DLL if an older version is loaded inside the audio engine }
  Exec(ExpandConstant('{sys}\net.exe'), 'stop audiosrv /y', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Result := '';
end;

procedure DeinitializeSetup();
var rc: Integer;
begin
  Exec(ExpandConstant('{sys}\net.exe'), 'start audiosrv', '', SW_HIDE, ewWaitUntilTerminated, rc);
end;
