; Zentwik Next 安装包脚本
; 由 scripts/build-installer.ps1 调用，版本通过 /DMyAppVersion 传入。
;
; 设计取舍：安装器只做三件事——放文件、停服务、调 Agent 自己的 --install-service。
; 服务的创建/升级/删除逻辑只存在于 C#（src/Zentwik.Agent/Service/ServiceInstaller.cs），
; 安装器不重复实现一份，换打包器时服务行为不变。

#define MyAppName "Zentwik Next"
#define MyAppShortName "Zentwik"
#define MyAppPublisher "Motu"
#define AgentExe "Zentwik.Agent.exe"
#define CliExe "zentwik.exe"
#define DataDirName "ZentwikNext"

; 与旧版 Zentwik 使用不同 AppId：两者可以并存，互不覆盖。
#define MyAppId "{{6F3A9C21-7D84-4B16-9E52-3C0D8A5F1B47}"

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#ifndef MyAppVersionNumeric
  #define MyAppVersionNumeric "0.0.0.0"
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersionNumeric}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppShortName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir=..\..\dist
OutputBaseFilename=Zentwik-Next-{#MyAppVersion}-Setup
UninstallDisplayIcon={app}\{#CliExe}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
; 默认不勾选：功耗墙绕出厂限制，必须由用户显式选择。
Name: "powerlimits"; Description: "启用硬件功耗墙（AMD PPT / TDC / EDC 写入）"; GroupDescription: "可选组件："; Flags: unchecked

[Files]
; 调试符号不进安装包：崩溃栈由 WER 与日志覆盖，pdb 只会让包大一半。
Source: "..\..\pkg\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; 早期手工安装把服务放在 {app}\Agent；新版平铺在 {app}。从那种状态升级时清掉旧目录。
Type: filesandordirs; Name: "{app}\Agent"

[Icons]
Name: "{group}\命令行（管理员）"; Filename: "{cmd}"; Parameters: "/k cd /d ""{app}"""; WorkingDir: "{app}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"

[UninstallRun]
; 必须在删文件之前停掉并删除服务，否则服务会指向已被删除的可执行文件。
Filename: "{app}\{#AgentExe}"; Parameters: "--uninstall-service"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"

[Code]
function PawnIoPresent(): Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\PawnIO');
end;

{ 升级时旧服务仍在运行，会锁住 exe；先停再让 [Files] 覆盖。首次安装时目录还不存在，跳过。 }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  AgentPath: String;
begin
  Result := '';
  AgentPath := ExpandConstant('{app}\{#AgentExe}');
  if FileExists(AgentPath) then
  begin
    Exec(AgentPath, '--stop-service', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Params: String;
  AgentPath: String;
  Exited: Boolean;
begin
  if CurStep <> ssPostInstall then
    Exit;

  AgentPath := ExpandConstant('{app}\{#AgentExe}');
  Params := '--install-service --data-dir "' + ExpandConstant('{commonappdata}\{#DataDirName}') + '"';
  if WizardIsTaskSelected('powerlimits') then
    Params := Params + ' --enable-module power-limits';

  Exited := Exec(AgentPath, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if (not Exited) or (ResultCode <> 0) then
  begin
    MsgBox('服务安装未完成（退出码 ' + IntToStr(ResultCode) + '）。' + #13#10 + #13#10 +
           '文件已就位，可在管理员命令行执行：' + #13#10 +
           '"' + AgentPath + '" --install-service', mbError, MB_OK);
  end
  else if not PawnIoPresent() then
  begin
    MsgBox('未检测到 PawnIO 驱动。' + #13#10 + #13#10 +
           'CPU 调校与硬件功耗墙需要它，其余功能不受影响。' + #13#10 +
           '装好驱动后执行 zentwik modules 可查看各模块状态。', mbInformation, MB_OK);
  end;
end;

{ 数据目录在 ProgramData，删完文件再问；默认「否」。 }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{commonappdata}\{#DataDirName}');
    if DirExists(DataDir) then
    begin
      if MsgBox('是否同时删除配置、日志与电源方案备份？' + #13#10 + #13#10 + DataDir,
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
