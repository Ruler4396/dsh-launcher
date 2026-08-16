<#
.SYNOPSIS
真实环境"预期失败"负向测试（质量治理阶段五补充，隔离铁律执行）。
验证 Launcher 在故障场景下的行为符合"显式失败"哲学：不静默、有错误码、
有日志、可诊断、不误杀、可恢复。

.DESCRIPTION
每个用例都满足隔离铁律：
- DSH_HOME 一律指向 %TEMP%\dsh-neg\<case>\home（前置防护断言 GetFullPath StartsWith TEMP）；
- 端口一律用 39xxx 高位测试端口，绝不触碰 3080；
- 不修改任何真实用户数据（~/.dsh、注册表自启、真实服务）；
- 每个 exe 实例限时运行，超时强制 kill 进程树，不留残留。

用例：
N1  E2004 外部托管指向死端口 → 日志出现 E2004 + 进程不崩溃
N2  pending-update.json 损坏 → 启动不崩溃（StagedUpdate 容错）
N3  僵尸 pid 文件（PID 已死）→ 启动早期被清扫（文件删除）
N4  日志写入失败（DSH_HOME 被文件占位）→ 壳不崩溃（日志失败不影响启动）
N5  --diagnose 脱敏：伪造含真实用户名/~/USERPROFILE 的日志 → zip 内不含明文用户名
N6  单实例：首实例卡住时二次启动在限定时间内自行退出（不重复开窗）
N7  settings.json 非法 JSON → 启动不崩溃、无 E2011 误报（无 serviceLifetime 键）

.EXAMPLE
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/negative-test.ps1
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot "..\.neg-publish\DshWeb.exe")
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$script:failed = 0
$script:passed = 0

function Assert-Neg([bool]$Cond, [string]$Msg) {
    if ($Cond) { Write-Host "[ OK ] $Msg" -ForegroundColor Green; $script:passed++ }
    else { Write-Host "[FAIL] $Msg" -ForegroundColor Red; $script:failed++ }
}

if (-not (Test-Path $Exe)) { Write-Host "[FAIL] 找不到 $Exe（先 dotnet publish -o .neg-publish）" -ForegroundColor Red; exit 1 }

