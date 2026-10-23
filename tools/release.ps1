<#
.SYNOPSIS
  DeskOrder 发版一键脚本：推送代码 → 打 vX.Y.Z 标签 → 等 Release 流水线 → 用双语更新日志覆盖正文。

.DESCRIPTION
  Release 由 .github/workflows/release.yml 在推送 v* 标签时自动构建并发布
  （publish → 便携 zip → Inno Setup 安装包 → MSIX → gh release create --generate-notes）。
  本脚本只做「发起 + 收尾」两件事：

    1) 从 DesktopZones.csproj 读 <Version>（唯一版本来源），标签自动定为 v<Version>
    2) 前置检查：在目标分支上 / 工作区干净 / releases\notes-<Version>.md 存在 / 标签未被别的提交占用
    3) 推送分支，再创建并推送标签（触发流水线）
    4) 轮询 release.yml 里属于该标签的那次运行，等它结束（失败会打印 gh run view --log-failed）
    5) 成功后用 releases\notes-<Version>.md 覆盖自动生成的英文提交列表（中英双语正文）
    6) 官网 website/index.html 的更新日志只保留最近 3 条（含本次），
       被裁掉版本的 i18n 文案一并清理，改动会自动提交（-NoTrim 跳过）

  官网 website/ 由 pages.yml 在推 main 时自动部署，不需要额外操作。

.PARAMETER DryRun
  只做检查并打印将执行的命令，不推送、不调 gh。

.PARAMETER TrimOnly
  只裁剪官网更新日志并提交，不做任何推送/发版动作。

.PARAMETER NoTrim
  跳过官网更新日志裁剪（默认保留最近 3 条）。

.PARAMETER NoNotes
  保留 CI 自动生成的英文更新日志，不覆盖。

.PARAMETER AllowDirty
  允许工作区有未提交改动（不建议：CI 打的是标签指向的提交）。

.PARAMETER TimeoutMinutes
  等待流水线的分钟数，默认 20。

.EXAMPLE
  tools/release.ps1 -DryRun
  tools/release.ps1
  tools/release.ps1 -TrimOnly
  tools/release.ps1 -NoNotes -TimeoutMinutes 25
#>
param(
    [string]$Branch = "main",
    [string]$Tag = "",
    [string]$NotesFile = "",
    [switch]$NoNotes,
    [switch]$NoTrim,
    [switch]$TrimOnly,
    [switch]$AllowDirty,
    [switch]$DryRun,
    [int]$TimeoutMinutes = 20
)

$ErrorActionPreference = "Stop"
# PS 7.4+ 会把原生命令的非零退出码当成错误；本脚本自己判 $LASTEXITCODE，关掉更可控。
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$root = Split-Path -Parent $PSScriptRoot

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}
function Fail([string]$Message) {
    Write-Host "xx $Message" -ForegroundColor Red
    throw $Message
}

