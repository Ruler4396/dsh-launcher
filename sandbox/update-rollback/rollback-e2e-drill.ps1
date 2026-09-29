#Requires -Version 7.0
<#
.SYNOPSIS
    回滚 saga 真机端到端演练（防臃肿整改 Phase 4 · T5 的最后一环）。
.DESCRIPTION
    种两份 launcher 自管的 SelfContained 运行时（发现链里 SelfContained 优先级最高，所以本机
    装着全局 dsh 也不影响）：
      · 9.9.9-drill-bad  —— 版本更高、能被发现、HTTP 也起得来，但页面里**没有好符号**；
      · 9.9.8-drill-good —— 页面里有好符号（回滚之后服务必须落在这里）。
    同时种一份"已应用、未确认健康"的 update-guard 快照（受保护文件 = 哨兵旧字节），并把沙盒里
    的 .credentials.yaml 改成"被新版迁移过"的字节。然后用 DSH_BOOT_SIGNATURES 把 good_symbol 指向
    只有 good 运行时才有的符号，真实启动一次：
      启动自检失败(E2008) → 回滚闸门已武装 → 还原受保护文件 + 把坏运行时移入 quarantine →
      以 9.9.8-drill-good 重新拉起 → E4003 如实上报。
    最后再冷启动一次，断言武装标记已被一次性消费（不再回滚）。
    隔离纪律：全部写入 sandbox/update-rollback/run-<ts>/；随机高位端口；按记录 PID 精确杀树；
    不碰真实 ~/.dsh、不碰 3080、不动全局 dsh 包（只读它一次以拿 node.exe 版本无关）。
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [int]$BudgetSeconds = 150
)
$ErrorActionPreference = 'Stop'
# 上一次运行只留下"不能对值为 Null 的表达式调用方法"这一行，无法定位——先补可观测性再谈结论。
trap {
    Write-Host ("DRILL-ERROR: " + $_.Exception.Message)
    Write-Host ($_.ScriptStackTrace -join "`n")
    Write-Host "现场保留在 $scene ；已启动 PID 见 $pidLog"
    exit 3
}
$scene = Join-Path $PSScriptRoot ("rb-" + (Get-Date -Format 'HHmmss'))
$home_ = Join-Path $scene 'home'
$dataDir = Join-Path $home_ 'dsh-launcher'
$runtimes = Join-Path $dataDir 'runtimes'
$wv2 = Join-Path $scene 'wv2'
$pidLog = Join-Path $scene 'launched-pids.txt'
New-Item -ItemType Directory -Force -Path $home_, $wv2, $dataDir, $runtimes | Out-Null
$badVer = '9.9.9-drill-bad'; $goodVer = '9.9.8-drill-good'
$sentinel = "version: 1`nrefs: {}`n# sentinel-pre-update-bytes`n"
$mutated = "version: 2`nrefs:`n  migrated-by: drill-bad`n"

# 好符号：由 DSH_BOOT_SIGNATURES 指定，只有 good 运行时的页面里有它
$goodSymbol = 'window.__drillGoodOk'

function New-FakeRuntime([string]$ver, [string]$serveBody, [bool]$healthy) {
    $pkg = Join-Path $runtimes "$ver\node_modules\@deepseek-ai\dsh"
    New-Item -ItemType Directory -Force -Path (Join-Path $pkg 'lib') | Out-Null
    # bin.js：真起一个 HTTP 服务并打印壳要的横幅行；页面体由调用方决定（有没有好符号）
    $js = @'
const http = require('http');
const args = process.argv.slice(2);
const port = Number(args[args.indexOf('--port') + 1] || 3080);
const body = BODY_JSON;
const GOOD = GOOD_JSON;
const srv = http.createServer((req, res) => {
  res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
  res.end('<!doctype html><html><head><meta charset="utf-8">'
    + '<script>window.__drillGoodOk = ' + GOOD + ';</script></head>'
    + '<body>' + body + '</body></html>');
});
srv.listen(port, '127.0.0.1', () => {
  console.log('dsh web: http://127.0.0.1:' + port + '/?token=drill-' + port);
});
'@
    $js = $js.Replace('BODY_JSON', ($serveBody | ConvertTo-Json)).Replace('GOOD_JSON', "$($healthy.ToString().ToLower())")
    Set-Content -Path (Join-Path $pkg 'lib\bin.js') -Value $js -Encoding utf8NoBOM
    (@{ name = '@deepseek-ai/dsh'; version = $ver; bin = @{ dsh = 'lib/bin.js' } } | ConvertTo-Json -Depth 5) |
        Set-Content -Path (Join-Path $pkg 'package.json') -Encoding utf8NoBOM
}
# 好符号是"表达式求值为真"，所以必须由页面**真的**置真/置假——上一版把符号名写进正文，
# 让"回滚后的 good 运行时"在页面探针里同样缺席（演练保真度缺陷，不是产品缺陷）。
New-FakeRuntime $badVer 'drill bad runtime: no good symbol' $false
New-FakeRuntime $goodVer 'drill good runtime' $true