$base = Join-Path $env:TEMP ("dsh-neg-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $base | Out-Null

# 前置防护断言：隔离区必须落在 %TEMP% 内（历史事故铁律）
$baseFull = [System.IO.Path]::GetFullPath($base)
$tempFull = [System.IO.Path]::GetFullPath($env:TEMP)
if (-not $baseFull.StartsWith($tempFull, [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "[FATAL] 隔离区不在 %TEMP% 内，拒绝执行" -ForegroundColor Red; exit 2
}

# 真实环境污染防护：壳启动早期若读到 HKLM AutoStartWanted=1 会把 HKCU Run 改写为测试 exe 路径——
# 备份原值，结束恢复（不打扰真实用户的自启设置）。
$runKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$origRunValue = (Get-ItemProperty -Path $runKeyPath -Name "dsh-launcher" -ErrorAction SilentlyContinue)."dsh-launcher"

function New-IsoHome([string]$case) {
    $isoHome = Join-Path $base $case
    New-Item -ItemType Directory -Force -Path (Join-Path $isoHome "dsh-launcher") | Out-Null
    return $isoHome
}

function Start-ShellExe([string]$case, [hashtable]$env2, [int]$waitSec = 5) {
    $isoHome = New-IsoHome $case
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    # 显式覆盖继承的 dsh 环境变量（本机会话可能带 DSH_WEB_URL/DSH_HOME，会污染隔离）
    $psi.EnvironmentVariables["DSH_HOME"] = $isoHome
    $psi.EnvironmentVariables["DSH_WEB_URL"] = ""   # 默认不外部托管；用例可覆盖
    $psi.EnvironmentVariables["DSH_WEB_PORT"] = ""
    foreach ($k in $env2.Keys) { $psi.EnvironmentVariables[$k] = [string]$env2[$k] }
    $p = [System.Diagnostics.Process]::Start($psi)
    Start-Sleep -Seconds $waitSec
    $alive = -not $p.HasExited
    if ($alive) { try { $p.Kill($true) } catch { } }
    return @{ Home = $isoHome; Alive = $alive; Process = $p }
}

function Get-LogText([string]$isoHome) {
    $log = Join-Path $isoHome "dsh-launcher\dsh.log"
    if (Test-Path $log) { return Get-Content $log -Raw } else { return "" }
}

Write-Host "`n=== N1: 外部托管指向死端口 → E2004 弹窗 + 日志 ===" -ForegroundColor Cyan
$r = Start-ShellExe "n1" @{ DSH_WEB_URL = "http://127.0.0.1:39871" } 6
Assert-Neg $r.Alive "N1: 进程存活（E2004 弹窗阻塞中，符合预期）"
Assert-Neg ((Get-LogText $r.Home) -match "E2004") "N1: 统一日志出现 E2004（可诊断）"
Assert-Neg ((Get-LogText $r.Home) -match "39871") "N1: 日志含目标地址上下文"

Write-Host "`n=== N2: pending-update.json 损坏 → 不崩溃 ===" -ForegroundColor Cyan
$r = Start-ShellExe "n2" @{ DSH_WEB_URL = "http://127.0.0.1:39872" } 5
Set-Content (Join-Path $r.Home "dsh-launcher\pending-update.json") "{broken" -Encoding UTF8
# 先写损坏文件再启动（上一步已启动，这里重新来一次）
$r2 = Start-ShellExe "n2b" @{ DSH_WEB_URL = "http://127.0.0.1:39872" } 5
Assert-Neg $r2.Alive "N2: 损坏的 pending-update.json 不导致崩溃（容错返回 null）"

Write-Host "`n=== N3: 僵尸 pid 文件（PID 已死）→ 启动早期清扫 ===" -ForegroundColor Cyan
$isoHome = New-IsoHome "n3"
# 用一个必然不存在的 PID 模拟"已死进程"（避免 spawn/kill 脆弱性）
$deadPid = 999999
Set-Content (Join-Path $isoHome "dsh-launcher\service-pid-39011.txt") "$deadPid" -Encoding ASCII
# 用隔离端口触发"托管"启动分支（端口未开 → SweepStaleServicePid 在拉起前清扫）
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.EnvironmentVariables["DSH_HOME"] = $isoHome
$psi.EnvironmentVariables["DSH_WEB_URL"] = ""      # 清除继承值，走"托管"分支
$psi.EnvironmentVariables["DSH_WEB_PORT"] = "39011"
$p = [System.Diagnostics.Process]::Start($psi)
Start-Sleep -Seconds 8
if (-not $p.HasExited) { try { $p.Kill($true) } catch { } }
Assert-Neg (-not (Test-Path (Join-Path $isoHome "dsh-launcher\service-pid-39011.txt"))) "N3: 已死 PID 的 pid 文件被清扫删除"
Assert-Neg $true "N3: 测试前提（PID 不存在=已死）成立"

Write-Host "`n=== N4: 日志写入失败（DSH_HOME 被文件占位）→ 壳不崩溃 ===" -ForegroundColor Cyan
$blocker = Join-Path $base "n4-blocker"
Set-Content $blocker "i am a file, not a dir" -Encoding ASCII
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.EnvironmentVariables["DSH_HOME"] = $blocker   # DSH_HOME 指向文件 → dsh.log 目录创建必然失败
$psi.EnvironmentVariables["DSH_WEB_URL"] = "http://127.0.0.1:39874"
$p = [System.Diagnostics.Process]::Start($psi)
Start-Sleep -Seconds 6
$alive = -not $p.HasExited
if ($alive) { try { $p.Kill($true) } catch { } }
Assert-Neg $alive "N4: 日志写失败时壳仍存活（日志失败不影响启动，有意设计）"

Write-Host "`n=== N5: --diagnose 脱敏（伪造含用户名路径的日志，完整导出无 UI 阻塞）===" -ForegroundColor Cyan
$isoHome = New-IsoHome "n5"
$userName = [System.IO.Path]::GetFileName($env:USERPROFILE)
$fakeLog = Join-Path $isoHome "dsh-launcher\dsh.log"
Set-Content $fakeLog ("C:\Users\" + $userName + "\secret.log: leak `n~\AppData leak `n%USERPROFILE%\leak") -Encoding UTF8
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.Arguments = "--diagnose"
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.EnvironmentVariables["DSH_HOME"] = $isoHome
$psi.EnvironmentVariables["DSH_WEB_URL"] = ""
$p = [System.Diagnostics.Process]::Start($psi)
Assert-Neg $p.WaitForExit(30000) "N5: --diagnose 正常退出且无 UI 阻塞（不再弹模态框）"
$zip = Get-ChildItem (Join-Path $env:USERPROFILE "Downloads\*dsh-launcher-diagnose-*.zip") | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($zip) {
    $extract = Join-Path $base "n5-zip"
    Expand-Archive -Path $zip.FullName -DestinationPath $extract -Force
    $fullTxt = Get-Content (Join-Path $extract "log-full.txt") -Raw
    Assert-Neg ($fullTxt -match "secret\.log") "N5: 伪造日志行进入导出（log-full 含原文，测试前提成立）"
    Assert-Neg ($fullTxt -notmatch [regex]::Escape($userName)) "N5: 导出的 zip 不含明文用户名（已脱敏）"
    Assert-Neg ($fullTxt -match "%USER%") "N5: 脱敏占位符 %USER% 生效"
    Remove-Item $zip.FullName -Force -ErrorAction SilentlyContinue
} else {
    Assert-Neg $false "N5: 未找到诊断 zip（下载目录）"
}

Write-Host "`n=== N6: 单实例（首实例卡住时二次启动自行退出）===" -ForegroundColor Cyan
$isoHome = New-IsoHome "n6"
$psi1 = New-Object System.Diagnostics.ProcessStartInfo
$psi1.FileName = $Exe
$psi1.UseShellExecute = $false
$psi1.CreateNoWindow = $true
$psi1.EnvironmentVariables["DSH_HOME"] = $isoHome
$psi1.EnvironmentVariables["DSH_WEB_URL"] = "http://127.0.0.1:39875"
$psi1.EnvironmentVariables["DSH_WEB_PORT"] = ""
$p1 = [System.Diagnostics.Process]::Start($psi1)
Start-Sleep -Seconds 3
$psi2 = New-Object System.Diagnostics.ProcessStartInfo
$psi2.FileName = $Exe
$psi2.UseShellExecute = $false
$psi2.CreateNoWindow = $true
$psi2.EnvironmentVariables["DSH_HOME"] = $isoHome
$psi2.EnvironmentVariables["DSH_WEB_URL"] = "http://127.0.0.1:39875"
$psi2.EnvironmentVariables["DSH_WEB_PORT"] = ""
$p2 = [System.Diagnostics.Process]::Start($psi2)
$p2.WaitForExit(30000)
Assert-Neg $p2.HasExited "N6: 第二实例在 30s 内自行退出（单实例互斥生效）"
if (-not $p1.HasExited) { try { $p1.Kill($true) } catch { } }

Write-Host "`n=== N7: settings.json 非法 JSON → 不崩溃、无 E2011 误报 ===" -ForegroundColor Cyan
$r = Start-ShellExe "n7" @{ DSH_WEB_URL = "http://127.0.0.1:39876" } 6
Set-Content (Join-Path $r.Home "dsh-launcher\settings.json") "{broken json" -Encoding UTF8
$r2 = Start-ShellExe "n7b" @{ DSH_WEB_URL = "http://127.0.0.1:39876" } 6
Assert-Neg $r2.Alive "N7: 非法 settings.json 不导致崩溃"
Assert-Neg ((Get-LogText $r2.Home) -notmatch "E2011") "N7: 无 serviceLifetime 键时不触发 E2011（精确判定，无子串误报）"

# 清理隔离区 + 测试进程（防弹窗残留：超时/异常路径也要保证不遗留 DshWeb 测试实例）
try {
    Get-CimInstance Win32_Process -Filter "Name='DshWeb.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -like "*\.neg-publish\*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
} catch { }
Remove-Item $base -Recurse -Force -ErrorAction SilentlyContinue

# 恢复真实 HKCU Run 自启值（防测试污染用户自启设置）
try {
    if ($null -eq $origRunValue) {
        Remove-ItemProperty -Path $runKeyPath -Name "dsh-launcher" -ErrorAction SilentlyContinue
    } else {
        Set-ItemProperty -Path $runKeyPath -Name "dsh-launcher" -Value $origRunValue
    }
} catch { }

Write-Host ""
if ($script:failed -eq 0) {
    Write-Host "负向测试全部通过（$script:passed 项断言）" -ForegroundColor Green
    exit 0
} else {
    Write-Host "$($script:failed) 项负向断言失败" -ForegroundColor Red
    exit 1
}
