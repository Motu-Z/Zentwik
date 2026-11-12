; Zentwik 安装包脚本（由 release.yml 的 Build installer 步骤调用，版本通过 /DMyAppVersion 传入）
#define MyAppName "Zentwik"
#define MyAppExeName "Zentwik.UI.exe"
#define WorkerName "Zentwik.Worker.exe"
#define AutoStartTask "ZentwikAutoStart"

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

; /DLite 切精简版（负载 pkg-portable，需预装 .NET 10 桌面运行时）；缺省为完整版。
; 用开关而非 /D 传路径：含反斜杠的值会被 ISCC 当表达式解析。
#ifdef Lite
  #define PayloadDir "..\pkg-portable"
  #define OutputSuffix "-Lite"
  #define RequireRuntime
#else
  #define PayloadDir "..\pkg-standalone"
  #define OutputSuffix ""
#endif

[Setup]
AppId={{4C1E7A52-8B3D-49F6-9E24-6D7B2F8C1A35}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Motu
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
OutputDir=..\dist
OutputBaseFilename=Zentwik-{#MyAppVersion}-Setup{#OutputSuffix}
SetupIconFile=..\Zentwik.UI\Assets\Zentwik.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#MyAppExeName}"; Flags: runhidden; RunOnceId: "KillUI"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#WorkerName}"; Flags: runhidden; RunOnceId: "KillWorker"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#AutoStartTask}"" /F"; Flags: runhidden; RunOnceId: "DelAutoStart"

; 单文件运行时按版本解包到临时目录且从不回收，卸载时整棵删除。
; {tmp} 与 {localappdata}\Temp 可能分叉，两条都写。
[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Temp\.net\Zentwik.UI"
Type: filesandordirs; Name: "{tmp}\.net\Zentwik.UI"

[Code]
procedure KillProcesses();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#WorkerName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillProcesses();
  Result := '';
end;

{ 程序文件删完后再问配置，默认「否」。用 usPostUninstall 以免卸载中途失败时先删用户数据。 }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{userappdata}\{#MyAppName}');
    if DirExists(DataDir) then
    begin
      if MsgBox('是否同时删除 Zentwik 的配置与日志？' + #13#10 + #13#10 + DataDir,
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;

#ifdef RequireRuntime
{ 精简版不自带运行时，缺了双击没反应，开头就拦。
  只认 Microsoft.WindowsDesktop.App（光有 NETCore.App 起不来 WPF）。
  版本号是键下的值名而非子键；键可能在 WOW6432Node，两个视图都查。
  最后兜底看 Program Files 实际目录，注册表查不到但能跑的机器不该被拦。 }
function HasNet10DesktopValue(RootKey: Integer): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(RootKey,
       'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App',
       Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Pos('10.', Names[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;
end;

function HasNet10DesktopFolder(): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec) then
  try
    Result := (FindRec.Attributes and $00000010) <> 0;   { 只认目录 }
  finally
    FindClose(FindRec);
  end;
end;

function IsNet10DesktopInstalled(): Boolean;
begin
  Result := HasNet10DesktopValue(HKLM64) or
            HasNet10DesktopValue(HKLM32) or
            HasNet10DesktopFolder();
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsNet10DesktopInstalled() then
  begin
    if MsgBox('未检测到 .NET 10 桌面运行时。' + #13#10 + #13#10 +
              '这个安装包不自带运行时，缺少它将无法启动。' + #13#10 +
              '建议改用完整安装包，或先安装 .NET 10 Desktop Runtime。' + #13#10 + #13#10 +
              '仍要继续安装吗？',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDNO then
      Result := False;
  end;
end;
#endif
