#Requires -Version 7.0
<#
.SYNOPSIS
    update-guard 真机演练（防臃肿整改 Phase 4 · T5 的收尾验证）。
.DESCRIPTION
    在隔离沙盒里手工种一份"已应用、未确认健康"的守卫快照，然后用真实 exe 冷启动一次会话，
    验证这条链在真机上确实接通：
      跨会话武装（UpdateRollbackCoordinator.ArmFromPersistedState → UpdateDataGuard.UnconfirmedSnapshotVersion）
      → 服务真实起来并出现好符号
      → 确认健康（ConfirmHealthy → MarkConfirmedHealthy，真实落盘 guard-state.json）
      → 解除武装（后续启动不再回滚）。
    不验证的部分（如实说明）：真正触发回滚需要一份"可被发现但起不来"的运行时，那是伪造
    SelfContained 布局的活，不在本演练范围内；回滚的 IO 半程由 RealOS 用例
    UpdateDataGuardOutcomes 覆盖，派发/状态闭合由 Headless 用例
    UpdateRollbackCoordinatorRegressionTests 覆盖。
    隔离纪律：只写 sandbox/update-rollback/ 下带时间戳的新目录；随机高位端口；清空
    DSH_WEB_URL/DSH_VERSION 等注入变量；结束按记录的 PID 精确杀进程树（绝不按名扫杀）。
    全局 dsh 包只读（本演练不装不降）。
.EXAMPLE
    pwsh -File sandbox/update-rollback/guard-arm-drill.ps1 -Exe <path to DshWeb.exe>
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [int]$BudgetSeconds = 120
)
$ErrorActionPreference = 'Stop'
$scene = Join-Path $PSScriptRoot ("run-" + (Get-Date -Format 'HHmmss'))
New-Item -ItemType Directory -Force -Path $scene | Out-Null
$home_ = Join-Path $scene 'home'
$wv2 = Join-Path $scene 'wv2'
New-Item -ItemType Directory -Force -Path $home_, $wv2, (Join-Path $home_ 'dsh-launcher') | Out-Null

# 1) 本机全局 dsh 的版本 = 壳在新鲜沙盒里会发现的版本（只读探查，绝不改动全局包）
$gpre = (& npm prefix -g 2>$null | Select-Object -Last 1)
$pkgJson = Join-Path $gpre 'node_modules\@deepseek-ai\dsh\package.json'
if (-not (Test-Path $pkgJson)) { Write-Host "[FAIL] 未找到全局 dsh 包：$pkgJson"; exit 1 }
$ver = (Get-Content $pkgJson -Raw | ConvertFrom-Json).version
Write-Host "[info] 期望被发现的身份版本 = $ver"

# 2) 种守卫状态：一条 "已应用、未确认健康" 的快照 + 快照目录里的受保护文件旧字节
$token = ($ver -replace '[^0-9A-Za-z._-]', '_')
$snapDir = "pre-$token-20260101-000000"
$guardRoot = Join-Path $home_ 'dsh-launcher\update-guard'
New-Item -ItemType Directory -Force -Path (Join-Path $guardRoot 'snapshots') | Out-Null
$snapPath = Join-Path $guardRoot "snapshots\$snapDir"
New-Item -ItemType Directory -Force -Path $snapPath | Out-Null
Set-Content -Path (Join-Path $snapPath '.credentials.yaml') -Value 'version: 1' -Encoding utf8NoBOM -NoNewline
Set-Content -Path (Join-Path $home_ '.credentials.yaml') -Value 'refs: migrated-by-newer-version' -Encoding utf8NoBOM -NoNewline
$state = @{ snapshots = @(@{ version = $ver; dir = $snapDir; createdAtUtc = '2026-01-01T00:00:00Z'; confirmedHealthyUtc = $null }) }
Set-Content -Path (Join-Path $guardRoot 'guard-state.json') -Value ($state | ConvertTo-Json -Depth 5) -Encoding utf8NoBOM

