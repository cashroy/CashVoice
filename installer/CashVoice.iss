; CashVoice installer (Inno Setup 6). Built by .github/workflows/build.yml
#ifndef MyAppVersion
  #define MyAppVersion "0.2.4"
#endif
[Setup]
AppName=CashVoice
AppId={{6C1F0E4A-3B9D-4F2A-9E57-C45D1A0B8E21}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
SetupIconFile=..\app\app.ico
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

[Dirs]
; Shared buffer between the app and the APO (audiodg.exe can't create a Global\ section).
Name: "{commonappdata}\CashVoice"

[Icons]
Name: "{group}\CashVoice"; Filename: "{app}\CashVoice.exe"
Name: "{autodesktop}\CashVoice"; Filename: "{app}\CashVoice.exe"; Tasks: desktopicon

[Run]
; Trust our (self-signed) certificate so Windows will load the signed APO.
Filename: "{sys}\certutil.exe"; Parameters: "-addstore -f Root ""{app}\CashVoice.cer"""; Flags: runhidden
Filename: "{sys}\certutil.exe"; Parameters: "-addstore -f TrustedPublisher ""{app}\CashVoice.cer"""; Flags: runhidden
; Shared folder: modify rights for Everyone, LOCAL SERVICE (audiodg), write-restricted tokens and app packages.
Filename: "{sys}\icacls.exe"; Parameters: """{commonappdata}\CashVoice"" /grant *S-1-1-0:(OI)(CI)M *S-1-5-19:(OI)(CI)M *S-1-5-33:(OI)(CI)M *S-1-15-2-1:(OI)(CI)M"; Flags: runhidden
; Register the APO COM server + audio-engine registration.
Filename: "{sys}\regsvr32.exe"; Parameters: "/s ""{app}\CashVoiceAPO.dll"""; Flags: runhidden
Filename: "{app}\CashVoice.exe"; Description: "Launch CashVoice"; Flags: nowait postinstall skipifsilent runasoriginaluser

[InstallDelete]
; leftovers from in-app updates
Type: files; Name: "{app}\*.old"

[UninstallDelete]
Type: files; Name: "{app}\*.old"
Type: filesandordirs; Name: "{commonappdata}\CashVoice"

[UninstallRun]
; Put each microphone's original driver effect back, restart audio, then unregister.
Filename: "{app}\CashVoice.exe"; Parameters: "--uninstall-apo"; Flags: runhidden waituntilterminated; RunOnceId: "RestoreMics"
Filename: "{sys}\regsvr32.exe"; Parameters: "/u /s ""{app}\CashVoiceAPO.dll"""; Flags: runhidden; RunOnceId: "UnregAPO"

[Code]
const
  RuntimeUrlX64   = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';
  RuntimeUrlArm64 = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-arm64.exe';

{ True if a .NET Desktop Runtime 8 or newer is installed for this PC's architecture. }
function HasDesktopRuntime(): Boolean;
var
  FindRec: TFindRec;
  Name: String;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\*'), FindRec) then
  try
    repeat
      Name := FindRec.Name;
      if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (Pos('.', Name) > 1) then
        if StrToIntDef(Copy(Name, 1, Pos('.', Name) - 1), 0) >= 8 then Result := True;
    until Result or not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

{ CashVoice.exe is framework-dependent (~1 MB). Fetch Microsoft's runtime only when it's missing. }
function InstallDesktopRuntime(): String;
var
  Url: String;
  rc: Integer;
begin
  Result := '';
  if HasDesktopRuntime() then Exit;
  if IsArm64 then Url := RuntimeUrlArm64 else Url := RuntimeUrlX64;
  try
    DownloadTemporaryFile(Url, 'dotnet-desktop-runtime.exe', '', nil);
  except
    Result := 'Couldn''t download the .NET Desktop Runtime that CashVoice needs. Check your internet connection and run setup again.';
    Exit;
  end;
  if not Exec(ExpandConstant('{tmp}\dotnet-desktop-runtime.exe'), '/install /quiet /norestart', '',
              SW_HIDE, ewWaitUntilTerminated, rc)
     or ((rc <> 0) and (rc <> 3010) and (rc <> 1641)) then
    Result := 'The .NET Desktop Runtime didn''t install (code ' + IntToStr(rc) + '). Run setup again.';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var rc: Integer;
begin
  Result := InstallDesktopRuntime();
  if Result <> '' then Exit;
  { release the DLL if an older version is loaded inside the audio engine }
  Exec(ExpandConstant('{sys}\net.exe'), 'stop audiosrv /y', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Result := '';
end;

procedure DeinitializeSetup();
var rc: Integer;
begin
  Exec(ExpandConstant('{sys}\net.exe'), 'start audiosrv', '', SW_HIDE, ewWaitUntilTerminated, rc);
end;
