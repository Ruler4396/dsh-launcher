#Requires -Version 5.1
<#
.SYNOPSIS
    dsh 更新"应用链路"进程级沙盒演练（2026-09）：以真实发布 exe 冷启动，
    验证 启动阶段0 → ApplyPendingDshUpdate（SelfContained 原子切换）→ 新运行时拉起服务 → HTTP 就绪 全链路。
.DESCRIPTION
    隔离铁律：
      - 一切写入限定于 %TEMP%\dsh-drill-<ts>\（DSH_HOME 重定向）；
      - 种子运行时从用户已安装的全局 dsh 包【只读复制】构造（绝不修改源）；
      - 使用随机高位端口，不触碰 3080；结束时整树回收进程；
      - 不设置 DSH_SANDBOX（否则维护任务被门控跳过，演练失真）；清空 DSH_WEB_URL/DSH_VERSION 等注入变量。
    覆盖范围说明：下载侧（点击→staged 构建）由 RealOS 单测覆盖（DshUpdatePipelineRealTests）；
    本脚本专注"已有 pending 的冷启动应用"这一无需交互的生产路径。
.PARAMETER ExePath
    待演练的 DshWeb.exe；缺省自动探测 dist 下最新版本目录。
.PARAMETER KeepArtifacts
    保留演练目录与日志以便排查（默认结束即清理）。
.EXAMPLE
    pwsh scripts/update-drill.ps1 -ExePath dist\dsh-launcher-windows-0.4.1\DshWeb.exe
#>
param(
    [string]$ExePath = "",
    [switch]$KeepArtifacts
)
$ErrorActionPreference = 'Stop'

function Assert-Cmd([bool]$Cond, [string]$Name, [string]$Detail = "") {
    if ($Cond) { Write-Host ("[ OK ] " + $Name) }
    else { Write-Host ("[FAIL] " + $Name + $(if ($Detail) { "`n       " + $Detail })); $script:failures++ }
}
$script:failures = 0

# ---- 0) 定位被测 exe ----
if (-not $ExePath) {
    $cand = Get-ChildItem "$PSScriptRoot\..\dist" -Directory -Filter 'dsh-launcher-windows-*' |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $cand) { throw "dist 下未找到 dsh-launcher-windows-* 目录；请先 build-release 或传 -ExePath" }
    $ExePath = Join-Path $cand.FullName 'DshWeb.exe'
}
if (-not (Test-Path $ExePath)) { throw "exe 不存在: $ExePath" }
Write-Host "== dsh 更新应用链路演练 =="
Write-Host ("被测 exe: " + $ExePath)

# ---- 1) 隔离环境准备 ----
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$drillRoot = Join-Path $env:TEMP "dsh-drill-$stamp"
$home_ = Join-Path $drillRoot 'home'
$dataDir = Join-Path $home_ 'dsh-launcher'
$runtimes = Join-Path $dataDir 'runtimes'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null

# 选一个空闲高位端口
$port = Get-Random -Minimum 31000 -Maximum 39999
while (Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue) {
    $port = Get-Random -Minimum 31000 -Maximum 39999
}

# ---- 2) 构造"旧版本"种子运行时（只读复制用户全局 dsh 包） ----
$srcPkg = Join-Path $env:APPDATA 'npm\node_modules\@deepseek-ai\dsh'
Assert-Cmd (Test-Path $srcPkg) "全局 dsh 包存在（只读复制源）" "$srcPkg 不存在——本机未全局安装 dsh"
$oldVer = '0.0.0-drill-old'
$seedRuntime = Join-Path $runtimes $oldVer
$pkgDest = Join-Path $seedRuntime 'node_modules\@deepseek-ai\dsh'
New-Item -ItemType Directory -Force -Path $pkgDest | Out-Null
Copy-Item (Join-Path $srcPkg '*') $pkgDest -Recurse -Force
# 改写副本版本号（仅副本！），使"应用目标版本"自洽
$pkgJson = Join-Path $pkgDest 'package.json'
$pkg = Get-Content $pkgJson -Raw | ConvertFrom-Json
$pkg.version = $oldVer
$pkg | ConvertTo-Json -Depth 20 | Set-Content $pkgJson -Encoding utf8
Write-Host ("种子运行时(副本): " + $seedRuntime)

