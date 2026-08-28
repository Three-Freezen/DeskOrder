; DeskOrder 安装脚本（Inno Setup 6）
; 调用方式: ISCC.exe tools\DeskOrder.iss /DAppVersion=x.y.z
; 产物: releases\DeskOrder-win-Setup.exe —— 文件名固定不带版本号，
;       配合 GitHub releases/latest/download/ 链接永远拿到最新版。
; 交互安装: 用户可选路径（默认 %LocalAppData%\Programs\DeskOrder，无需管理员），
;           目录自动创建；静默升级: /SILENT /DIR=<原路径>，装完自动重启应用。

#define MyAppName "DeskOrder"
#define MyAppPublisher "Three Freezen"
#ifndef AppVersion
#define AppVersion "0.0.0"
#endif

[Setup]
AppId={{8D6B4B39-6A21-4E7A-9C33-2D0F1A55E7B1}}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher={#MyAppPublisher}
WizardStyle=modern
; 免管理员：装到用户目录（VS Code 用户版同款位置），路径在向导里可改
PrivilegesRequired=lowest
DefaultDirName={userpf}\{#MyAppName}
DirExistsWarning=no
DisableProgramGroupPage=yes
OutputDir=..\releases
OutputBaseFilename=DeskOrder-win-Setup
Compression=lzma2/max
SolidCompression=yes
; 升级时等待旧实例退出（我们的单实例互斥体名），再覆盖文件
CloseApplications=yes
AppMutex=DeskOrder_SingleInstance
SetupIconFile=..\Resources\Icons\DesktopZones.ico
UninstallDisplayIcon={app}\Resources\Icons\DesktopZones.ico

[Languages]
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; \
    GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; \
    Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\DeskOrder.exe"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\DeskOrder.exe"; \
    Tasks: desktopicon

[Run]
; 正常安装：向导最后的“立即运行”勾选项（静默升级时跳过）
Filename: "{app}\DeskOrder.exe"; Description: "{cm:LaunchProgram,{#MyAppName}}"; \
    Flags: nowait postinstall skipifsilent
; 静默升级（应用内更新）完成后自动重启应用
Filename: "{app}\DeskOrder.exe"; Flags: nowait runasoriginaluser; Check: SilentUpgrade

[Code]
// Check: 参数只能引用 [Code] 里自定义的函数，这里包一层内置的 WizardSilent。
function SilentUpgrade: Boolean;
begin
  Result := WizardSilent;
end;

// 同一 AppId 已安装过（升级/重装）时，先静默卸载旧版本，避免
// “电脑中已存在该软件”弹窗打断 /SILENT 的应用内升级流程。
// 用户数据在 %APPDATA%\DesktopZones，不受卸载影响。
function InitializeSetup(): Boolean;
var
  Uninst: String;
  Res: Integer;
begin
  Result := True;
  if RegQueryStringValue(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{8D6B4B39-6A21-4E7A-9C33-2D0F1A55E7B1}_is1',
      'UninstallString', Uninst) then
  begin
    Uninst := RemoveQuotes(Uninst);
    Exec(Uninst, '/SILENT /NORESTART', '', SW_SHOW, ewWaitUntilTerminated, Res);
  end;
end;