# ---- 官网更新日志裁剪 -------------------------------------------------------
# 官网 website/index.html 的「更新日志」只保留最近 $Keep 条版本：
#   · 每条 = <div class="cl-head..."><span class="rm-ver">vX.Y.Z</span></div> + 紧随的 <ul class="cl-list">
#   · 第一条不带 cl-prev（间距更紧），其余带 cl-prev（margin-top:22px）
#   · 文案在 zh:{...} / en:{...} 两个字典里，键名 cl<版本数字>_i<n>t / _i<n>d，
#     被删掉的版本对应的键一并清掉（否则字典会越滚越大）
# 返回 @{ Removed=@(); Keys=@(); Text=...; Changed=$bool }，由调用方决定写盘/提交。
function Update-SiteChangelog {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$Keep = 3
    )
    # 注意:别把键名取成 Keys —— 那是 Hashtable 的保留属性,结果会被吞掉。
    $result = @{ Removed = @(); RemovedKeys = @(); Text = $null; Changed = $false; File = $Path }
    if (-not (Test-Path $Path)) { return $result }
    $text = [System.IO.File]::ReadAllText($Path)

    # 1) 按出现顺序切出所有版本条目
    $entryPattern = '(?s)<div class="cl-head(?<cls>[^"]*)">\s*<span class="rm-ver">(?<ver>[^<]+)</span>\s*</div>\s*<ul class="cl-list">(?<body>.*?)</ul>'
    $entries = [regex]::Matches($text, $entryPattern)
    if ($entries.Count -eq 0) { return $result }

    # 2) 超出的版本：从后往前删（先删旧的，索引不乱）
    for ($i = $entries.Count - 1; $i -ge $Keep; $i--) {
        $m = $entries[$i]
        $result.Removed += $m.Groups['ver'].Value.Trim()
        $text = $text.Remove($m.Index, $m.Length)
    }

    # 3) 重排 cl-prev：第一条不带，其余带（不管作者手工加条目时有没有摆对）
    $heads = [regex]::Matches($text, '<div class="cl-head(?<cls>[^"]*)">')
    $wanted = @()
    for ($i = 0; $i -lt $heads.Count; $i++) {
        $wanted += ($(if ($i -eq 0) { 'cl-head' } else { 'cl-head cl-prev' }))
    }
    for ($i = $heads.Count - 1; $i -ge 0; $i--) {
        $m = $heads[$i]
        $newHead = '<div class="' + $wanted[$i] + '">'
        $text = $text.Remove($m.Index, $m.Length).Insert($m.Index, $newHead)
    }

    # 4) 清掉已经不存在的版本留下的 i18n 键
    #    字典里的写法是「一行里可能塞了 t/d 两对键」，例如
    #      cl18_i1t:"…",cl18_i1d:"…",
    #    所以按「键 + 到下一个键之前」匹配，而不是按整行删（否则会误删同行的其它键）。
    #    注意"哪些键还在用"只能从**标记区**统计（<script> 之前）：字典本身也含键名，
    #    整份文件一起扫的话每个键都"被引用"，一个都删不掉（这个坑踩过一次）。
    $markupEnd = $text.IndexOf('<script')
    $markup = if ($markupEnd -gt 0) { $text.Substring(0, $markupEnd) } else { $text }
    $used = @{}
    foreach ($m in [regex]::Matches($markup, 'cl\d+_i\d+[td]')) { $used[$m.Value] = $true }
    $keyPattern = '[ \t]*(?<key>cl\d+_i\d+[td])[ \t]*:[ \t]*"(?:[^"\\\r\n]|\\.)*"[ \t]*,?'
    $keyMatches = [regex]::Matches($text, $keyPattern)
    for ($i = $keyMatches.Count - 1; $i -ge 0; $i--) {
        $m = $keyMatches[$i]
        if ($used.ContainsKey($m.Groups['key'].Value)) { continue }
        $result.RemovedKeys += $m.Groups['key'].Value
        $text = $text.Remove($m.Index, $m.Length)
    }
    # 删完可能留下连续空行 / 行尾多余空格，收拾一下
    $text = [regex]::Replace($text, '[ \t]+\r?\n', "`r`n")
    $text = [regex]::Replace($text, '(\r?\n){3,}', "`r`n`r`n")

    $result.Text = $text
    $result.Changed = ($result.Removed.Count -gt 0) -or ($result.RemovedKeys.Count -gt 0) -or
                      ($text -ne [System.IO.File]::ReadAllText($Path))
    return $result
}

# 写回 index.html：保持原来的 BOM 状态（PowerShell 写文件默认会改掉，容易整份文件脏）
function Save-SiteChangelog([string]$Path, [string]$Text) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $enc = New-Object System.Text.UTF8Encoding($hasBom)
    [System.IO.File]::WriteAllText($Path, $Text, $enc)
}

# ---- 版本与标签 -------------------------------------------------------------
$csprojPath = Join-Path $root "DesktopZones.csproj"
$csprojText = Get-Content $csprojPath -Raw -Encoding UTF8
if ($csprojText -notmatch "<Version>([0-9]+(?:\.[0-9]+){1,3})</Version>") {
    Fail "无法从 DesktopZones.csproj 读取 <Version>"
}
$Version = $Matches[1]
if (-not $Tag) { $Tag = "v$Version" }
if (-not $NotesFile) { $NotesFile = Join-Path $root "releases\notes-$Version.md" }
if ($NotesFile -and -not [System.IO.Path]::IsPathRooted($NotesFile)) { $NotesFile = Join-Path $root $NotesFile }

Write-Step "版本 $Version，标签 $Tag"