# update-guard：种"已应用未确认健康"的快照（受保护文件 = 哨兵旧字节），并把在线文件改成新版字节
$token = ($badVer -replace '[^0-9A-Za-z._-]', '_')
$snapDir = "pre-$token-20260101-000000"
$guardRoot = Join-Path $dataDir 'update-guard'
New-Item -ItemType Directory -Force -Path (Join-Path $guardRoot 'snapshots') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $guardRoot "snapshots\$snapDir") | Out-Null
Set-Content -Path (Join-Path $guardRoot "snapshots\$snapDir\.credentials.yaml") -Value $sentinel -Encoding utf8NoBOM -NoNewline
Set-Content -Path (Join-Path $home_ '.credentials.yaml') -Value $mutated -Encoding utf8NoBOM -NoNewline
@{ snapshots = @(@{ version = $badVer; dir = $snapDir; createdAtUtc = '2026-01-01T00:00:00Z'; confirmedHealthyUtc = $null }) } |
    ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $guardRoot 'guard-state.json') -Encoding utf8NoBOM

$sigs = (@{ good_symbol = $goodSymbol; grace_ms = 1500; probe_interval_ms = 1500; absent_threshold = 2 } | ConvertTo-Json -Compress)

function Start-Session([string]$tag, [int]$budget) {
    $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $probe.Start()
    $port = ([System.Net.IPEndPoint]$probe.LocalEndpoint).Port; $probe.Stop()
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $Exe; $psi.WorkingDirectory = Split-Path $Exe; $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    foreach ($k in 'DSH_WEB_URL', 'DSH_VERSION', 'DSH_BOOT_SIGNATURES') { $psi.EnvironmentVariables.Remove($k) | Out-Null }
    $psi.EnvironmentVariables['DSH_SANDBOX'] = '1'          # 绝不写宿主自启/机器级副作用
    $psi.EnvironmentVariables['DSH_HOME'] = $home_
    $psi.EnvironmentVariables['DSH_WEB_PORT'] = "$port"
    $psi.EnvironmentVariables['DSH_WEBVIEW2_DATA'] = Join-Path $wv2 $tag
    $psi.EnvironmentVariables['DSH_TEST_INSTANCE'] = '1'
    $psi.EnvironmentVariables['DSH_E2E'] = '1'              # 弹窗只写日志+stdout，不阻塞
    $psi.EnvironmentVariables['DSH_BOOT_SIGNATURES'] = $sigs
    $p = [System.Diagnostics.Process]::Start($psi)
    # 记账：脚本被中断时也要能按 PID 精确回收（绝不按映像名扫杀）
    Add-Content -Path $pidLog -Value "$tag=$($p.Id)"
    # 异步排空 stdout/stderr。上一版用 OutputDataReceived 事件 + StringBuilder 累加：
    # 事件回调在 PowerShell 的独立作用域里跑，$out 在那里是 null，于是**第一条子进程输出
    # 就把整个演练炸掉**（只留一行"不能对值为 Null 的表达式调用方法"，两次运行都栽在这里，
    # 而我还把留在半路的启动器误认成上一次的残留给杀了）。改为 Task 读干，循环内只看统一日志。
    $stdout = $p.StandardOutput.ReadToEndAsync()
    $stderr = $p.StandardError.ReadToEndAsync()
    $log = Join-Path $dataDir 'dsh.log'
    $deadline = (Get-Date).AddSeconds($budget)
    $hit = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 700
        $txt = ''
        if (Test-Path $log) { $txt = Get-Content $log -Raw -ErrorAction SilentlyContinue }
        if ($txt -match '\[update-rollback\] completed for v') { $hit = $true; break }
        if ($tag -eq 'second' -and $txt -match 'lifecycle: WaitingForReadiness --ServiceReady-->') {
            Start-Sleep -Seconds 12; $hit = $true; break
        }
    }
    Start-Sleep -Seconds 2
    Stop-LauncherTree $p.Id $tag
    Start-Sleep -Seconds 1
    # 再读一次：$txt 是循环里最后一轮的快照，回滚的收尾行可能在那 2 秒里才落盘
    if (Test-Path $log) { $txt = Get-Content $log -Raw -ErrorAction SilentlyContinue }
    [void]$stdout.Wait(3000); [void]$stderr.Wait(3000)
    $outText = ''
    if ($stdout.IsCompleted -and -not $stdout.IsFaulted) { $outText = $stdout.Result }
    if (-not $hit) { Write-Host "[WARN] $tag 未在预算内命中期望终态（断言仍会如实判红）" }
    # 统一日志是**跨会话同一个文件**：会话 2 若整份读，就会把会话 1 的 [update-rollback]
    # 当成"第二次启动仍在回滚"（实测红过一次，属演练自证的假红）。按本次启动器 PID 过滤。
    $own = ($txt -split "`n" | Where-Object { $_ -match ('"pid":' + $p.Id + '[,:]') }) -join "`n"
    return @{ port = $port; shell = $own; stdout = $outText }
}