# 3) 随机高位端口 + 真实 exe 冷启动（DSH_SANDBOX=1：绝不写宿主自启/机器级副作用）
$probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $probe.Start()
$port = ([System.Net.IPEndPoint]$probe.LocalEndpoint).Port; $probe.Stop()
$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $Exe; $psi.WorkingDirectory = Split-Path $Exe; $psi.UseShellExecute = $false
foreach ($k in 'DSH_WEB_URL', 'DSH_HOME', 'DSH_WEB_PORT', 'DSH_VERSION', 'DSH_SANDBOX', 'DSH_E2E',
               'DSH_TEST_INSTANCE', 'DSH_TEST_UPDATE_SIGNAL', 'DSH_TEST_FAKE_APPLY') {
    $psi.EnvironmentVariables.Remove($k) | Out-Null }
$psi.EnvironmentVariables['DSH_SANDBOX'] = '1'
$psi.EnvironmentVariables['DSH_HOME'] = $home_
$psi.EnvironmentVariables['DSH_WEB_PORT'] = "$port"
$psi.EnvironmentVariables['DSH_WEBVIEW2_DATA'] = $wv2
$psi.EnvironmentVariables['DSH_TEST_INSTANCE'] = '1'
$psi.EnvironmentVariables['DSH_E2E'] = '1'
$p = [System.Diagnostics.Process]::Start($psi)
if ($null -eq $p) { Write-Host '[FAIL] 无法启动 exe'; exit 1 }
$log = Join-Path $home_ 'dsh-launcher\dsh.log'
$deadline = (Get-Date).AddSeconds($BudgetSeconds)
$armed = $false; $confirmed = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 800
    if (-not (Test-Path $log)) { continue }
    $txt = Get-Content $log -Raw -ErrorAction SilentlyContinue
    if (-not $armed -and $txt -match 'rollback guard armed \(cross-session\) for v(\S+)') {
        $armed = $true; Write-Host "[ OK ] 跨会话武装：发现未确认快照 v$($Matches[1])" }
    if (-not $confirmed -and $txt -match 'confirmed healthy; rollback guard disarmed') {
        $confirmed = $true; Write-Host '[ OK ] 好符号确认健康并解除武装（真实落盘）' }
    if ($armed -and $confirmed) { break }
}
try { $p.Kill($true) } catch { }
Start-Sleep -Seconds 1

# 4) 物理终局断言：manifest 里那条的 confirmedHealthyUtc 已写入；健康路径不得触发回滚。
#    （原先这里断言"受保护文件字节不变"，是错的：.credentials.yaml 归 dsh 所有，健康运行时
#     本来就会写它——实测它被迁移成 version+refs 布局。判"有没有回滚"要看回滚自己留下的痕迹。）
$after = Get-Content (Join-Path $guardRoot 'guard-state.json') -Raw | ConvertFrom-Json
$entry = $after.snapshots | Where-Object { $_.version -eq $ver } | Select-Object -First 1
$healthyStamp = if ($entry) { $entry.confirmedHealthyUtc } else { $null }
if ($healthyStamp) { Write-Host "[ OK ] guard-state.json confirmedHealthyUtc = $healthyStamp" }
else { Write-Host '[FAIL] guard-state.json 仍无 confirmedHealthyUtc（确认动作未落盘）' }
$txt2 = if (Test-Path $log) { Get-Content $log -Raw } else { '' }
$noRollbackFired = $txt2 -notmatch '\[update-rollback\]'
$noHistory = -not (Test-Path (Join-Path $guardRoot 'rollback-history.jsonl'))
if ($noRollbackFired -and $noHistory) { Write-Host '[ OK ] 健康路径未触发回滚（无 [update-rollback] 痕迹、无 rollback-history.jsonl）' }
else { Write-Host "[FAIL] 健康路径却留下了回滚痕迹（fired=$noRollbackFired history=$noHistory）" }
if ($armed -and $confirmed -and $healthyStamp -and $noRollbackFired -and $noHistory) {
    Write-Host '== GUARD ARM/CONFIRM DRILL: PASS =='; exit 0 }
Write-Host "== DRILL: NOT COMPLETE (armed=$armed confirmed=$confirmed stamp=$([bool]$healthyStamp)) =="
Write-Host "日志与沙盒保留在 $scene"
exit 2