Push-Location $root
try {
    # ---- 前置检查 ---------------------------------------------------------
    $current = (git rev-parse --abbrev-ref HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { Fail "git rev-parse 失败，当前目录不是 git 仓库？" }
    if ($current -ne $Branch) { Fail "当前分支是 $current，不是 $Branch（先切过去，或用 -Branch 指定）" }

    $dirty = git status --porcelain
    if ($dirty -and -not $AllowDirty) {
        Fail "工作区有未提交改动，先提交（或加 -AllowDirty）:`n$dirty"
    }
    if ($dirty) { Write-Host "! 工作区有未提交改动，-AllowDirty 已放行：CI 打的是已提交内容" -ForegroundColor Yellow }

    if (-not $NoNotes -and -not (Test-Path $NotesFile)) {
        Fail "缺少更新日志：$NotesFile（或用 -NoNotes 保留 CI 自动生成的英文条目）"
    }

    git rev-parse -q --verify "refs/tags/$Tag" 2>$null | Out-Null
    $tagExists = ($LASTEXITCODE -eq 0)
    if ($tagExists) {
        Write-Step "标签 $Tag 已存在，将跳过创建（若指向旧提交需先删：git tag -d $Tag && git push origin :refs/tags/$Tag）"
    }

    $aheadText = git rev-list --count "origin/$Branch..$Branch" 2>$null
    $ahead = 0
    if ($LASTEXITCODE -eq 0 -and $aheadText) { $ahead = [int]$aheadText.Trim() }
    Write-Step "本地领先 origin/$Branch $ahead 个提交"

    # ---- 官网更新日志：只保留最近 3 条 ----------------------------------
    $sitePath = Join-Path $root "website\index.html"
    if (-not $NoTrim) {
        Write-Step "裁剪官网更新日志（保留最近 3 条）"
        $trim = Update-SiteChangelog -Path $sitePath -Keep 3
        if (-not $trim.Changed) {
            Write-Host "    已经是 3 条以内，无需改动"
        }
        elseif ($DryRun) {
            Write-Host "    将删除版本：$($trim.Removed -join ', ')"
            Write-Host "    将清理文案键：$($trim.RemovedKeys.Count) 个"
        }
        else {
            Save-SiteChangelog -Path $sitePath -Text $trim.Text
            Write-Host "    删除版本：$($trim.Removed -join ', ')；清理文案键 $($trim.RemovedKeys.Count) 个"
            git add -- "website/index.html"
            if ($LASTEXITCODE -ne 0) { Fail "git add website/index.html 失败" }
            git commit -m "chore: 官网更新日志仅保留最近 3 条" 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) { Fail "提交官网更新日志裁剪失败" }
            Write-Host "    已提交（chore: 官网更新日志仅保留最近 3 条）"
        }
    }

    if ($TrimOnly) {
        Write-Step "-TrimOnly：只做更新日志裁剪，未推送、未发版"
        return
    }

    # ---- DryRun ----------------------------------------------------------
    if ($DryRun) {
        Write-Step "-DryRun：检查通过，以下命令未执行"
        "  git push origin $Branch"
        if (-not $tagExists) { "  git tag -a $Tag -m `"$Tag`"" }
        "  git push origin $Tag        # 触发 .github/workflows/release.yml"
        "  gh run list --workflow=release.yml --limit 20 --json databaseId,headBranch,status,conclusion"
        if (-not $NoNotes) { "  gh release edit $Tag --title $Tag --notes-file `"$NotesFile`"" }
        "  gh release view $Tag --json url -q .url"
        return
    }

    # ---- 推送代码 + 标签 --------------------------------------------------
    Write-Step "推送 $Branch"
    git push origin $Branch
    if ($LASTEXITCODE -ne 0) { Fail "git push origin $Branch 失败" }

    if (-not $tagExists) {
        Write-Step "创建标签 $Tag"
        git tag -a $Tag -m "$Tag — DeskOrder $Version"
        if ($LASTEXITCODE -ne 0) { Fail "创建标签 $Tag 失败" }
    }

    Write-Step "推送标签 $Tag（触发 Release 流水线）"
    git push origin $Tag
    if ($LASTEXITCODE -ne 0) { Fail "git push origin $Tag 失败" }

    # ---- 等流水线 --------------------------------------------------------
    Write-Step "等待 release.yml 运行（最多 $TimeoutMinutes 分钟）"
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $runId = $null
    $conclusion = $null
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 10
        $json = gh run list --workflow=release.yml --limit 20 --json databaseId,headBranch,status,conclusion
        if ($LASTEXITCODE -ne 0) { Fail "gh run list 失败（gh auth login 了吗？）" }
        $mine = ($json | ConvertFrom-Json) | Where-Object { $_.headBranch -eq $Tag } | Select-Object -First 1
        if (-not $mine) { continue }
        $runId = $mine.databaseId
        if ($mine.status -eq "completed") {
            $conclusion = $mine.conclusion
            break
        }
        Write-Host "    ...运行中 run=$runId status=$($mine.status)"
    }

    if (-not $runId) { Fail "没等到标签 $Tag 的流水线运行，去 Actions 页面手动确认" }
    if ($conclusion -ne "success") {
        Write-Host "xx 流水线失败：$conclusion" -ForegroundColor Red
        "   看失败步骤: gh run view $runId --log-failed"
        Fail "Release 流水线没有成功，Release 未发布"
    }
    Write-Host "    流水线成功 run=$runId" -ForegroundColor Green

    # ---- 覆盖双语更新日志 -------------------------------------------------
    if (-not $NoNotes) {
        Write-Step "用双语更新日志覆盖 Release 正文"
        gh release edit $Tag --title $Tag --notes-file $NotesFile
        if ($LASTEXITCODE -ne 0) { Fail "gh release edit 失败（Release 可能还没创建）" }
    }

    $releaseUrl = (gh release view $Tag --json url -q .url)
    Write-Step "完成"
    "  Release: $releaseUrl"
    "  官网   : https://three-freezen.github.io/DeskOrder/（pages.yml 已随 main 推送自动部署）"
}
finally {
    Pop-Location
}
