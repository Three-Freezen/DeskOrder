# DeskOrder 打包脚本：dotnet publish → vpk pack 产出 Velopack 安装包 + 更新包。
# 产物在仓库根 releases\ 下：Setup.exe（分发用）+ *.nupkg（增量更新用，全部要传上 Release）。
#
# 首次使用先装 CLI（一次性）: dotnet tool install -g vpk
#
# 用法:
#   ./tools/pack.ps1                 # 用 csproj <Version> 打包到本地
#   ./tools/pack.ps1 -Version 0.9.1  # 覆盖版本号
#   ./tools/pack.ps1 -Push           # 打包并发布 GitHub Release（需 GITHUB_TOKEN 环境变量）
#
# 注意: Push 需要 repo 权限的 GITHUB_TOKEN 环境变量。
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

Write-Host "==> vpk pack"
# dotnet tool 刚装完时当前会话 PATH 可能还没带上 ~/.dotnet/tools，这里兜底解析
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    $vpkLocal = Join-Path $env:USERPROFILE ".dotnet\tools\vpk.exe"
    if (Test-Path $vpkLocal) { Set-Alias vpk $vpkLocal }
    else { throw "未找到 vpk，请先执行: dotnet tool install -g vpk" }
}
vpk pack -u DeskOrder -v $Version -p "$root\publish" -o "$root\releases"
if ($LASTEXITCODE -ne 0) { exit 1 }

if ($Push) {
    if (-not $env:GITHUB_TOKEN) { throw "Push 需要 GITHUB_TOKEN 环境变量（repo 权限的 PAT）" }
    Write-Host "==> vpk upload github"
    # vpk 1.2.0 中 GitHub 上传是 `upload github` 子命令(publish 是官方托管服务)。
    vpk upload github --repoUrl "https://github.com/Three-Freezen/DeskOrder" `
        --token $env:GITHUB_TOKEN --releaseName "v$Version" --tag "v$Version" --publish True --merge True
    if ($LASTEXITCODE -ne 0) { exit 1 }
}

Write-Host "==> 完成。产物: $root\releases"
