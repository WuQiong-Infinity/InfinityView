; Original LightView installer. Inno compiler/engine: pinned 6.7.3, unmodified.
; No user-data deletion, default takeover, or application termination.
#ifndef PackageDir
  #error Supply a verified package with scripts/Build-Installer.ps1
#endif
#ifdef TestMode
  #define ProductName "InfinityView Install Test"
  #define LegacyProductName "LightView Install Test"
  #define DirectoryName "InfinityView.InstallTest"
#else
  #define ProductName "InfinityView"
  #define LegacyProductName "LightView"
  #define DirectoryName "InfinityView"
#endif

[Setup]
AppId={#Identity}
AppName={#ProductName}
AppVersion={#AppVersion}
AppPublisher=InfinityView
LicenseFile=..\LICENSE.txt
DefaultDirName={localappdata}\Programs\{#DirectoryName}
DisableDirPage=no
UsePreviousAppDir=yes
AlwaysShowDirOnReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
CloseApplications=no
RestartApplications=no
AllowCancelDuringInstall=no
DisableProgramGroupPage=yes
SetupMutex={#Identity}.Setup
UninstallDisplayIcon={app}\InfinityView.exe
UninstallDisplayName={#ProductName}
SetupIconFile=..\src\v0.4\LightView.App\Assets\InfinityView.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
OutputBaseFilename=LightView-setup

[Languages]
Name: "zhHans"; MessagesFile: "languages/ChineseSimplified.isl"

[Run]
Filename: "{app}\InfinityView.exe"; Description: "启动 InfinityView"; Flags: postinstall nowait skipifsilent unchecked; Check: InstallSucceeded
Filename: "{app}"; Description: "打开安装目录"; Flags: postinstall shellexec nowait skipifsilent unchecked; Check: InstallSucceeded

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MarkerFile}"; DestDir: "{app}"; DestName: ".lightview-install"; Flags: ignoreversion
Source: "{#IntegrityFile}"; DestName: "install-integrity.txt"; Flags: dontcopy

#ifndef TestMode
[Icons]
Name: "{userprograms}\InfinityView"; Filename: "{app}\InfinityView.exe"; AppUserModelID: "LightView.Desktop"
#endif

[Code]
var
  BackupDirectory: String;
  OldFiles, Integrity, IncomingExisted, OldFileBackedUp: TArrayOfString;
  Successful, CopyStarted, IntegrityExtracted: Boolean;
  FailureMessage: String;
  PreviousDirectory: String;
  UninstallFields, OldUninstallState, NewUninstallState: TArrayOfString;
  HaveOldUninstallState, HaveNewUninstallState: Boolean;
  ProgramDirectoryMoved, DataDirectoryMoved: Boolean;
  LegacyDataPath: String;
  MetadataRestored, HadInfinityShortcutBackup: Boolean;

function InstallSucceeded: Boolean;
begin
  Result := Successful;
end;

function GetDriveTypeW(Root: String): LongWord;
  external 'GetDriveTypeW@kernel32.dll stdcall';

function CreateFileW(Name: String; Access, Share: LongWord; Security: Integer;
  Disposition, Flags: LongWord; Template: Integer): Integer;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandle(Handle: Integer): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function GetFileAttributesW(Name: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function RegOpenKeyExW(Root: Integer; Subkey: String; Options, Access: LongWord; var Key: Integer): Integer;
  external 'RegOpenKeyExW@advapi32.dll stdcall';
function RegQueryValueType(Key: Integer; Name: String; Reserved: Integer; var Kind: LongWord; Data: Integer; var Size: LongWord): Integer;
  external 'RegQueryValueExW@advapi32.dll stdcall';
function RegCloseKey(Key: Integer): Integer;
  external 'RegCloseKey@advapi32.dll stdcall';
function Running: Boolean; forward;

function UninstallKey: String;
begin Result := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#Identity}_is1'; end;

function BrandDirectoryRename: Boolean;
begin
  Result := (PreviousDirectory <> '') and
    (CompareText(ExtractFileName(PreviousDirectory), 'LightView') = 0) and
    (CompareText(ExtractFileName(RemoveBackslashUnlessRoot(ExpandConstant('{app}'))), 'InfinityView') = 0) and
    (CompareText(ExtractFileDir(PreviousDirectory), ExtractFileDir(RemoveBackslashUnlessRoot(ExpandConstant('{app}')))) = 0);
end;

function AcceptedMetadataDirectory(Directory: String): Boolean;
begin
  Directory := RemoveBackslashUnlessRoot(ExpandFileName(Directory));
  Result := CompareText(Directory, RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) = 0;
  if not Result and BrandDirectoryRename then Result := CompareText(Directory, PreviousDirectory) = 0;
end;

procedure InitializeUninstallFields;
begin
  SetArrayLength(UninstallFields, 20);
  UninstallFields[0] := 'Inno Setup: Setup Version'; UninstallFields[1] := 'Inno Setup: App Path';
  UninstallFields[2] := 'InstallLocation'; UninstallFields[3] := 'Inno Setup: Icon Group';
  UninstallFields[4] := 'Inno Setup: User'; UninstallFields[5] := 'Inno Setup: Language';
  UninstallFields[6] := 'DisplayName'; UninstallFields[7] := 'DisplayIcon';
  UninstallFields[8] := 'UninstallString'; UninstallFields[9] := 'QuietUninstallString';
  UninstallFields[10] := 'Publisher'; UninstallFields[11] := 'DisplayVersion'; UninstallFields[12] := 'InstallDate';
  UninstallFields[13] := 'NoModify'; UninstallFields[14] := 'NoRepair'; UninstallFields[15] := 'EstimatedSize';
  UninstallFields[16] := 'MajorVersion'; UninstallFields[17] := 'MinorVersion';
  UninstallFields[18] := 'VersionMajor'; UninstallFields[19] := 'VersionMinor';
end;

function UninstallValueType(Name: String): LongWord;
var Key, Code: Integer; Access, Kind, Size: LongWord;
begin
  Access := 1;
  if Is64BitInstallMode then Access := Access or $100 else Access := Access or $200;
  if RegOpenKeyExW(-2147483647, UninstallKey, 0, Access, Key) <> 0 then RaiseException('Cannot query owned uninstall metadata type.');
  try
    Size := 0;
    Code := RegQueryValueType(Key, Name, 0, Kind, 0, Size);
    if Code <> 0 then RaiseException('Cannot read owned uninstall metadata type: ' + Name);
    Result := Kind;
  finally RegCloseKey(Key); end;
end;

function OwnedUninstallCommand(State: String): Boolean;
var Command, Name: String; I: Integer;
begin
  Result := False;
  if (Length(State) < 4) or (Copy(State, 1, 2) <> 'S"') or (Copy(State, Length(State), 1) <> '"') then Exit;
  Command := Copy(State, 3, Length(State) - 3);
  if not AcceptedMetadataDirectory(ExtractFileDir(Command)) then Exit;
  Name := ExtractFileName(Command);
  if (Length(Name) <> 12) or (CompareText(Copy(Name, 1, 5), 'unins') <> 0) or (CompareText(Copy(Name, 9, 4), '.exe') <> 0) then Exit;
  for I := 6 to 8 do if (Name[I] < '0') or (Name[I] > '9') then Exit;
  Result := FileExists(Command);
  if not Result and ProgramDirectoryMoved then
    Result := FileExists(AddBackslash(ExpandConstant('{app}')) + Name);
end;

function ReadUninstallValue(Index: Integer): String;
var Text: String; Number, Kind: LongWord;
begin
  Result := 'M';
  if not RegValueExists(HKCU, UninstallKey, UninstallFields[Index]) then Exit;
  Kind := UninstallValueType(UninstallFields[Index]);
  if ((Index < 13) and (Kind <> 1)) or ((Index >= 13) and (Kind <> 4)) then begin Result := 'X' + IntToStr(Kind); Exit; end;
  if Index < 13 then begin
    if not RegQueryStringValue(HKCU, UninstallKey, UninstallFields[Index], Text) then RaiseException('Cannot read uninstall string.');
    Result := 'S' + Text;
  end else begin
    if not RegQueryDWordValue(HKCU, UninstallKey, UninstallFields[Index], Number) then RaiseException('Cannot read uninstall DWORD.');
    Result := 'D' + IntToStr(Int64(Number));
  end;
end;

procedure ReadStrictUninstallState(var State: TArrayOfString);
var Names, Children: TArrayOfString; I, J: Integer; Known: Boolean;
begin
  if not RegKeyExists(HKCU, UninstallKey) then RaiseException('Owned uninstall metadata is missing; repair the existing installation first.');
  if not RegGetSubkeyNames(HKCU, UninstallKey, Children) or (GetArrayLength(Children) <> 0) then RaiseException('Uninstall metadata contains unsupported subkeys; nothing was replaced.');
  if not RegGetValueNames(HKCU, UninstallKey, Names) then RaiseException('Cannot enumerate owned uninstall metadata.');
  for I := 0 to GetArrayLength(Names) - 1 do begin
    Known := False;
    for J := 0 to 19 do if CompareText(Names[I], UninstallFields[J]) = 0 then Known := True;
    if not Known then RaiseException('Uninstall metadata contains an unknown value; nothing was replaced: ' + Names[I]);
  end;
  SetArrayLength(State, 20);
  for I := 0 to 19 do begin
    State[I] := ReadUninstallValue(I);
    if Copy(State[I], 1, 1) = 'X' then RaiseException('Uninstall metadata has an unsupported type; nothing was replaced: ' + UninstallFields[I]);
  end;
  if (Copy(State[1], 1, 1) <> 'S') or (Copy(State[2], 1, 1) <> 'S') then
    RaiseException('Owned uninstall metadata paths are missing.');
  if not AcceptedMetadataDirectory(Copy(State[1], 2, MaxInt)) or
     not AcceptedMetadataDirectory(Copy(State[2], 2, MaxInt)) or
     (CompareText(RemoveBackslashUnlessRoot(Copy(State[1], 2, MaxInt)), RemoveBackslashUnlessRoot(Copy(State[2], 2, MaxInt))) <> 0) or
     ((State[6] <> 'S{#ProductName}') and (State[6] <> 'S{#LegacyProductName}')) or
     (not OwnedUninstallCommand(State[8])) then
    RaiseException('Owned uninstall metadata path or identity does not match this installation.');
end;

function SnapshotEscape(Value: String): String;
begin
  StringChangeEx(Value, '%', '%25', True); StringChangeEx(Value, #13, '%0D', True);
  StringChangeEx(Value, #10, '%0A', True); StringChangeEx(Value, '|', '%7C', True);
  Result := Value;
end;

procedure SaveUninstallState(Name: String; State: TArrayOfString);
var Lines, Verify: TArrayOfString; I: Integer; Path: String;
begin
  SetArrayLength(Lines, 22); Lines[0] := 'AppId={#Identity}'; Lines[1] := 'Path=' + SnapshotEscape(ExpandConstant('{app}'));
  for I := 0 to 19 do Lines[I + 2] := UninstallFields[I] + '|' + SnapshotEscape(State[I]);
  Path := BackupDirectory + '\' + Name;
  if not SaveStringsToUTF8File(Path, Lines, False) or not LoadStringsFromFile(Path, Verify) or (GetArrayLength(Verify) <> 22) then
    RaiseException('Cannot persist uninstall metadata snapshot.');
  for I := 0 to 21 do if Verify[I] <> Lines[I] then RaiseException('Uninstall metadata snapshot readback differs.');
end;

procedure MetadataRollbackStatus(Message: String);
begin
  Log(Message);
  if BackupDirectory <> '' then
    if not SaveStringToFile(BackupDirectory + '\uninstall-rollback.status.txt', Message + #13#10, True) then Log('Cannot persist uninstall rollback status; backup retained.');
end;

procedure RestoreUninstallState(ProgramRestored: Boolean);
var I: Integer; Current: String; Number: Int64; Written, Complete: Boolean;
begin
  if not HaveOldUninstallState then Exit;
  if not ProgramRestored or Running then begin MetadataRollbackStatus('UNINSTALL METADATA RESTORE INCOMPLETE: program restore incomplete or instance running.'); Exit; end;
  if not HaveNewUninstallState then begin MetadataRollbackStatus('UNINSTALL METADATA RESTORE INCOMPLETE: no trusted engine write snapshot.'); Exit; end;
  if not RegKeyExists(HKCU, UninstallKey) then begin MetadataRollbackStatus('UNINSTALL METADATA RESTORE INCOMPLETE: uninstall key disappeared.'); Exit; end;
  for I := 0 to 19 do
    if (I = 1) or (I = 2) or (I = 6) or (I = 8) then begin
      Current := ReadUninstallValue(I);
      if (Current <> OldUninstallState[I]) and (Current <> NewUninstallState[I]) then begin
        MetadataRollbackStatus('UNINSTALL METADATA RESTORE INCOMPLETE: ownership field conflict retained: ' + UninstallFields[I]); Exit;
      end;
    end;
  Complete := True;
  for I := 0 to 19 do begin
    Current := ReadUninstallValue(I);
    if Current <> OldUninstallState[I] then begin
      if Current <> NewUninstallState[I] then begin
        Complete := False; MetadataRollbackStatus('Uninstall metadata external change retained: ' + UninstallFields[I]);
      end else begin
        if OldUninstallState[I] = 'M' then Written := RegDeleteValue(HKCU, UninstallKey, UninstallFields[I])
        else if I < 13 then Written := RegWriteStringValue(HKCU, UninstallKey, UninstallFields[I], Copy(OldUninstallState[I], 2, MaxInt))
        else begin
          Number := StrToInt64(Copy(OldUninstallState[I], 2, MaxInt));
          Written := RegWriteDWordValue(HKCU, UninstallKey, UninstallFields[I], LongWord(Number));
        end;
        if not Written or (ReadUninstallValue(I) <> OldUninstallState[I]) then begin Complete := False; MetadataRollbackStatus('Uninstall metadata restore/writeback failed: ' + UninstallFields[I]); end;
      end;
    end;
  end;
  MetadataRestored := Complete;
  if Complete then MetadataRollbackStatus('UNINSTALL METADATA RESTORED: old typed values and missing states verified.')
  else MetadataRollbackStatus('UNINSTALL METADATA RESTORE INCOMPLETE: conflicts or write failures; backup retained.');
end;

procedure CheckNoLinks(Path: String);
var Parent: String; Attributes: LongWord;
begin
  repeat
    Attributes := GetFileAttributesW(Path);
    if Attributes <> $FFFFFFFF then
      if (Attributes and $400) <> 0 then RaiseException('Linked installation paths are not supported: ' + Path);
    Parent := ExtractFileDir(Path);
    if (Parent = '') or (Parent = Path) then Break;
    Path := Parent;
  until False;
end;

function DataDirectory: String;
begin
  Result := GetEnv('INFINITYVIEW_DATA_DIR');
  if Result = '' then Result := GetEnv('LIGHTVIEW_DATA_DIR');
  if Result = '' then Result := ExpandConstant('{localappdata}\InfinityView');
  Result := ExpandFileName(Result);
end;

function LegacyDataDirectory: String;
begin
  Result := '';
#ifdef TestMode
  Result := ExpandConstant('{param:TestLegacyDataDirectory|}');
  if Result <> '' then begin
    Result := RemoveBackslashUnlessRoot(ExpandFileName(Result));
    if (CompareText(ExtractFileName(Result), 'LightView') <> 0) or
       (CompareText(ExtractFileName(RemoveBackslashUnlessRoot(DataDirectory)), 'InfinityView') <> 0) or
       (CompareText(ExtractFileDir(Result), ExtractFileDir(RemoveBackslashUnlessRoot(DataDirectory))) <> 0) then
      RaiseException('Invalid isolated-test migration directories.');
  end;
#else
  if (GetEnv('INFINITYVIEW_DATA_DIR') = '') and (GetEnv('LIGHTVIEW_DATA_DIR') = '') then
    Result := ExpandConstant('{localappdata}\LightView');
#endif
end;

function RunningAt(Directory: String): Boolean;
var Name: String;
begin
  Name := 'Local\LightView-' + Uppercase(Copy(GetSHA256OfString(Utf8Encode(Uppercase(ExpandFileName(Directory)))), 1, 24));
  Result := CheckForMutexes(Name);
end;

function Running: Boolean;
var Legacy: String;
begin
  Result := RunningAt(DataDirectory);
  Legacy := LegacyDataDirectory;
  if (not Result) and (Legacy <> '') then Result := RunningAt(Legacy);
end;

function ChildPath(Root, Relative: String): String;
begin
  if (Relative = '') or (Pos(':', Relative) > 0) or (Relative[1] = '\') or (Relative[1] = '/') then
    RaiseException('Invalid installed file manifest.');
  Result := ExpandFileName(AddBackslash(Root) + Relative);
  if Pos(Lowercase(AddBackslash(ExpandFileName(Root))), Lowercase(Result)) <> 1 then
    RaiseException('Installed file path escapes its directory.');
  CheckNoLinks(Result);
end;

function HasFiles(Directory: String): Boolean;
var Item: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Directory) + '*', Item) then begin
    try
      repeat
        if (Item.Name <> '.') and (Item.Name <> '..') then begin Result := True; Break; end;
      until not FindNext(Item);
    finally FindClose(Item); end;
  end;
end;

function OwnDirectory: Boolean;
var Marker: TArrayOfString;
begin
  Result := LoadStringsFromFile(ExpandConstant('{app}\.lightview-install'), Marker);
  if Result then Result := (GetArrayLength(Marker) = 1);
  if Result then Result := (Marker[0] = '{#Identity}');
end;

procedure CheckLocation;
var Path, Root: String;
begin
  Path := RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{app}')));
  CheckNoLinks(Path);
  Root := ExtractFileDrive(Path) + '\';
  if (Length(Path) > 180) or (Length(Path) < 4) or (Path[2] <> ':') or (Path = Root) or (GetDriveTypeW(Root) <> 3) then
    RaiseException('请选择本地磁盘上的专用目录，路径不超过180个字符。');
  if (Pos(Lowercase(AddBackslash(ExpandConstant('{win}'))), Lowercase(AddBackslash(Path))) = 1) or
     (Pos(Lowercase(AddBackslash(ExpandConstant('{commonpf}'))), Lowercase(AddBackslash(Path))) = 1) or
     (Pos(Lowercase(AddBackslash(ExpandConstant('{commonpf32}'))), Lowercase(AddBackslash(Path))) = 1) or
     (Pos(Lowercase(AddBackslash(ExpandConstant('{commonappdata}'))), Lowercase(AddBackslash(Path))) = 1) then
    RaiseException('仅当前用户安装，请选择系统目录之外的可写目录。');
  if (Pos(Lowercase(AddBackslash(DataDirectory)), Lowercase(AddBackslash(Path))) = 1) or
     (Pos(Lowercase(AddBackslash(Path)), Lowercase(AddBackslash(DataDirectory))) = 1) then
    RaiseException('程序与用户数据必须使用独立目录。');
#ifndef TestMode
  if (PreviousDirectory <> '') and (CompareText(Path, PreviousDirectory) <> 0) and not BrandDirectoryRename then
    RaiseException('升级必须沿用原安装位置。要更换目录，请先卸载程序并保留数据，再重新安装。');
#else
  if (PreviousDirectory <> '') and (CompareText(Path, PreviousDirectory) <> 0) and not BrandDirectoryRename then
    RaiseException('测试升级必须沿用原安装位置。');
  if GetEnv('LIGHTVIEW_DATA_DIR') = '' then
    RaiseException('测试安装器仅用于隔离验证，必须设置独立的LIGHTVIEW_DATA_DIR。请使用正式安装器。');
#endif
  if FileExists(ExpandConstant('{app}\portable')) then
    RaiseException('Remove the portable marker only after safely preserving its UserData; this installer will not change portable data.');
end;

function InitializeSetup: Boolean;
var Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040);
#ifdef TestMode
  if ExpandConstant('{param:TestFrameworkMissing|0}') = '1' then Result := False;
#endif
  if not Result then MsgBox('InfinityView 需要 Windows 11 自带的 .NET Framework 4.8 或更高版本。组件未就绪，未开始安装。请修复系统组件后重试。', mbError, MB_OK);
end;

procedure InitializeWizard;
begin
  PreviousDirectory := '';
#ifdef TestMode
  if ExpandConstant('{param:TestUsePrevious|0}') = '1' then begin
#endif
  RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#Identity}_is1', 'InstallLocation', PreviousDirectory);
  if PreviousDirectory <> '' then begin
    PreviousDirectory := RemoveBackslashUnlessRoot(ExpandFileName(PreviousDirectory));
    if CompareText(ExtractFileName(PreviousDirectory), 'LightView') = 0 then
      WizardForm.DirEdit.Text := AddBackslash(ExtractFileDir(PreviousDirectory)) + 'InfinityView'
    else WizardForm.DirEdit.Text := PreviousDirectory;
    WizardForm.DirEdit.Enabled := False;
    WizardForm.DirBrowseButton.Enabled := False;
  end;
#ifdef TestMode
  end;
#endif
  WizardForm.SelectDirLabel.Caption := '仅为当前用户安装 InfinityView。升级保留所在位置，旧 LightView 文件夹自动改名为 InfinityView。';
  WizardForm.SelectDirBrowseLabel.Caption := '安装程序的位置（与下载文件和用户数据目录不同）：';
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := '仅当前用户安装' + NewLine + MemoDirInfo + NewLine + NewLine +
    '用户数据：' + DataDirectory + NewLine + '照片保留原位置；不会自动启动或切换默认应用。' + NewLine +
    '卸载保留设置、日志和照片恢复区。';
end;

procedure CheckAvailable(Path: String);
var Handle: Integer;
begin
  if not FileExists(Path) then Exit;
  Handle := CreateFileW(Path, $80000000, 0, 0, 3, 0, 0);
  if Handle = -1 then RaiseException('A program file is in use or inaccessible. Close InfinityView normally and retry: ' + Path);
  CloseHandle(Handle);
end;

procedure CheckOldFiles;
var I: Integer;
begin
  if not LoadStringsFromFile(ExpandConstant('{app}\installed-files.txt'), OldFiles) then
    RaiseException('The existing installation manifest is missing. Preserve the old program and repair it before upgrading.');
  for I := 0 to GetArrayLength(OldFiles) - 1 do CheckAvailable(ChildPath(ExpandConstant('{app}'), OldFiles[I]));
end;

function RelativeAt(I: Integer): String;
begin Result := Copy(Integrity[I], 66, MaxInt); end;

procedure LoadIntegrity;
var I: Integer;
begin
  if not IntegrityExtracted then begin
    ExtractTemporaryFile('install-integrity.txt');
    IntegrityExtracted := True;
  end;
  if not LoadStringsFromFile(ExpandConstant('{tmp}\install-integrity.txt'), Integrity) then
    RaiseException('Package integrity manifest is unavailable.');
  SetArrayLength(IncomingExisted, GetArrayLength(Integrity));
  for I := 0 to GetArrayLength(Integrity) - 1 do begin
    if (Length(Integrity[I]) < 66) or (Integrity[I][65] <> '|') then RaiseException('Invalid integrity entry.');
    CheckAvailable(ChildPath(ExpandConstant('{app}'), RelativeAt(I)));
    if FileExists(ChildPath(ExpandConstant('{app}'), RelativeAt(I))) then IncomingExisted[I] := RelativeAt(I);
  end;
end;

procedure BackupDataFile(Name, BackupName: String);
var Source: String;
begin
  Source := Addbackslash(DataDirectory) + Name;
  if FileExists(Source) and not CopyFile(Source, BackupDirectory + '\' + BackupName, True) then
    RaiseException('Cannot preserve existing application data: ' + Name);
end;

procedure BackupOld;
var I: Integer; Source, Destination, Target: String; Shell, Shortcut: Variant;
begin
  BackupDirectory := AddBackslash(DataDirectory) + 'InstallBackups\' + GetDateTimeString('yyyymmdd-hhnnss-zzz', '-', ':');
  if DirExists(BackupDirectory) then RaiseException('Upgrade backup already exists. Retry later.');
  if not ForceDirectories(BackupDirectory) then RaiseException('Cannot create upgrade backup.');
  SetArrayLength(OldFileBackedUp, GetArrayLength(OldFiles));
  for I := 0 to GetArrayLength(OldFiles) - 1 do begin
    Source := ChildPath(ExpandConstant('{app}'), OldFiles[I]);
    if FileExists(Source) then begin
      Destination := ChildPath(BackupDirectory + '\program', OldFiles[I]);
      if not ForceDirectories(ExtractFileDir(Destination)) or not CopyFile(Source, Destination, True) then
        RaiseException('Cannot preserve the old program. Installation has not started.');
      OldFileBackedUp[I] := OldFiles[I];
    end;
  end;
  BackupDataFile('settings.json', 'settings.pre-upgrade.json');
  BackupDataFile('registration-owner.json', 'registration-owner.pre-upgrade.json');
  BackupDataFile('registration-pending.json', 'registration-pending.pre-upgrade.json');
  if ProgramDirectoryMoved then begin
    if not CopyFile(ExpandConstant('{app}\unins000.exe'), BackupDirectory + '\unins000.exe', True) or
       not CopyFile(ExpandConstant('{app}\unins000.dat'), BackupDirectory + '\unins000.dat', True) then
      RaiseException('Cannot preserve the previous uninstaller before directory migration.');
#ifndef TestMode
    Source := ExpandConstant('{userprograms}\InfinityView.lnk');
    if FileExists(Source) then begin
      Shell := CreateOleObject('WScript.Shell'); Shortcut := Shell.CreateShortcut(Source); Target := Shortcut.TargetPath;
      if (CompareText(ExpandFileName(Target), AddBackslash(PreviousDirectory) + 'InfinityView.exe') <> 0) and
         (CompareText(ExpandFileName(Target), AddBackslash(PreviousDirectory) + 'LightView.exe') <> 0) then
        RaiseException('Existing InfinityView shortcut belongs to another location; nothing was replaced.');
      if not CopyFile(Source, BackupDirectory + '\InfinityView.lnk', True) then RaiseException('Cannot preserve the old InfinityView shortcut.');
      HadInfinityShortcutBackup := True;
    end;
#endif
  end;
  Log('Old program and configuration backup: ' + BackupDirectory);
end;

procedure RestoreDirectoryNames;
begin
  // Never merge or remove either directory on rollback. Rename only to the
  // original, now absent sibling; an external collision is left for recovery.
  if ProgramDirectoryMoved then begin
    if not DirExists(PreviousDirectory) and not FileExists(PreviousDirectory) and
       RenameFile(ExpandConstant('{app}'), PreviousDirectory) then ProgramDirectoryMoved := False
    else Log('Directory restore incomplete; preserved program at: ' + ExpandConstant('{app}'));
  end;
  if DataDirectoryMoved then begin
    if not DirExists(LegacyDataPath) and not FileExists(LegacyDataPath) and
       RenameFile(DataDirectory, LegacyDataPath) then DataDirectoryMoved := False
    else Log('Directory restore incomplete; preserved data at: ' + DataDirectory);
  end;
end;

procedure MigrateDirectoryNames;
var Marker, Manifest: TArrayOfString; I: Integer;
begin
  LegacyDataPath := LegacyDataDirectory;
  if LegacyDataPath <> '' then begin
    CheckNoLinks(LegacyDataPath); CheckNoLinks(DataDirectory);
    if FileExists(LegacyDataPath) then RaiseException('旧数据路径不是文件夹，未迁移。');
    if DirExists(LegacyDataPath) and (DirExists(DataDirectory) or FileExists(DataDirectory)) then
      RaiseException('LightView 与 InfinityView 数据目录同时存在。为保护设置，未覆盖或合并，请先保留并整理这两个目录。');
  end;
  if BrandDirectoryRename then begin
    CheckNoLinks(PreviousDirectory);
    if DirExists(ExpandConstant('{app}')) or FileExists(ExpandConstant('{app}')) then
      RaiseException('InfinityView 安装目标已经存在，未覆盖。请先保留并整理该目录。');
    if FileExists(AddBackslash(PreviousDirectory) + 'portable') then
      RaiseException('便携副本不执行安装目录迁移。');
    if not LoadStringsFromFile(AddBackslash(PreviousDirectory) + '.lightview-install', Marker) then
      RaiseException('旧安装归属标记缺失，未迁移。');
    if (GetArrayLength(Marker) <> 1) or (Marker[0] <> '{#Identity}') then
      RaiseException('旧安装归属标记不匹配，未迁移。');
    ReadStrictUninstallState(OldUninstallState);
    if not LoadStringsFromFile(AddBackslash(PreviousDirectory) + 'installed-files.txt', Manifest) then
      RaiseException('旧安装清单缺失，未迁移。');
    for I := 0 to GetArrayLength(Manifest) - 1 do CheckAvailable(ChildPath(PreviousDirectory, Manifest[I]));
    CheckAvailable(AddBackslash(PreviousDirectory) + 'unins000.exe');
    CheckAvailable(AddBackslash(PreviousDirectory) + 'unins000.dat');
  end;
  if (LegacyDataPath <> '') and DirExists(LegacyDataPath) then begin
    if not RenameFile(LegacyDataPath, DataDirectory) then
      RaiseException('无法更名用户数据目录，请关闭占用它的程序后重试。原数据保留。');
    DataDirectoryMoved := True;
  end;
  if BrandDirectoryRename then begin
    if not RenameFile(PreviousDirectory, ExpandConstant('{app}')) then
      RaiseException('无法更名程序目录，请关闭占用它的程序后重试。');
    ProgramDirectoryMoved := True;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var FreeBytes, TotalBytes, RequiredBytes, OldBytes, Size: Int64; I, ProbeHandle: Integer; Source, Probe, Parent: String;
begin
  Result := '';
  try
    InitializeUninstallFields;
    CheckLocation;
    Log('Preflight: location accepted');
    if Running then RaiseException('InfinityView is running with this data directory. Close it normally, including old preview versions, then retry.');
    MigrateDirectoryNames;
    if HasFiles(ExpandConstant('{app}')) then begin
      if not OwnDirectory then RaiseException('This directory is not owned by the LightView installer. No files were replaced.');
      CheckOldFiles;
      ReadStrictUninstallState(OldUninstallState);
      HaveOldUninstallState := True;
    end else if RegKeyExists(HKCU, UninstallKey) then
      RaiseException('Existing uninstall metadata has no matching owned program directory; repair or uninstall it first.');
    Parent := ExpandConstant('{app}');
    while not DirExists(Parent) do Parent := ExtractFileDir(Parent);
    Probe := AddBackslash(Parent) + '.lightview-write-probe-' + GetDateTimeString('yyyymmddhhnnsszzz', '-', ':');
    ProbeHandle := CreateFileW(Probe, $40000000, 0, 0, 1, $100, 0);
    if ProbeHandle = -1 then RaiseException('安装目录不可写，请选择其他目录。');
    CloseHandle(ProbeHandle);
    if not DeleteFile(Probe) then RaiseException('无法清理目录写入探针，安装已停止。');
    Log('Preflight: writable directory accepted');
    OldBytes := 0;
    for I := 0 to GetArrayLength(OldFiles) - 1 do begin
      Source := ChildPath(ExpandConstant('{app}'), OldFiles[I]);
      if FileExists(Source) then begin
        if not FileSize64(Source, Size) then RaiseException('无法读取旧程序大小，安装已停止。');
        OldBytes := OldBytes + Size;
      end;
    end;
    RequiredBytes := {#PayloadBytes} + OldBytes + 67108864;
    Log('Preflight: calculating program disk space');
    if not GetSpaceOnDisk64(Parent, FreeBytes, TotalBytes) or (FreeBytes < RequiredBytes) then
      RaiseException('安装磁盘空间不足：需要程序、升级备份及至少64MiB余量。');
    Log('Preflight: calculating temporary disk space');
    if not GetSpaceOnDisk64(ExpandConstant('{tmp}'), FreeBytes, TotalBytes) or (FreeBytes < {#PayloadBytes} + 67108864) then
      RaiseException('临时目录磁盘空间不足，请保留解包空间后重试。');
    Log('Preflight: calculating backup disk space');
    Parent := DataDirectory;
    while not DirExists(Parent) do Parent := ExtractFileDir(Parent);
    if not GetSpaceOnDisk64(Parent, FreeBytes, TotalBytes) or (FreeBytes < OldBytes + 67108864) then
      RaiseException('用户数据磁盘空间不足，无法安全保存升级备份。');
    LoadIntegrity;
    if OwnDirectory then begin
      BackupOld;
      SaveUninstallState('uninstall-old.snapshot.txt', OldUninstallState);
    end;
  except
    Result := GetExceptionMessage;
    RestoreDirectoryNames;
    HaveOldUninstallState := False;
    BackupDirectory := '';
  end;
end;

procedure RegisterApplication(Argument: String);
#ifndef TestMode
var Code: Integer;
#endif
begin
#ifndef TestMode
  if not Exec(ExpandConstant('{app}\InfinityView.exe'), Argument, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code) then
    RaiseException('Could not start the InfinityView registration helper.');
  if Code <> 0 then RaiseException('InfinityView registration did not complete. Close any running instance and inspect the preserved program/configuration backup.');
#endif
end;

function IncomingHash(Relative: String): String;
var I: Integer;
begin
  Result := '';
  for I := 0 to GetArrayLength(Integrity) - 1 do
    if Lowercase(RelativeAt(I)) = Lowercase(Relative) then begin Result := Copy(Integrity[I], 1, 64); Exit; end;
end;

procedure RetireOldFiles;
var I: Integer; Source, Destination: String;
begin
  if BackupDirectory = '' then Exit;
  for I := 0 to GetArrayLength(OldFiles) - 1 do
    if (IncomingHash(OldFiles[I]) = '') and (CompareText(OldFiles[I], '.lightview-install') <> 0) then begin
      Source := ChildPath(BackupDirectory + '\program', OldFiles[I]);
      Destination := ChildPath(ExpandConstant('{app}'), OldFiles[I]);
      if FileExists(Destination) then begin
        if not FileExists(Source) then RaiseException('旧文件缺少备份，未清理：' + Destination);
        if GetSHA256OfFile(Source) <> GetSHA256OfFile(Destination) then RaiseException('旧文件被外部更改，未清理：' + Destination);
        CheckAvailable(Destination);
        if not DeleteFile(Destination) then RaiseException('无法清理已备份的旧程序文件：' + Destination);
      end;
    end;
end;

procedure RetireLegacyShortcut;
var Link, Target: String; Shell, Shortcut: Variant;
begin
  if BackupDirectory = '' then Exit;
  Link := ExpandConstant('{userprograms}\LightView.lnk');
  if not FileExists(Link) then Exit;
  try
    Shell := CreateOleObject('WScript.Shell'); Shortcut := Shell.CreateShortcut(Link); Target := Shortcut.TargetPath;
    if (CompareText(ExpandFileName(Target), ExpandConstant('{app}\LightView.exe')) <> 0) and
       not (BrandDirectoryRename and (CompareText(ExpandFileName(Target), AddBackslash(PreviousDirectory) + 'LightView.exe') = 0)) then Exit;
    if CopyFile(Link, BackupDirectory + '\LightView.lnk', False) then
      if not DeleteFile(Link) then Log('Legacy owned shortcut could not be retired; preserved for manual cleanup.');
  except Log('Legacy shortcut ownership could not be verified; left unchanged.'); end;
end;

procedure CurStepChanged(Step: TSetupStep);
var I: Integer; Path: String; CurrentUninstallState: TArrayOfString;
begin
  if Step = ssInstall then begin
    if Running then begin
      SuppressibleMsgBox('InfinityView started during preparation. Close it normally before installing.', mbError, MB_OK, IDOK);
      Abort;
    end;
    if HaveOldUninstallState then begin
#ifdef TestMode
      if ExpandConstant('{param:TestPreparedMetadataUnknown|0}') = '1' then
        RegWriteStringValue(HKCU, UninstallKey, 'SyntheticPreparedValue', 'retain preparation-time external change');
#endif
      ReadStrictUninstallState(CurrentUninstallState);
      for I := 0 to 19 do if CurrentUninstallState[I] <> OldUninstallState[I] then
        RaiseException('Uninstall metadata changed during preparation; no program files were copied.');
    end;
    CopyStarted := True;
  end;
  if Step = ssPostInstall then begin
    try
    if HaveOldUninstallState then begin
#ifdef TestMode
    if ExpandConstant('{param:TestMissingOldBackup|0}') = '1' then
      if not RenameFile(BackupDirectory + '\program\InfinityView.exe', BackupDirectory + '\program\InfinityView.exe.test-retained') then RaiseException('Cannot create isolated-test missing-source fixture.');
      if ExpandConstant('{param:TestFailMetadataCapture|0}') = '1' then RaiseException('Injected isolated-test metadata write-state capture failure.');
#endif
      ReadStrictUninstallState(NewUninstallState);
      SaveUninstallState('uninstall-new.snapshot.txt', NewUninstallState);
      HaveNewUninstallState := True;
    end;
#ifdef TestMode
    if ExpandConstant('{param:TestMetadataConflict|0}') = '1' then begin
      RegWriteStringValue(HKCU, UninstallKey, 'Publisher', 'synthetic external publisher change');
      RegWriteStringValue(HKCU, UninstallKey, 'SyntheticLateValue', 'retain external value');
    end;
    if ExpandConstant('{param:TestMetadataOwnerConflict|0}') = '1' then
      RegWriteStringValue(HKCU, UninstallKey, 'InstallLocation', 'synthetic external location');
#endif
    for I := 0 to GetArrayLength(Integrity) - 1 do begin
      Path := ChildPath(ExpandConstant('{app}'), RelativeAt(I));
      if Lowercase(GetSHA256OfFile(Path)) <> Lowercase(Copy(Integrity[I], 1, 64)) then RaiseException('Installed file integrity check failed.');
    end;
#ifdef TestMode
    if ExpandConstant('{param:TestFailCommit|0}') = '1' then RaiseException('Injected isolated-test commit failure.');
#endif
    RetireOldFiles;
#ifdef TestMode
    if ExpandConstant('{param:TestProgramRestoreConflict|0}') = '1' then
      if not SaveStringToFile(ExpandConstant('{app}\LightView.dll'), 'synthetic external old file change after retirement', False) then RaiseException('Cannot create isolated-test conflict.');
    if ExpandConstant('{param:TestFailAfterRetire|0}') = '1' then RaiseException('Injected isolated-test failure after old payload retirement.');
#endif
    RegisterApplication('--register-install');
    Successful := True;
    RetireLegacyShortcut;
    except
      FailureMessage := GetExceptionMessage;
      Log('Installation incomplete: ' + FailureMessage);
      SuppressibleMsgBox(FailureMessage + #13#10 + 'Installation did not complete. Preserve the program/configuration backup; run Setup again to repair.', mbError, MB_OK, IDOK);
    end;
  end;
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := 0;
  if CopyStarted and not Successful then Result := 1;
end;

procedure CurPageChanged(Page: Integer);
begin
  if (Page = wpFinished) and Successful then begin
    WizardForm.FinishedLabel.Height := ScaleY(180);
    WizardForm.RunList.Top := WizardForm.FinishedLabel.Top + WizardForm.FinishedLabel.Height + ScaleY(8);
    WizardForm.RunList.Height := ScaleY(80);
    WizardForm.FinishedLabel.Caption := '已安装到：' + ExpandConstant('{app}') + #13#10 + #13#10 +
      '用户数据：' + DataDirectory + #13#10 + '可从开始菜单启动。默认图片应用请在Windows设置中自行选择。';
#ifdef TestMode
    WizardForm.FinishedLabel.Caption := '已安装测试副本：' + ExpandConstant('{app}') + #13#10 + #13#10 +
      '测试数据：' + DataDirectory + #13#10 + '不创建开始菜单或默认关联。日常使用请选择正式安装包。';
#endif
  end;
  if (Page = wpFinished) and CopyStarted and not Successful then begin
    WizardForm.FinishedHeadingLabel.Caption := 'InfinityView installation incomplete';
    WizardForm.FinishedLabel.Caption := FailureMessage + #13#10 +
      'The program/configuration backup is retained. Run Setup again to repair. This is not a completed installation.';
  end;
end;

procedure DeinitializeSetup;
var I: Integer; Source, Destination, Expected: String; ProgramRestored: Boolean;
begin
  if Successful then Exit;
  if not CopyStarted then begin RestoreDirectoryNames; Exit; end;
  ProgramRestored := True;
  try
    if Running then begin Log('A LightView instance is running. Backup retained; automatic program restore skipped.'); Exit; end;
    if BackupDirectory <> '' then
      for I := 0 to GetArrayLength(OldFiles) - 1 do begin
        Source := ChildPath(BackupDirectory + '\program', OldFiles[I]);
        Destination := ChildPath(ExpandConstant('{app}'), OldFiles[I]);
        Expected := IncomingHash(OldFiles[I]);
        if FileExists(Source) then begin
          if FileExists(Destination) then begin
            if Lowercase(GetSHA256OfFile(Destination)) = Lowercase(GetSHA256OfFile(Source)) then Continue;
            if (Expected = '') or (Lowercase(GetSHA256OfFile(Destination)) <> Lowercase(Expected)) then begin
              ProgramRestored := False; Log('External change preserved; restore skipped: ' + Destination); Continue;
            end;
          end;
          CheckAvailable(Destination);
          if not ForceDirectories(ExtractFileDir(Destination)) or not CopyFile(Source, Destination, False) then begin
            ProgramRestored := False; Log('Restore incomplete; old usable program remains in: ' + BackupDirectory);
          end;
        end else if (I < GetArrayLength(OldFileBackedUp)) and (OldFileBackedUp[I] <> '') then begin
          ProgramRestored := False; Log('Restore incomplete; expected old backup source is missing: ' + Source);
        end;
      end;
    // A failed fresh installation keeps its owned manifest and complete payload
    // available for repair/uninstall; it is reported as incomplete, never success.
    if BackupDirectory <> '' then for I := 0 to GetArrayLength(Integrity) - 1 do
      if IncomingExisted[I] = '' then begin
        Destination := ChildPath(ExpandConstant('{app}'), RelativeAt(I));
        if FileExists(Destination) then
          if Lowercase(GetSHA256OfFile(Destination)) = Lowercase(Copy(Integrity[I], 1, 64)) then begin
            if not DeleteFile(Destination) then ProgramRestored := False;
          end else ProgramRestored := False;
      end;
  except ProgramRestored := False; Log('Automatic program restore stopped; preserve the backup: ' + GetExceptionMessage); end;
  try RestoreUninstallState(ProgramRestored);
  except MetadataRollbackStatus('UNINSTALL METADATA RESTORE INCOMPLETE: ' + GetExceptionMessage); end;
  if ProgramDirectoryMoved and not MetadataRestored then begin
    Log('Directory migration retained at its current location because metadata restore is incomplete.'); Exit;
  end;
  if ProgramRestored and ProgramDirectoryMoved then begin
    if (BackupDirectory <> '') and FileExists(BackupDirectory + '\unins000.exe') and FileExists(BackupDirectory + '\unins000.dat') then begin
      if not CopyFile(BackupDirectory + '\unins000.exe', ExpandConstant('{app}\unins000.exe'), False) or
         not CopyFile(BackupDirectory + '\unins000.dat', ExpandConstant('{app}\unins000.dat'), False) then begin
        Log('Previous uninstaller could not be restored; directory migration backup retained.'); Exit;
      end;
    end else begin Log('Previous uninstaller backup missing; directory migration backup retained.'); Exit; end;
#ifndef TestMode
    if HadInfinityShortcutBackup then begin
      if not CopyFile(BackupDirectory + '\InfinityView.lnk', ExpandConstant('{userprograms}\InfinityView.lnk'), False) then
        Log('Old InfinityView shortcut could not be restored; backup retained.');
    end else if FileExists(ExpandConstant('{userprograms}\InfinityView.lnk')) then
      DeleteFile(ExpandConstant('{userprograms}\InfinityView.lnk'));
#endif
  end;
  if ProgramRestored then RestoreDirectoryNames;
end;

function InitializeUninstall: Boolean;
begin
  Result := False;
  try
    CheckLocation;
    if not OwnDirectory then RaiseException('Installation ownership marker is missing. No files were removed.');
    if Running then RaiseException('Close InfinityView normally before uninstalling.');
    CheckOldFiles;
    Result := True;
  except SuppressibleMsgBox(GetExceptionMessage, mbError, MB_OK, IDOK); end;
end;

procedure CurUninstallStepChanged(Step: TUninstallStep);
begin
  if Step = usUninstall then begin
    try
    if Running then RaiseException('InfinityView is running. Uninstall stopped.');
#ifdef TestMode
    if ExpandConstant('{param:TestFailUnregister|0}') = '1' then RaiseException('Injected isolated-test unregister failure.');
#endif
    RegisterApplication('--unregister-install');
    except
      SuppressibleMsgBox(GetExceptionMessage + #13#10 + 'Uninstall stopped before deleting program files.', mbError, MB_OK, IDOK);
      Abort;
    end;
  end;
end;