# 上一版这里用 $p.Kill($true) 并 catch{} 吞掉异常：演练被中断时留下过孤儿启动器
# （进程还活着、锁着构建输出，后续 build 直接失败）。改为按记录的 PID 杀整树并**验确认**。
function Stop-LauncherTree([int]$procId, [string]$tag) {
    if ($procId -le 0) { return }
    taskkill /PID $procId /T /F 2>&1 | Out-Null
    Start-Sleep -Milliseconds 800
    if (Get-Process -Id $procId -ErrorAction SilentlyContinue) {
        Write-Host "[WARN] $tag launcher pid=$procId survived Kill"
    }
}

Write-Host "== 会话 1：期望自动回滚 =="
$s1 = Start-Session 'first' $BudgetSeconds
$combined = $s1.shell + $s1.stdout
$checks = [ordered]@{
    '武装（跨会话）'                    = $combined -match 'rollback guard armed \(cross-session\) for v9\.9\.9-drill-bad'
    '停服先于数据还原（防目录锁）'      = ($combined -match 'stopping service before data rollback') -and
                                        (([int]$combined.IndexOf('[update-rollback] stopping service before data rollback')) -lt ([int]$combined.IndexOf('rolling back pre-update shared data')))
    '回滚事务确实执行'                  = $combined -match '\[update-rollback\] completed for v9\.9\.9-drill-bad'
    '坏运行时被移出发现链'              = (-not (Test-Path (Join-Path $runtimes $badVer))) -and (Test-Path (Join-Path $guardRoot 'quarantine'))
    '回滚历史留痕'                      = (Test-Path (Join-Path $guardRoot 'rollback-history.jsonl'))
    '受保护文件按字节还原'              = ((Get-Content (Join-Path $home_ '.credentials.yaml') -Raw) -eq $sentinel)
    '旧版服务重新起来'                  = $combined -match 'service start via identity' -and (Test-Path (Join-Path $runtimes $goodVer))
    'E4003 对用户可见（含回滚结果）'    = $combined -match 'E4003'
}
foreach ($k in $checks.Keys) {
    if ($checks[$k]) { Write-Host "[ OK ] $k" } else { Write-Host "[FAIL] $k" }
}

Write-Host "== 会话 2：武装标记应已被一次性消费（不得再次回滚） =="
$s2 = Start-Session 'second' 90
$combined2 = $s2.shell + $s2.stdout
$noSecondRollback = $combined2 -notmatch '\[update-rollback\]'
if ($noSecondRollback) { Write-Host '[ OK ] 第二次启动未再回滚' } else { Write-Host '[FAIL] 第二次启动仍触发回滚（一次性消费失效）' }

$fail = @($checks.Keys | Where-Object { -not $checks[$_] })
if ($fail.Count -eq 0 -and $noSecondRollback) {
    Write-Host '== UPDATE-ROLLBACK E2E DRILL: PASS =='; exit 0
}
Write-Host "== DRILL: FAIL ($($fail.Count + (1 - [int]$noSecondRollback)) 项) ；现场保留在 $scene =="
Write-Host "回滚相关日志片段："
($combined -split "`n" | Select-String -Pattern 'update-rollback|update-guard|E4003|quarantin' | Select-Object -First 18) | ForEach-Object { "  " + $_.ToString().Substring(0, [Math]::Min(160, $_.ToString().Length)) }
exit 2
