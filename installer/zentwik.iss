; Zentwik 安装包脚本（由 installer.yml 调用，版本通过 /DMyAppVersion 传入）
#define MyAppName "Zentwik"
#define MyAppExeName "Zentwik.UI.exe"
#define WorkerName "Zentwik.Worker.exe"
#define AutoStartTask "ZentwikAutoStart"

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
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
OutputBaseFilename=Zentwik-{#MyAppVersion}-Setup
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
Source: "..\pkg-standalone\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

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
