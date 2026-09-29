param([int]$ObserveSeconds = 150)
# #28-② 机制验证：服务进程在"运行中"退出时，壳必须静默自愈（不弹任何异常窗），
# 换一个新服务进程、重新导航、页面重新 HEALTHY、插件仍在。
# 用外部强杀代替"点 DSH 自带重启按钮"（0.1.5-rc.2 的 UI 里找不到那个按钮），差别在结论里如实标注。
$ErrorActionPreference = 'Continue'
. 'E:\dsh-launcher\sandbox\issue-verify\lib.ps1'
$sysRoot = if ($env:SystemRoot) { $env:SystemRoot } else { 'C:\Windows' }
$launcher = Get-Process DshWeb -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $launcher) { 'no launcher'; exit 1 }
$ctx = @{ Proc = $launcher }
$lg = Join-Path $env:USERPROFILE '.dsh\dsh-launcher\dsh.log'
$before = @(Get-Content $lg -Encoding UTF8).Count
$svc = @(Get-NetTCPConnection -LocalPort 3080 -State Listen -ErrorAction SilentlyContinue |
  Select-Object -ExpandProperty OwningProcess) | Select-Object -First 1
if (-not $svc) { 'no service on 3080'; exit 1 }
Write-Host ("launcher pid={0} service pid={1}" -f $launcher.Id, $svc)

# 基线：现在有哪些窗口
$baseline = @((Q-FindWindows -Ctx $ctx -Title '' -Any) | ForEach-Object { $_.Title })
Write-Host ("windows before: " + ($baseline -join ' | '))

& (Join-Path $sysRoot 'System32\taskkill.exe') '/T' '/F' '/PID' $svc 2>&1 | Out-Null
Write-Host 'service killed (stands in for the in-page restart)'

$dialogs = @()
$deadline = (Get-Date).AddSeconds($ObserveSeconds)
$newSvc = $null
while ((Get-Date) -lt $deadline) {
  foreach ($w in @(Q-FindWindows -Ctx $ctx -Title '' -Any)) {
    if ($baseline -notcontains $w.Title -and -not ($dialogs | Where-Object { $_ -eq $w.Title })) {
      $dialogs += $w.Title
      Write-Host ("  NEW WINDOW appeared: [{0}] {1}x{2}" -f $w.Title, $w.W, $w.Ht)
    }
  }
  $mine = @(Get-Content $lg -Encoding UTF8 | Select-Object -Skip $before |
    Where-Object { $_ -like ('*"pid":' + $launcher.Id + ',*') })
  $joined = $mine -join "`n"
  if ($joined -match 'HEALTHY' -and $joined -match 'resumed after service restart') { break }
  Start-Sleep -Milliseconds 400
}
$newSvc = @(Get-NetTCPConnection -LocalPort 3080 -State Listen -ErrorAction SilentlyContinue |
  Select-Object -ExpandProperty OwningProcess) | Select-Object -First 1
Write-Host ("service pid now = {0}（kill 前 {1}）" -f ($newSvc -join ','), $svc)
Write-Host '--- 自愈链日志 ---'
$mine | Where-Object { $_ -match 'exited|ServiceExited|restart|resumed|navigat|HEALTHY|E200|E2007|E2004|ask|异常|plugin|record' } |
  Select-Object -First 18 | ForEach-Object { Write-Host $_.Substring(0, [Math]::Min(190, $_.Length)) }
Write-Host ("--- 期间新出现的窗口数 = {0} ---" -f $dialogs.Count)
