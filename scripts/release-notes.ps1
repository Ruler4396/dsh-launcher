#Requires -Version 7.0
<#
.SYNOPSIS
    从 CHANGELOG.md 生成 GitHub Release 正文（唯一实现）。
.DESCRIPTION
    为什么需要这个脚本，而不是在 workflow 里就地正则抽段：

    1) 【换行归一】CHANGELOG 是**硬折行**写的（每行约 100 列，句子中间断开），而 GitHub 的
       Release 正文把**单个换行渲染成硬换行**（实测 v0.5.1 正文里出现 26 个 <br>）。后果就是
       公告看起来被强行截断、右边大片留白。这里把段内续行接回一行：只有当接缝两侧都是 ASCII
       词字符时才补一个空格，否则直接相接（中文相接不该带空格，英文单词之间必须带空格）。
    2) 【单一真相源】发布正文此前只在 build.yml 里内联生成一次；本地想重发/校对一份已发布的
       正文，就得把那段 PowerShell 抄第二遍——抄第二遍迟早一份改一份漏（本仓库反复吃过这个亏）。
       现在 CI 与本地都调本脚本。
    3) 【发布闸不变】找不到 `## [x.y.z] - ` 小节仍然以退出码 1 失败，并打出
       `::error title=missing-changelog::`，由 build.yml 转成红灯（v0.4.0 占位文案事故的根治）。
       代码围栏内的行**原样保留**，不做任何相接。
.EXAMPLE
    ./scripts/release-notes.ps1 -Version 0.5.1 -OutFile body.md -SumsPath dist/SHA256SUMS.txt
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$ChangelogPath = 'CHANGELOG.md',
    [string]$OutFile = 'body.md',
    [string]$SumsPath = '',
    # 调试用：只抽段并归一，不拼校验和/安装说明（本地校对渲染效果时够用）
    [switch]$SectionOnly
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ChangelogPath)) {
    Write-Host "::error title=missing-changelog::CHANGELOG not found at $ChangelogPath"
    exit 1
}
$raw = Get-Content $ChangelogPath -Raw
# 与 build.yml 历史上用的同一个正则：从 `## [x.y.z] - 日期` 起到下一个 `## ` 标题（或文件尾）。
$m = [regex]::Match($raw, "(?ms)^## \[$([regex]::Escape($Version))\] - .*?(?=^## |\z)")
if (-not $m.Success) {
    Write-Host "::error title=missing-changelog::CHANGELOG.md has no '## [$Version]' entry; add it before releasing."
    exit 1
}

function Test-BlockStart([string]$t) {
    # 这些行是新段落的起点，不与上一段相接
    return ($t -match '^#{1,6}\s') -or
           ($t -match '^[-*]\s') -or
           ($t -match '^\d+[.)]\s') -or
           ($t -match '^\|') -or
           ($t -match '^>\s?') -or
           ($t -match '^<!--')
}

# ---- 换行归一：逐行扫描，段内续行接回一行 ----
$lines = $m.Value -split "`r?`n"
$out = New-Object System.Collections.Generic.List[string]
$buf = $null
$inFence = $false

function Flush-Buffer {
    if ($null -ne $buf) { $out.Add($buf); $script:buf = $null }
}

foreach ($line in $lines) {
    $trimmed = $line.Trim()
    if ($trimmed.StartsWith('```')) {
        # 围栏本身与围栏内内容一律原样输出
        Flush-Buffer
        $out.Add($line)
        $inFence = -not $inFence
        continue
    }
    if ($inFence) { $out.Add($line); continue }
    if ($trimmed -eq '') { Flush-Buffer; $out.Add(''); continue }
    if (Test-BlockStart $trimmed) { Flush-Buffer; $buf = $trimmed; continue }

    if ($null -eq $buf) { $buf = $trimmed; continue }

    $prevChar = $buf[$buf.Length - 1]
    $nextChar = $trimmed[0]
    $glue = ''
    if (($prevChar -match "[A-Za-z0-9._\-\])``]") -and ($nextChar -match '[A-Za-z0-9]')) { $glue = ' ' }
    $script:buf = $buf + $glue + $trimmed
}
Flush-Buffer

$section = ($out -join "`n").TrimEnd()

if ($SectionOnly) {
    Set-Content -Path $OutFile -Value $section -Encoding utf8
    Write-Host "release notes section written: $OutFile"
    exit 0
}

# ---- 正式正文：标题 + 小节 + 校验和 + 安装说明（与 build.yml 原内联版逐字一致）----
$body = "## v$Version`n`n" + $section
if ($SumsPath -and (Test-Path $SumsPath)) {
    $body += "`n`n## 校验和 (SHA256)`n``````text`n" + (Get-Content $SumsPath -Raw).TrimEnd() + "`n``````"
}
$body += @"

---

## 安装与卸载 / Install & Uninstall

**MSI 安装包（推荐新手）**：双击安装，向导里可勾选是否开机自启；自动创建桌面与开始菜单快捷方式（含"卸载 dsh-launcher"）。卸载：设置 → 应用 → dsh-launcher → 卸载。

**便携版 ZIP**：解压即用，双击 ``DshWeb.exe``；删文件夹即卸载（自启/快捷方式用 ``uninstall-autostart.cmd`` 清理）。ZIP 为框架依赖发布，**解压后建议先运行同目录 ``check-prereq.cmd`` 确认已安装 .NET Desktop Runtime 10 与 Node.js 18+**（MSI 安装包自带前置检查，无需手动）。

> MSI 与 ZIP 内容完全相同；区别只在安装方式：MSI 有标准安装/卸载流程，适合新手；ZIP 免安装，适合便携党。
> The MSI and ZIP contain the same files; the MSI adds a standard install/uninstall flow for new users, the ZIP is portable and install-free.
"@
Set-Content -Path $OutFile -Value $body -Encoding utf8
Write-Host "release notes written: $OutFile ($(($body -split "`n").Count) lines)"