# ---- 3) 写入合法 pending（指向种子运行时 → 走路径 A 原子切换） ----
$newVer = '0.0.0-drill-new'   # pending 目标版本；apply 会把种子目录整体搬到 runtimes\<newVer>
$pending = [ordered]@{
    version     = $newVer
    failCount   = 0
    tarball     = $null
    prefetched  = $true
    runtimeDir  = $pkgDest          # 直接指向"完整构建产物"，命中路径 A
}
$pending | ConvertTo-Json | Set-Content (Join-Path $dataDir 'pending-update.json') -Encoding utf8

# bin 入口解析需要 package.json.bin 字段——若上游包没有则补一个（仍只改副本）
$raw = Get-Content $pkgJson -Raw | ConvertFrom-Json
if (-not $raw.bin) {
    $binJs = $null
    foreach ($c in @('lib\bin.js','bin.js','lib\index.js')) {
        if (Test-Path (Join-Path $pkgDest $c)) { $binJs = $c; break }
    }
    if ($binJs) {
        $raw | Add-Member -NotePropertyName bin -NotePropertyValue ([pscustomobject]@{ dsh = $binJs }) -Force
        $raw | ConvertTo-Json -Depth 20 | Set-Content $pkgJson -Encoding utf8
    }
}

# ---- 4) 冷启动被测 exe（隔离 env；不设 DSH_SANDBOX！） ----
$logPath = Join-Path $dataDir 'dsh-launcher\dsh.log'
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $ExePath
$psi.UseShellExecute = $false
$psi.EnvironmentVariables['DSH_HOME'] = $home_
$psi.EnvironmentVariables['DSH_WEB_PORT'] = "$port"
$psi.EnvironmentVariables['DSH_WEB_URL'] = $null
$psi.EnvironmentVariables['DSH_VERSION'] = $null
foreach ($k in @('DSH_SERVICE_CMD','DSH_PROFILE')) { $psi.EnvironmentVariables[$k] = $null }
$proc = [System.Diagnostics.Process]::Start($psi)
Write-Host ("已启动 PID=" + $proc.Id + " port=$port")

try {
    # ---- 5) 就绪轮询（最多 150s）：HTTP 200 即视为启动成功 ----
    $ready = $false
    $deadline = (Get-Date).AddSeconds(150)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 800
        try {
            $resp = Invoke-WebRequest -Uri "http://127.0.0.1:$port/" -UseBasicParsing -TimeoutSec 3
            if ($resp.StatusCode -eq 200) { $ready = $true; break }
        } catch { }
        if ($proc.HasExited) { break }
    }
    Assert-Cmd $ready "服务在新运行时上就绪（HTTP 200 @ $port）" $(if ($proc.HasExited) { "exe 提前退出 code=$($proc.ExitCode)" })

    # ---- 6) 断言：原子切换真的发生了 ----
    $swapped = Test-Path (Join-Path $runtimes ($newVer + '\node_modules\@deepseek-ai\dsh\package.json'))
    Assert-Cmd $swapped "原子切换产物存在 runtimes\$newVer"
    $pendGone = -not (Test-Path (Join-Path $dataDir 'pending-update.json'))
    Assert-Cmd $pendGone "pending-update.json 已清除"

    Start-Sleep -Milliseconds 600
    $logText = if (Test-Path $logPath) { Get-Content $logPath -Raw } else { '' }
    Assert-Cmd ($logText -match '\[Apply\] Result: atomic swap success') "统一日志含原子切换成功证据"
    Assert-Cmd (-not ($logText -match 'E4002')) "日志无 E4002 应用失败记录"
}
finally {
    # ---- 7) 整树回收 ----
    try { taskkill /PID $proc.Id /T /F 2>&1 | Out-Null } catch { }
    Start-Sleep -Milliseconds 500
}

# ---- 8) 结论与清理 ----
Write-Host ""
if ($script:failures -eq 0) {
    Write-Host "全部通过：更新应用链路（阶段0 → 原子切换 → 服务就绪）在隔离沙盒内端到端工作正常。"
} else {
    Write-Host ("共 $script:failures 项失败——详见上方 [FAIL] 与演练目录日志。")
}
if ($KeepArtifacts) { Write-Host ("演练目录保留: " + $drillRoot) }
else {
    Remove-Item $drillRoot -Recurse -Force -ErrorAction SilentlyContinue
}
exit ($(if ($script:failures -eq 0) { 0 } else { 1 }))
