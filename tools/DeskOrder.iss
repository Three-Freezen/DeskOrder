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
// ── ponytail 2026-08-29: 应用数据保存位置选择 ──
// 全新交互安装时,向导在目录页之后让用户二选一:
//   • 系统 AppData(推荐) — %APPDATA%\DesktopZones,更新/卸载不触碰,权限最稳;
//   • 软件安装文件夹(便携模式) — 安装目录\Data,数据随软件目录整体移动/拷贝。
// 选便携 → ssPostInstall 写 安装目录\Data\portable.flag;应用端 DataLocator 据此
// 落点并自动把 AppData 里的既有用户数据(config/Notes/Presets/lang)搬进 Data。
// 升级(交互或 /SILENT)跳过本页且不触碰 Data —— 模式一经选择跨升级保持。
// Data 目录不在 [Files] 清单内,卸载时 Inno 不会删除(用户数据保留)。
var
  DataPage: TInputOptionWizardPage;
  IsUpgradeInstall: Boolean;

// Check: 参数只能引用 [Code] 里自定义的函数，这里包一层内置的 WizardSilent。
function SilentUpgrade: Boolean;
begin
  Result := WizardSilent;
end;

// 同一 AppId 已安装过（升级/重装）时，先静默卸载旧版本，避免
// “电脑中已存在该软件”弹窗打断 /SILENT 的应用内升级流程。
// 用户数据不受卸载影响:标准模式在 %APPDATA%\DesktopZones,便携模式在 安装目录\Data。
function InitializeSetup(): Boolean;
var
  Uninst: String;
  Res: Integer;
begin
  Result := True;
  IsUpgradeInstall := False;
  if RegQueryStringValue(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{8D6B4B39-6A21-4E7A-9C33-2D0F1A55E7B1}_is1',
      'UninstallString', Uninst) then
  begin
    IsUpgradeInstall := True;
    Uninst := RemoveQuotes(Uninst);
    Exec(Uninst, '/SILENT /NORESTART', '', SW_SHOW, ewWaitUntilTerminated, Res);
  end;
end;

procedure InitializeWizard();
begin
  DataPage := CreateInputOptionPage(wpSelectDir,
      '应用数据保存位置', '选择 DeskOrder 用户数据(设置/分区/预设/便签)的保存位置。',
      '系统 AppData 为推荐选项。切换为便携模式后,数据会自动迁移到所选文件夹。', True, False);
  DataPage.Add('系统 AppData(推荐)— %APPDATA%\DesktopZones');
  DataPage.Add('软件安装文件夹(便携模式)— 安装目录\Data,可随软件目录整体移动');
  DataPage.Values[0] := True;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  // 升级/静默安装跳过选择页 — 维持既有数据位置(便携标记不被触碰)。
  if DataPage <> nil then
    if PageID = DataPage.ID then
      Result := IsUpgradeInstall or WizardSilent;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  FlagPath: String;
begin
  if CurStep <> ssPostInstall then Exit;
  FlagPath := ExpandConstant('{app}\Data\portable.flag');
  // 仅全新交互安装处理模式切换;升级/静默安装永不触碰 Data(模式保持)。
  if (not IsUpgradeInstall) and (not WizardSilent) then
  begin
    if DataPage.Values[1] then
    begin
      // 便携模式:创建 Data 并写标记 — 应用首启自动迁移 AppData 既有数据。
      if not ForceDirectories(ExpandConstant('{app}\Data')) then
        MsgBox('创建便携数据文件夹失败,应用将改用系统 AppData 保存数据。',
            mbCriticalError, MB_OK);
      SaveStringToFile(FlagPath, 'portable', False);
    end
    else if FileExists(FlagPath) then
    begin
      // 全新安装选回标准模式:清掉可能残留的旧标记(如上一任便携安装的遗留)。
      DeleteFile(FlagPath);
    end;
  end;
end;
