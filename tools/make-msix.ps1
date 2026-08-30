<#
.SYNOPSIS
  DeskOrder MSIX 打包:发布 → 暂存(清单+资产) → MakeAppx 压包 → 可选自签。

.DESCRIPTION
  CI(release.yml)产物为未签名 MSIX artifact。上架微软商店时,把
  -IdentityName / -Publisher 换成 Partner Center "程序包管理 → 标识详细信息"
  给出的值重新打包即可(商店上传不要求预签名,商店会自行重签)。
  本机侧载测试加 -SignSideload:自动生成自签证书并签名,按输出提示导入证书。
  注意:全脚本不使用反引号续行(PS 5.1 对 LF 行尾 + 续行反引号会解析炸)。

.EXAMPLE
  tools/make-msix.ps1 -SignSideload
  tools/make-msix.ps1 -PublishDir publish -IdentityName 47092ThreeFreezen.DeskOrder -Publisher "CN=E0C3B..."
#>
param(
    [string]$ProjectFile = (Join-Path $PSScriptRoot "..\DesktopZones.csproj"),
    [string]$PublishDir = "",                            # 已有 dotnet publish 输出目录;空则自行发布到 obj\msix-publish
    [string]$OutDir = (Join-Path $PSScriptRoot "..\releases"),
    [string]$IdentityName = "ThreeFreezen.DeskOrder",    # TODO: Partner Center → Package identity → Name
    [string]$Publisher = "CN=Three-Freeze",              # TODO: Partner Center → Package identity → Publisher
    [string]$Version = "",                               # 空 = 读 csproj <Version>,不足四段补 .0
    [switch]$SignSideload                                # 生成自签证书并签名,供本机侧载
)
$ErrorActionPreference = "Stop"

# ---- 版本号:清单 Identity Version 必须四段(1.0.7 → 1.0.7.0)
if (-not $Version) {
    $csprojText = Get-Content $ProjectFile -Raw
    if ($csprojText -match '<Version>([0-9]+(?:\.[0-9]+){1,3})</Version>') { $Version = $Matches[1] }
    else { throw "无法从 $ProjectFile 读取 <Version>,请用 -Version 显式指定" }
}
$segs = $Version.Split('.')
while ($segs.Count -lt 4) { $segs += "0" }
$Version = ($segs[0..3] -join '.')

# ---- 发布输出
$ProjectFile = (Resolve-Path $ProjectFile).Path
if ($PublishDir) {
    $PublishDir = (Resolve-Path $PublishDir).Path
} else {
    $PublishDir = Join-Path (Split-Path $ProjectFile -Parent) "obj\msix-publish"
    if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
    dotnet publish $ProjectFile -c Release -r win-x64 --self-contained -o $PublishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }
}
if (-not (Test-Path (Join-Path $PublishDir "DeskOrder.exe"))) { throw "发布输出里没有 DeskOrder.exe: $PublishDir" }

# ---- Windows SDK 工具(makeappx / signtool)
$kits = "C:\Program Files (x86)\Windows Kits\10\bin"
if (-not (Test-Path $kits)) { throw "未找到 Windows Kits: $kits(需要 Windows SDK 提供 makeappx.exe)" }
$sdkDir = Get-ChildItem $kits -Directory | Where-Object { $_.Name -match '^\d' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$makeappx = Join-Path $sdkDir.FullName "x64\makeappx.exe"
$signtool = Join-Path $sdkDir.FullName "x64\signtool.exe"
if (-not (Test-Path $makeappx)) { throw "未找到 makeappx.exe: $makeappx" }

# ---- 暂存目录 = 包内容(publish/* + Assets + 渲染后的 AppxManifest.xml)
$pkgRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$manifestTemplate = Join-Path $pkgRoot "packaging\msix\AppxManifest.template.xml"
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("DeskOrder-msix-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $stage | Out-Null

$OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$msix = Join-Path $OutDir "DeskOrder-win-MSIX.msix"
Remove-Item $msix -Force -ErrorAction SilentlyContinue

try {
    Copy-Item -Path (Join-Path $PublishDir "*") -Destination $stage -Recurse -Force
    Copy-Item -Path (Join-Path $pkgRoot "packaging\msix\Assets") -Destination $stage -Recurse -Force

    $manifest = Get-Content $manifestTemplate -Raw -Encoding UTF8
    $manifest = $manifest.Replace("__IDENTITY_NAME__", $IdentityName).Replace("__PUBLISHER__", $Publisher).Replace("__VERSION__", $Version)
    # 无 BOM UTF-8:AppxManifest 声明了 encoding=utf-8,MakeAppx 按声明解析
    [System.IO.File]::WriteAllText((Join-Path $stage "AppxManifest.xml"), $manifest, (New-Object System.Text.UTF8Encoding($false)))

    & $makeappx pack /o /d $stage /p $msix
    if ($LASTEXITCODE -ne 0) { throw "makeappx 打包失败" }
} finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}

# ---- 侧载自签:CN 必须与清单 Publisher 完全一致,签名才会通过校验
if ($SignSideload) {
    $certParams = @{ Type = "Custom"; Subject = $Publisher; KeyUsage = "DigitalSignature"; FriendlyName = "DeskOrder MSIX sideload"; CertStoreLocation = "Cert:\CurrentUser\My"; TextExtension = @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") }
    $cert = New-SelfSignedCertificate @certParams
    $pfx = Join-Path $OutDir "DeskOrder-sideload.pfx"
    $pfxPwd = [guid]::NewGuid().ToString("N")
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString -String $pfxPwd -Force -AsPlainText) | Out-Null
    & $signtool sign /fd SHA256 /a /f $pfx /p $pfxPwd $msix
    if ($LASTEXITCODE -ne 0) { throw "signtool 签名失败" }

    Write-Host ""
    Write-Host "==== 本机侧载步骤 ====" -ForegroundColor Cyan
    Write-Host "1) 管理员 PowerShell 导入证书(当前会话的随机口令):"
    Write-Host "   Import-PfxCertificate -FilePath `"$pfx`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople -Password (ConvertTo-SecureString -String '$pfxPwd' -Force -AsPlainText)"
    Write-Host "2) 安装: 双击 $msix,或 Add-AppxPackage -Path `"$msix`""
}

Write-Host ""
Write-Host "MSIX 完成: $msix (Identity=$IdentityName, Publisher=$Publisher, Version=$Version)" -ForegroundColor Green
