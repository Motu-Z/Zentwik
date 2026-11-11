; Zentwik 安装包脚本（由 release.yml 的 Build installer 步骤调用，版本通过 /DMyAppVersion 传入）
#define MyAppName "Zentwik"
#define MyAppExeName "Zentwik.UI.exe"
#define WorkerName "Zentwik.Worker.exe"
#define AutoStartTask "ZentwikAutoStart"

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

; 同一份脚本产出两个安装包，用 /DLite 切换：
;   完整版（自带 .NET 10，默认）
;     ISCC /DMyAppVersion=x installer\zentwik.iss
;   精简版（负载换成 pkg-portable，需预装 .NET 10 桌面运行时）
;     ISCC /DMyAppVersion=x /DLite installer\zentwik.iss
;
; 为什么用开关而不是 /D 传路径：ISCC 的 /D 值不带引号时会被当表达式解析，
; ..\pkg-portable 这种含反斜杠的值直接报错；而 PowerShell 5.1 往原生 exe
; 传带内层引号的参数又常常被吞掉。开关只定义一个符号，两个坑都绕开。
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

; 单文件运行时会把自己解包到临时目录，且每个版本各建一个 hash 子目录，从不回收。
; 升级几次就会攒出几百 MB。这里整棵删掉。
;
; 两条都写：{tmp} 在多数机器上等于 {localappdata}\Temp，但 TEMP 环境变量被改过时
; 两者会分叉。删不存在的路径 Inno 会静默跳过，重复写没有副作用。
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

{ 程序文件删完之后再问配置。
  放在 usPostUninstall 而不是 usUninstall：卸载中途失败时不会先把用户数据删掉。
  默认按钮是「否」——重装还能沿用掩码与方案，想彻底清干净就选「是」。 }
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
{ 精简版不自带运行时。装完才发现缺 .NET 是最差的体验 —— 双击没反应、还要自己查日志，
  所以开头就拦一下，并且把「该装哪个包」直接说清楚。

  只认 Desktop 运行时（Microsoft.WindowsDesktop.App）：只装了 NETCore.App 的机器
  连 WPF 的 PresentationFramework 都没有，照样起不来。

  这里有两个容易踩的坑：
  1. 版本号是那个键下的**值名**（10.0.12 这种），不是子键。用 RegGetSubkeyNames
     去找子键，拿到的永远是空数组 —— 装了也会被判成没装。
  2. 键不保证落在 64 位视图里。有些机器（本机就是一例）只在 WOW6432Node 下有，
     所以两个视图都要查。

  最后再兜底看 Program Files 下的实际目录：注册表被清理过、或运行时是按用户装的，
  键可能查不到但程序照样能跑，这时不该拦人。 }
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
