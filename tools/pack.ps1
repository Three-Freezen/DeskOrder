# DeskOrder 打包脚本：dotnet publish → 便携 zip → Inno Setup 编译安装包。
# 产物 releases\ 下：
#   DeskOrder-win-Setup.exe   —— 文件名固定不带版本号，配合 GitHub
#                                releases/latest/download/ 链接永远拿到最新版
#   DeskOrder-win-Portable.zip —— 便携版
# 依赖: Inno Setup 6 (ISCC.exe)；Push 需要 gh CLI 已登录（gh auth login）。
#
# 用法:
#   ./tools/pack.ps1                 # 用 csproj <Version> 打包到本地
#   ./tools/pack.ps1 -Version 0.9.2  # 覆盖版本号
#   ./tools/pack.ps1 -Push           # 打包并发布 GitHub Release（gh 已登录）
param(
    [string]$Version = "",
    [switch]$Push
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot   # 仓库根（= 本 csproj 所在目录）

if (-not $Version) {
    # 单一版本来源：读 csproj 的 <Version>（文件含中文注释，必须显式 UTF-8）
    [xml]$csproj = Get-Content (Join-Path $root "DesktopZones.csproj") -Raw -Encoding UTF8
    $Version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
    if (-not $Version) { throw "无法从 DesktopZones.csproj 读取 <Version>，请用 -Version 指定" }
}
Write-Host "==> Version: $Version"

Write-Host "==> dotnet publish (win-x64, self-contained)"
dotnet publish "$root\DesktopZones.csproj" -c Release -r win-x64 --self-contained -o "$root\publish"
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "==> 便携版 zip"
New-Item -ItemType Directory -Force -Path "$root\releases" | Out-Null
Compress-Archive -Path "$root\publish\*" -DestinationPath "$root\releases\DeskOrder-win-Portable.zip" -Force

Write-Host "==> Inno Setup 编译安装包"
$iscc = @("C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
          "C:\Program Files\Inno Setup 6\ISCC.exe",
          "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "未找到 ISCC.exe，请先安装 Inno Setup 6: winget install JRSoftware.InnoSetup" }
& $iscc /DAppVersion=$Version "$root\tools\DeskOrder.iss"
if ($LASTEXITCODE -ne 0) { exit 1 }

if ($Push) {
    Write-Host "==> 发布 GitHub Release"
    # tag 需已推送（git tag v$Version && git push origin v$Version）
    gh release create "v$Version" --title "v$Version" --generate-notes `
        "$root\releases\DeskOrder-win-Setup.exe" `
        "$root\releases\DeskOrder-win-Portable.zip"
    if ($LASTEXITCODE -ne 0) { exit 1 }
}

Write-Host "==> 完成。产物: $root\releases"
