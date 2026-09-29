param([Parameter(Mandatory)][string]$Scenario, [int]$StartBudget = 170)
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'lib.ps1')
# Real-machine verification of the issue scenarios. One scenario per process so a hang in
# one does not hide the others; every wait has a hard budget; cleanup by recorded PID.
switch ($Scenario) {

'T1' {
  # U1 cold start with a REAL dsh service + #25: the notification platform must never load.
  $c = Q-NewScene -Name 'T1'
  Q-Start $c | Out-Null
  try {
    $r = Q-WaitLog $c @('HEALTHY', 'service readiness failed', 'E2002', 'E2001') $StartBudget
    Write-Host "  stage1 hit: $($r.hit)"
    $w = Q-WaitWindow $c 'DeepSeek Harness' 30
    if ($w) { Write-Host ("  main window hwnd={0} {1}x{2} at ({3},{4}) dpi={5}" -f $w.H,$w.W,$w.Ht,$w.X,$w.Y,$w.Dpi) }
    Start-Sleep -Seconds 3
    $m = Q-WpnApps $c
    Q-Shot $c 'T1-main' | Out-Null
    $nav = (Q-Log $c) -match 'webview nav completed: success=True'
    if ($r.hit -eq 'HEALTHY' -and $m.alive -and $m.hits.Count -eq 0 -and $nav) {
      Q-Result 'T1' 'PASS' ("service HEALTHY; nav 200; proc alive pid=" + $c.Proc.Id + "; wpnapps modules=0")
    } else {
      Q-Result 'T1' 'FAIL' ("hit=$($r.hit) alive=$($m.alive) wpnapps=$($m.hits -join ',') nav=$nav")
    }
    Q-Lines $c 'HEALTHY|nav completed|readiness|service start via identity|display topology' 8
  } finally { Q-Stop $c }
}

'T2' {
  # #25 notice channel semantics on screen: present / dedupe / hover pause / queue rotation.
  # Fast mode: the startup pipeline is short-circuited (no real service) - this scenario is
  # about the card, not about dsh.
  $c = Q-NewScene -Name 'T2' -SplashDelayMs 3000 -ExtraEnv @{
      'DSH_TEST_NOTICE_CARD' = '1'; 'DSH_TEST_UPDATE_SIGNAL' = 'dsh:9.9.9' }
  Q-Start $c | Out-Null
  try {
    $card = Q-WaitWindow $c 'DshNoticeCard' 40
    if (-not $card) { Q-Result 'T2' 'FAIL' 'no DshNoticeCard window appeared'; Q-Lines $c 'notice' 8; return }
    Write-Host ("  card hwnd={0} {1}x{2} at ({3},{4}) dpi={5}" -f $card.H,$card.W,$card.Ht,$card.X,$card.Y,$card.Dpi)
    Q-Shot $c 'T2-1-shown' | Out-Null
    # self-test card lives 15s; hover at ~8s must freeze the countdown -> still there at 25s
    Q-Hover ($card.X + [int]($card.W/2)) ($card.Y + [int]($card.Ht/2)) 'card centre (pause)'
    Start-Sleep -Seconds 17
    $still = @(Q-FindWindows $c 'DshNoticeCard').Count
    Write-Host "  after 17s of hovering: cards visible = $still (expect 1: pause works)"
    Q-Shot $c 'T2-2-hovering' | Out-Null
    # leave it alone: countdown resumes and the queued update card must rotate in
    [void][QWin]::SetCursorPos(20, $card.Y + 400)
    $r = Q-WaitLog $c @('notice displayed: dsh \u6709', 'update notice presented', 'notice displayed') 60
    Start-Sleep -Seconds 20
    $rot = @(Q-FindWindows $c 'DshNoticeCard')
    Write-Host "  after leaving: cards now = $($rot.Count)"
    if ($rot.Count -gt 0) { Q-Shot $c 'T2-3-rotated' | Out-Null }
    $log = Q-Log $c
    $dup = $log -match 'notice suppressed as duplicate'
    $hover = $log -match 'notice card hover-pause'
    $resume = $log -match 'notice card hover-resume'
    $presented = ([regex]::Matches($log, 'notice displayed')).Count
    $passMsg = "hover paused (17s>15s lifetime); duplicate suppressed; presented=" + $presented
    $failMsg = "hovered=" + $still + " dup=" + $dup + " presented=" + $presented
    if ($still -eq 1 -and $dup -and $presented -ge 2) { Q-Result 'T2' 'PASS' $passMsg }
    else { Q-Result 'T2' 'FAIL' $failMsg }
    Write-Host ("  hoverLogSeen={0} resumeLogSeen={1}" -f $hover, $resume)
    Q-Lines $c 'notice|update notice' 12
  } finally { Q-Stop $c }
}

'T3' {
  # Full update chain, real clicks, real download: notice card -> click -> "dsh 更新" ask ->
  # Yes -> staged build -> ready notice -> (restart) pending -> "待应用" ask -> apply decision.
  # The final npm install is exercised through DSH_TEST_FAKE_APPLY so the user's GLOBAL dsh
  # package is not mutated; everything up to it is real.
  $c = Q-NewScene -Name 'T3' -ExtraEnv @{ 'DSH_TEST_UPDATE_SIGNAL' = 'dsh:0.1.5-rc.2' }
  Q-Start $c | Out-Null
  $staged = $false
  try {
    $r = Q-WaitLog $c @('HEALTHY', 'service readiness failed') $StartBudget
    Write-Host "  service: $($r.hit)"
    $r2 = Q-WaitLog $c @('prompting update notice', 'update notice presented') 60
    $card = Q-WaitWindow $c 'DshNoticeCard' 40
    if (-not $card) { Q-Result 'T3' 'FAIL' 'update notice card never appeared'; Q-Lines $c 'notice|update' 10; return }
    Write-Host ("  update card {0}x{1} at ({2},{3})" -f $card.W,$card.Ht,$card.X,$card.Y)
    # 文案契约：卡片上"检测到的版本"与"当前版本"不得相同。真机出现过
    # "检测到 dsh 0.1.5-rc.2（当前 0.1.5-rc.2）"——同版本自催更新（假信号绕过了裁决门）。
    # 日志里的中文是 JSON 转义形态，所以按 \uXXXX 匹配。
    $txt = [regex]::Match((Q-Log $c), 'dsh (\S+?)\\uFF08\\u5F53\\u524D (\S+?)\\uFF09')
    if (-not $txt.Success) { Q-Result 'T3' 'FAIL' 'update card body not found in log; cannot check 文案' ; return }
    Write-Host ("  card 文案: 检测到 {0} / 当前 {1}" -f $txt.Groups[1].Value, $txt.Groups[2].Value)
    if ($txt.Groups[1].Value -eq $txt.Groups[2].Value) {
      Q-Result 'T3' 'FAIL' ("card offers an update to the version already installed ({0})" -f $txt.Groups[1].Value); return }
    Q-Shot $c 'T3-1-update-card' | Out-Null
    Q-Click ($card.X + [int]($card.W/2)) ($card.Y + [int]($card.Ht/2)) 'update card centre'
    $ask = Q-WaitWindow $c 'dsh 更新' 15
    if ($ask) {
      Write-Host ("  ask dialog title=[{0}] {1}x{2}" -f $ask.Title, $ask.W, $ask.Ht)
      Q-Shot $c 'T3-2-ask-dialog' | Out-Null
      Q-PressDefault $ask.H
    } else { Q-Result 'T3' 'FAIL' 'card clicked but no "dsh 更新" ask dialog'; return }
    $r3 = Q-WaitLog $c @('update success notification', 'E4001', 'staged update refused',
                         'staged build failed', 'DownloadFailed') 300
    Write-Host "  build verdict needle: $($r3.hit)"
    Q-Shot $c 'T3-3-after-build' | Out-Null
    Q-Lines $c 'staged|build|update|E400' 10
    if ($r3.hit -eq 'update success notification') {
      $staged = $true
      Q-Result 'T3a' 'PASS' 'click -> ask -> real staged download/build succeeded (ready notice)'
    } else {
      Q-Result 'T3a' 'FAIL' ("build did not succeed: hit=$($r3.hit)")
    }
  } finally { Q-Stop $c }

  if (-not $staged) { Q-Summary; return }
  # second launch, same DSH_HOME: pending must be detected and the apply decision asked
  $c2 = Q-NewScene -Name 'T3' -Reuse -ExtraEnv @{
      'DSH_TEST_UPDATE_SIGNAL' = 'dsh:0.1.5-rc.2'; 'DSH_TEST_FAKE_APPLY' = '1' }
  Q-Start $c2 | Out-Null
  try {
    $r4 = Q-WaitLog $c2 @('fake apply', 'pending', 'E4003', 'service readiness failed') 200
    Write-Host "  restart hit: $($r4.hit)"
    $pend = Q-WaitWindow $c2 'dsh 更新待应用' 20
    if ($pend) { Write-Host ("  pending ask title=[{0}]" -f $pend.Title); Q-Shot $c2 'T3-4-pending-ask' | Out-Null; Q-PressDefault $pend.H }
    $r5 = Q-WaitLog $c2 @('fake apply (test hook), pending cleared', 'HEALTHY') 200
    Start-Sleep -Seconds 3
    Q-Shot $c2 'T3-5-applied' | Out-Null
    Q-Lines $c2 'Apply|pending|update|HEALTHY' 10
    if ($r5.hit -eq 'fake apply (test hook), pending cleared') {
      Q-Result 'T3b' 'PASS' 'restart detected pending -> apply decision ran -> pending cleared'
    } else {
      Q-Result 'T3b' 'FAIL' ("restart hit=$($r4.hit) after=$($r5.hit) (apply decision not observed)")
    }
  } finally { Q-Stop $c2 }
}

'T4' {
  # Plugin crash reported by the page -> shell asks the user (real click) -> acts on the answer.
  # External-managed service here (the page is ours), so the accepted branch is the documented
  # "navigate with the safe-mode flag" route; the shell-managed ladder itself is covered by T5-T7.
  $c = Q-NewScene -Name 'T4' -ExternalService
  $srv = Q-StartFakeServer -Port $c.Port -HtmlPath (Join-Path $VerifyRoot 'page-pluginfatal.html')
  Q-Start $c | Out-Null
  try {
    $r = Q-WaitLog $c @('nav completed', 'plugin crash detected', 'E2004') 90
    Write-Host "  stage1: $($r.hit)"
    # 询问框标题是固定的 "DeepSeek Harness - 启动异常"（AskEnterSafeModeOnce）
    $ask = Q-ConfirmDialog $c 'DeepSeek Harness - 启动异常' 45 'T4-1-ask-dialog'
    if (-not $ask) { Q-Result 'T4' 'FAIL' 'no safe-mode ask dialog after pluginFatal message'; Q-Lines $c 'plugin|crash|safe' 10; return }
    $r2 = Q-WaitLog $c @('navigating to safe mode URL') 30
    Write-Host "  after-accept hit: $($r2.hit)"
    Start-Sleep -Seconds 2
    Q-Shot $c 'T4-2-after-accept' | Out-Null
    Q-Lines $c 'plugin crash|safe|E1008|navigat' 10
    $log = Q-Log $c
    $detected = $log -match 'plugin crash detected via webview message'
    $answeredLine = $log -match 'safe-mode ask answered: yes'
    if ($ask.Answered -and $detected -and $answeredLine -and $r2.hit -eq 'navigating to safe mode URL') {
      Q-Result 'T4' 'PASS' 'page pluginFatal -> E1008 detected -> ask dialog -> real click Yes -> safe-mode URL navigated'
    } else {
      Q-Result 'T4' 'FAIL' ("answered=$($ask.Answered) detected=$detected answeredLine=$answeredLine nav=$($r2.hit)")
    }
  } finally { Q-Stop $c; Q-StopFakeServer $srv }
}

'T567' {
  # Sticky safe mode from a previous session, with the isolated profile directory MISSING:
  # T7 = ensure (rebuild before launch, UI must be reachable); T5 = sticky card never
  # auto-dismisses; T6 = real click on the card exits safe mode for real.
  $c = Q-NewScene -Name 'T567' -SafeModeSticky
  $safeDir = Join-Path $c.Home 'profiles\.dsh-safe'
  Write-Host ("  pre-condition: .dsh-safe exists = {0} (must be False)" -f (Test-Path $safeDir))
  Q-Start $c | Out-Null
  try {
    $r = Q-WaitLog $c @('rebuilt before launch', 'SAFEMODE: activated', 'E2002', 'service process exited') $StartBudget
    Write-Host "  T7 ensure hit: $($r.hit)"
    if ($r.hit -ne 'rebuilt before launch') {
      Q-Result 'T7' 'FAIL' "ensure not observed (hit=$($r.hit))"; Q-Lines $c 'SAFEMODE|E200|exited' 10; return
    }
    $pkg = Join-Path $safeDir 'package.json'
    Write-Host "  .dsh-safe/package.json after ensure = $(Test-Path $pkg)"
    $r2 = Q-WaitLog $c @('HEALTHY', 'service readiness failed') 90
    $safe = (Q-Log $c) -match 'service start via identity \(SAFE profile\)'
    if ($r2.hit -eq 'HEALTHY' -and $safe -and (Test-Path $pkg)) {
      Q-Result 'T7' 'PASS' 'missing sticky profile rebuilt -> launched with --profile .dsh-safe -> UI HEALTHY (user can reach the exit)'
    } else { Q-Result 'T7' 'FAIL' "readiness=$($r2.hit) safeProfileStart=$safe pkg=$(Test-Path $pkg)" }

    $card = Q-WaitWindow $c 'DshNoticeCard' 40
    if (-not $card) { Q-Result 'T5' 'FAIL' 'no sticky card window'; return }
    Write-Host ("  sticky card {0}x{1} at ({2},{3})" -f $card.W,$card.Ht,$card.X,$card.Y)
    Q-Shot $c 'T567-1-sticky' | Out-Null
    Start-Sleep -Seconds 60
    $still = @(Q-FindWindows $c 'DshNoticeCard')
    Q-Shot $c 'T567-2-after-60s' | Out-Null
    if ($still.Count -eq 1) { Q-Result 'T5' 'PASS' 'sticky card still on screen 60s after display (no auto-dismiss)' }
    else { Q-Result 'T5' 'FAIL' "cards visible after 60s = $($still.Count)" }

    $flagBefore = (Get-Content (Join-Path $c.Home 'dsh-launcher\safe-mode.json') -Raw)
    $c2 = $still[0]
    Q-Click ($c2.X + [int]($c2.W/2)) ($c2.Y + $c2.Ht - 20) 'sticky card action row'
    $r3 = Q-WaitLog $c @('user exited safe mode via notice') 30
    $r4 = Q-WaitLog $c @('exit-safe-mode: identity-driven start returned True',
                         'exit-safe-mode: identity-driven start returned False',
                         'exit safe mode incomplete') 180
    Write-Host "  exit result: $($r4.hit)"
    Start-Sleep -Seconds 5
    Q-Shot $c 'T567-3-after-exit' | Out-Null
    $flagAfter = (Get-Content (Join-Path $c.Home 'dsh-launcher\safe-mode.json') -Raw)
    $log = Q-Log $c
    $normal = $log -match 'exit-safe-mode.*' -and ($log -match 'service start via identity: [^"]*web --host')
    Write-Host "  safe-mode.json before: $($flagBefore -replace '\s+','')"
    Write-Host "  safe-mode.json after : $($flagAfter -replace '\s+','')"
    Q-Lines $c 'SAFEMODE|service start via identity|notice displayed|exit-safe-mode' 10
    $okExit = ($r3.hit -ne $null) -and ($r4.hit -eq 'exit-safe-mode: identity-driven start returned True')
    $okFlag = $flagAfter -match '"active":\s*false'
    if ($okExit -and $okFlag -and $normal) {
      Q-Result 'T6' 'PASS' 'real click exited safe mode: normal profile relaunched (web subcommand), sticky flag cleared'
    } else {
      Q-Result 'T6' 'FAIL' "clicked=$($r3.hit) restart=$($r4.hit) flagAfter=$($flagAfter -replace '\s+','') normal=$normal"
    }
  } finally { Q-Stop $c }
}

'T8' {
  # Real mouse click on the title-bar version badge -> 版本信息窗 opens, is laid out for the
  # window's own DPI, and closes without killing the host (issue #28-2 crash site).
  $c = Q-NewScene -Name 'T8' -Arguments '--ui-probe' -ExtraEnv @{ 'DSH_TEST_MODE' = '1' }
  Q-Start $c | Out-Null
  try {
    $w = Q-WaitWindow $c 'DeepSeek Harness*' 40
    if (-not $w) { Q-Result 'T8' 'FAIL' 'probe window never appeared'; Q-Lines $c 'probe|hook' 6; return }
    Write-Host ("  window {0}x{1} at ({2},{3}) dpi={4}" -f $w.W,$w.Ht,$w.X,$w.Y,$w.Dpi)
    $rect = $null
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
      $rect = Q-Pipe $c.Proc.Id '{"cmd":"GetVersionBadgeRect"}' 5
      if ($rect -and $rect.ok) { break }
      Start-Sleep -Milliseconds 500
    }
    if (-not ($rect -and $rect.ok)) { Q-Result 'T8' 'FAIL' 'version badge never rendered'; return }
    Write-Host ("  badge rect from production paint: ({0},{1})-({2},{3})" -f $rect.left,$rect.top,$rect.right,$rect.bottom)
    Q-Click ([int](($rect.left + $rect.right)/2)) ([int](($rect.top + $rect.bottom)/2)) 'version badge'
    $d = Q-WaitWindow $c '版本信息' 20
    if (-not $d) { Q-Result 'T8' 'FAIL' 'no 版本信息 window after real badge click'; return }
    Write-Host ("  dialog {0}x{1} at ({2},{3}) dpi={4}" -f $d.W,$d.Ht,$d.X,$d.Y,$d.Dpi)
    Q-Shot $c 'T8-1-version-dialog' | Out-Null
    $asm = [Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $script:ExePath) 'DshWeb.dll'))
    $layout = $asm.GetType('DshWeb.ShellLogic+VersionDialogLayout')
    $exp = $layout.GetMethod('Compute').Invoke($null, @([int]$d.Dpi))
    $sizeOk = ([Math]::Abs($d.W - $exp.ClientWidth) -le 1) -and ([Math]::Abs($d.Ht - $exp.ClientHeight) -le 1)
    Write-Host ("  expected from pure function at dpi {0}: {1}x{2} ; actual {3}x{4} -> {5}" -f
        $d.Dpi, $exp.ClientWidth, $exp.ClientHeight, $d.W, $d.Ht, $sizeOk)
    Q-Click ($d.X + $d.W - 24) ($d.Y + 16) 'dialog close (X)'
    Start-Sleep -Seconds 2
    $stillThere = (@(Q-FindWindows $c '版本信息').Count -gt 0)
    if (-not $c.Proc.HasExited) { $c.Proc.Refresh() }
    $alive = -not $c.Proc.HasExited
    if ($sizeOk -and (-not $stillThere) -and $alive) {
      Q-Result 'T8' 'PASS' 'badge click -> dialog sized by pure function for its own DPI -> closed via X -> host alive'
    } else {
      Q-Result 'T8' 'FAIL' "sizeOk=$sizeOk stillOpen=$stillThere alive=$alive"
    }
  } finally { Q-Stop $c }
}

'T10' {
  # --diagnose: the artifact the issue reporter was asked to provide must be produced,
  # contain the safe-mode evidence, and never crash the host.
  $c = Q-NewScene -Name 'T10' -Arguments '--diagnose' -SafeModeSticky
  $c.Psi.RedirectStandardOutput = $true
  $c.Psi.RedirectStandardError = $true
  $p = Q-Start $c
  $stdoutTask = $p.StandardOutput.ReadToEndAsync()
  $exited = $p.WaitForExit(120000)
  $stdout = if ($exited) { $stdoutTask.Result } else { '' }
  $printed = [regex]::Match($stdout, '([A-Za-z]:\\[^\r\n"]*\.zip)').Value
  Write-Host "  printed path: $printed"
  # 手动入口按设计写到"用户下载目录"（issue 里让报告人上传的就是它）；
  # 静默的启动失败导出才落在 DSH_HOME\dsh-launcher\diagnostics。两条都要能对上。
  $dir = Join-Path $c.Home 'dsh-launcher\diagnostics'
  $zipFile = if ($printed -and (Test-Path $printed)) { Get-Item $printed } else { $null }
  if (-not $zipFile) {
    $zipFile = @(Get-ChildItem $dir -Filter *.zip -ErrorAction SilentlyContinue) |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
  }
  Write-Host ("  exited={0} exitCode={1} zip={2}" -f $exited, $(if ($exited) { $p.ExitCode } else { 'n/a' }), $(if ($zipFile) { $zipFile.FullName } else { 'none' }))
  if (-not $zipFile) { Q-Result 'T10' 'FAIL' "no diagnostics zip (printed=$printed)"; Q-Lines $c 'diagnose' 6; return }
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $a = [System.IO.Compression.ZipFile]::OpenRead($zipFile.FullName)
  $names = @($a.Entries | ForEach-Object { $_.FullName })
  # 证据按主题打包（log-full/env/versions/settings/state/errors），不是原文件名的拷贝——
  # 所以断言看"内容里有没有安全模式证据"，不是文件名。
  $dump = ''
  foreach ($e in $a.Entries) {
    $sr = New-Object System.IO.StreamReader($e.Open())
    $dump += ("`n=== " + $e.FullName + " ===`n" + $sr.ReadToEnd())
    $sr.Dispose()
  }
  $a.Dispose()
  Write-Host ("  zip {0} bytes, {1} entries" -f $zipFile.Length, $names.Count)
  $names | ForEach-Object { Write-Host "    - $_" }
  $hasLog = $dump -match '(?s)=== log-full\.txt ===\s*(\S.{20,})'
  $hasSafe = $dump -match '(?i)safe'
  $hasEnv = $dump -match 'DSH_HOME|env\.txt'
  Write-Host ("  evidence: logBody={0} safeModeMention={1} env={2}" -f $hasLog, $hasSafe, $hasEnv)
  Write-Host "  ---- state.txt / errors.txt excerpt ----"
  (($dump -split "`n") | Select-String -Pattern 'safe|state.txt|errors.txt|===' | Select-Object -First 12 | ForEach-Object { Write-Host "    $($_.Line)" })
  if ($printed -and (Test-Path $printed)) { Remove-Item $printed -Force; Write-Host "  removed test artifact $printed" }
  if ($exited -and $p.ExitCode -eq 0 -and $zipFile.Length -gt 0 -and $hasLog -and $hasSafe) {
    Q-Result 'T10' 'PASS' "diagnostics zip ($($zipFile.Length)B, $($names.Count) parts) carries log body + safe-mode evidence; exit=0"
  } else { Q-Result 'T10' 'FAIL' "exited=$exited code=$($p.ExitCode) size=$($zipFile.Length) log=$hasLog safe=$hasSafe" }
}

'T9' {
  # Tray-resident mode: real right-click on the tray icon -> the self-drawn menu must appear
  # near the cursor, inside the *physical* work area, sized for that monitor's DPI; then a
  # real click on 退出 must actually stop the service and exit the host.
  $c = Q-NewScene -Name 'T9'
  # 托盘驻留由 dsh 的 lifetime 插件控制：壳只读配置，插件不在场时会忽略并 purge（E2011）。
  # 沙盒里合成"插件已安装"的实体标记 + serviceLifetime=1(Tray)，让真实驻留路径可测。
  $marker = Join-Path $c.Home 'profiles\web\node_modules\dsh-launcher-lifetime'
  New-Item -ItemType Directory -Force -Path $marker | Out-Null
  Set-Content -Path (Join-Path $c.Home 'dsh-launcher\settings.json') `
      -Value '{"serviceLifetime":1}' -Encoding utf8
  Q-Start $c | Out-Null
  try {
    $r = Q-WaitLog $c @('HEALTHY', 'service readiness failed') $StartBudget
    Write-Host "  service: $($r.hit)"
    # 托盘图标是**按需**创建的（EnsureTrayIcon 由"关窗到托盘"这条路径触发）：
    # 所以先真点标题栏 × 关窗（Tray 模式下应隐藏而非退出），图标才会出现。
    $w = Q-WaitWindow $c 'DeepSeek Harness*' 40
    if ($w) {
      # 首击常被 WM_MOUSEACTIVATE 吃掉（窗口非前台时只负责激活）——与模态框同一现象，必须复点
      $cx = $w.X + $w.W - 24; $cy = $w.Y + 16
      Q-Click $cx $cy 'main window close (X) #1'
      Start-Sleep -Seconds 2
      if (@(Q-FindWindows $c 'DeepSeek Harness*').Count -gt 0) { Q-Click $cx $cy 'main window close (X) #2' }
      Start-Sleep -Seconds 3
      $stillUp = @(Q-FindWindows $c 'DeepSeek Harness*').Count
      $aliveAfterClose = -not $c.Proc.HasExited
      Write-Host ("  after close: windows visible = {0}, host alive = {1} (Tray mode expects 0 + True)" -f $stillUp, $aliveAfterClose)
      Q-Shot $c 'T9-0-after-close' | Out-Null
      if ($stillUp -gt 0 -or -not $aliveAfterClose) {
        Q-Result 'T9' 'FAIL' "closing the window did not hide-to-tray (windows=$stillUp alive=$aliveAfterClose)"
        Q-Lines $c 'lifetime|tray|E2011|close' 8
        return
      }
    } else { Q-Result 'T9' 'FAIL' 'main window never appeared'; return }
    $asm = [Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $script:ExePath) 'DshWeb.dll'))
    $layout = $asm.GetType('DshWeb.ShellLogic+TrayMenuLayout')
    $g = $layout.GetMethod('ComputeGeometry').Invoke($null, @([int]96))
    & (Join-Path $VerifyRoot 'tray-probe.ps1') -NameHint 'dsh' -Sweep -OverflowFirst `
        -TargetPid $c.Proc.Id -MenuW $g.FormWidth -MenuH $g.FormHeight
    $probeRc = $LASTEXITCODE
    Write-Host "  tray-probe rc=$probeRc"
    $menu = $null
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
      $cands = @(Q-FindWindows $c '' | Where-Object { $_.W -eq $g.FormWidth -and $_.Ht -eq $g.FormHeight })
      if ($cands.Count -gt 0) { $menu = $cands[0]; break }
      Start-Sleep -Milliseconds 400
    }
    if (-not $menu) {
      Q-Result 'T9' 'SKIP' ("tray menu window ({0}x{1}) not found; probe rc={2} — 需要人工把图标拖到可见区再测" -f $g.FormWidth, $g.FormHeight, $probeRc)
      Q-Lines $c 'tray|lifetime|E2011' 8
      return
    }
    Write-Host ("  tray menu {0}x{1} at ({2},{3}) dpi={4}" -f $menu.W,$menu.Ht,$menu.X,$menu.Y,$menu.Dpi)
    Q-Shot $c 'T9-1-tray-menu' | Out-Null
    # 本机 dpi=96 → 逻辑/物理一致；跨倍率的钳位正确性由 T11（双屏）与契约测试负责
    $wa = [System.Windows.Forms.Screen]::AllScreens | ForEach-Object { $_.WorkingArea } |
        Where-Object { $_.Contains($menu.X + 5, $menu.Y + 5) } | Select-Object -First 1
    if (-not $wa) { $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea }
    $inside = ($menu.X -ge $wa.Left - 2) -and ($menu.Y -ge $wa.Top - 2) `
        -and (($menu.X + $menu.W) -le ($wa.Right + 2)) -and (($menu.Y + $menu.Ht) -le ($wa.Bottom + 2))
    Write-Host ("  work area containing the menu: {0},{1}-{2},{3} ; menu inside = {4}" -f `
        $wa.Left, $wa.Top, $wa.Right, $wa.Bottom, $inside)
    # 真点"退出"：条目矩形 = 阴影 + 内缩，取条目中心
    $ix = [int]($menu.X + $g.ShadowMargin + $g.Item.Width / 2)
    $iy = [int]($menu.Y + $g.ShadowMargin + $g.Item.Height / 2)
    Q-Click ([int]$ix) ([int]$iy) 'tray menu 退出 row'
    $gone = $c.Proc.WaitForExit(25000)
    # 托盘"退出"的承诺是**停服务并退出**：先只观察端口，不代劳清理
    $portClosed = $false
    $pl = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $pl) {
      $cl = New-Object System.Net.Sockets.TcpClient
      try {
        $done = $cl.ConnectAsync('127.0.0.1', $c.Port).Wait(600)
        if (-not $done -or -not $cl.Connected) { $portClosed = $true }
      } catch { $portClosed = $true } finally { $cl.Close() }
      if ($portClosed) { break }
      Start-Sleep -Milliseconds 500
    }
    Write-Host ("  host exited = {0}; service port {1} closed = {2}" -f $gone, $c.Port, $portClosed)
    Start-Sleep -Seconds 2
    Q-Shot $c 'T9-2-after-exit' | Out-Null
    $alive = -not $c.Proc.HasExited
    # 端口必须进 PASS 判据：上一版只看"宿主进程退了"就报绿，实测那条绿里写着
    # "service port closed=False"——托盘"退出"根本没停服务，绿灯把 bug 洗白了。
    if ($gone -and -not $alive -and $portClosed) {
      Q-Result 'T9' 'PASS' ("tray menu {0}x{1} inside work area={2}; real click on 退出 exited the host AND closed service port {3}" -f $menu.W, $menu.Ht, $inside, $c.Port)
    } else {
      Q-Result 'T9' 'FAIL' "exit click: gone=$gone alive=$alive portClosed=$portClosed (menu was at $($menu.X),$($menu.Y) size $($menu.W)x$($menu.Ht))"
      Q-Stop $c
    }
  } finally { Q-Stop $c }
}

'T3R' {
  # Same chain as T3 but WITHOUT the fake-apply hook: the real apply runs. Path A moves the
  # staged build into <DSH_HOME>/dsh-launcher/runtimes/<version> (zero npm, sandbox-local), so
  # the user's global dsh package is untouched - and we can ask the honest question the
  # fake hook cannot: does the new version actually boot?
  $c = Q-NewScene -Name 'T3R' -ExtraEnv @{ 'DSH_TEST_UPDATE_SIGNAL' = 'dsh:0.1.5-rc.2' }
  Q-Start $c | Out-Null
  try {
    $r = Q-WaitLog $c @('HEALTHY', 'service readiness failed') $StartBudget
    $before = [regex]::Match((Q-Log $c), 'local=[0-9][^",\s]*').Value
    Write-Host "  service: $($r.hit) ; $before"
    Q-WaitLog $c @('update notice presented') 60 | Out-Null
    $card = Q-WaitWindow $c 'DshNoticeCard' 40
    if (-not $card) { Q-Result 'T3R' 'FAIL' 'no update card'; return }
    Q-Click ($card.X + [int]($card.W/2)) ($card.Y + [int]($card.Ht/2)) 'update card'
    $asked = Q-ConfirmDialog $c 'dsh 更新' 20 'T3R-1-ask'
    if (-not $asked) { Q-Result 'T3R' 'FAIL' 'no ask dialog after click'; return }
    $r3 = Q-WaitLog $c @('update success notification', 'E4001') 300
    Write-Host "  build: $($r3.hit)"
    if ($r3.hit -ne 'update success notification') { Q-Result 'T3R' 'FAIL' "build failed: $($r3.hit)"; return }
  } finally { Q-Stop $c }
  $staged = @(Get-ChildItem (Join-Path $c.Home 'dsh-launcher\staging') -Directory -ErrorAction SilentlyContinue) |
      ForEach-Object { $_.Name }
  Write-Host ("  staging after build: {0}" -f ($staged -join ', '))

  $c2 = Q-NewScene -Name 'T3R' -Reuse -ExtraEnv @{ 'DSH_TEST_UPDATE_SIGNAL' = 'dsh:0.1.5-rc.2' }
  Q-Start $c2 | Out-Null
  try {
    $r4 = Q-WaitLogPid $c2 @('[Apply] Identity verified', '[Apply] Result', 'E4002') 120
    Write-Host "  apply hit: $($r4.hit)"
    $r5 = Q-WaitLogPid $c2 @('HEALTHY', 'service readiness failed', 'E2002', 'E4003',
                             'service start via identity') 240
    Write-Host "  first service line: $($r5.hit)"
    $start = Q-WaitLogPid $c2 @('service start via identity') 30
    Write-Host ("  launch line: {0}" -f $start.line.Substring(0, [Math]::Min(230, $start.line.Length)))
    $r6 = Q-WaitLogPid $c2 @('HEALTHY', 'service readiness failed', 'E2002', 'E4003') 240
    Write-Host "  readiness: $($r6.hit)"
    Q-Lines $c2 '\[Apply\]|update-guard|E400|HEALTHY|readiness' 10
    $rt = @(Get-ChildItem (Join-Path $c2.Home 'dsh-launcher\runtimes') -Directory -ErrorAction SilentlyContinue) |
        ForEach-Object { $_.Name }
    Write-Host ("  runtimes dir: {0}" -f ($rt -join ', '))
    Q-Shot $c2 'T3R-2-after-real-apply' | Out-Null
    # 应用完之后本地就是 0.1.5-rc.2，同一个假信号**不得**再催"有新版本"
    # （真机曾弹过 "检测到 dsh 0.1.5-rc.2（当前 0.1.5-rc.2）"，因为旧假信号分支绕过裁决门）
    $again = Q-WaitLogPid $c2 @('prompting update notice', 'update notice suppressed: up-to-date') 40
    Write-Host ("  post-apply notice verdict: {0}" -f $again.hit)
    $selfContained = $start.line -match 'runtimes' -and $start.line -match '0\.1\.5-rc\.2'
    if ($r6.hit -eq 'HEALTHY' -and $selfContained -and $again.hit -eq 'update notice suppressed: up-to-date') {
      Q-Result 'T3R' 'PASS' 'real apply installed 0.1.5-rc.2 (SelfContained), service booted HEALTHY on it, and the same signal no longer claims an update is available'
    } elseif ($r6.hit -eq 'HEALTHY' -and $selfContained) {
      Q-Result 'T3R' 'FAIL' "update applied but post-apply notice verdict was '$($again.hit)' (expected suppression as up-to-date)"
    } else {
      Q-Result 'T3R' 'FAIL' "after real apply readiness=$($r6.hit); selfContainedLaunch=$selfContained; runtimes=$($rt -join ',')"
    }
  } finally { Q-Stop $c2 }
}

'T11' {
  # Dual-screen: real drag of the main window onto the second monitor, then
  #  (a) a notice that rotates in AFTER the move must be placed on THAT monitor,
  #  (b) real double-click maximize must equal that monitor's rcWork (0px gaps, no taskbar cover).
  # Three queued notices (self-test x2 @15s + update @25s) give the rotation for free.
  $c = Q-NewScene -Name 'T11' -ExtraEnv @{
      'DSH_TEST_NOTICE_CARD' = '1'; 'DSH_TEST_UPDATE_SIGNAL' = 'dsh:0.1.5-rc.2' }
  Q-Start $c | Out-Null
  # 拓扑一律取 **物理** rcWork（Q-Monitors/EnumDisplayMonitors）：WinForms 的 Screen 在
  # DPI-unaware 进程里会把 175% 副屏报成 1463x914，拿它算拖拽落点 = 落错屏。
  try {
    $mons = @(Q-Monitors)
    $sec = @($mons | Where-Object { -not $_.Primary }) | Select-Object -First 1
    if (-not $sec) { Q-Result 'T11' 'SKIP' 'no second monitor visible to the harness'; return }
    Write-Host ("  secondary PHYS work=({0},{1}) {2}x{3} dpi={4}" -f $sec.X, $sec.Y, $sec.W, $sec.H, $sec.Dpi)
    $main = @{ }
    $script:T11Debug = $true
    function Find-Main {
      # 拖动后主窗会被 PMv2 重放到副屏并换 DPI 上下文：只按尺寸筛会在"刚好最小化/短暂不可见"
      # 时静默返回 null，所以每次都把该 pid 的窗口原样打出来，再按"非卡片、够大"取最大者。
      $raw = @(Q-FindWindows $c '')
      $all = @($raw | Where-Object { $_.Title -notmatch 'DshNoticeCard|DshTrayMenu' -and $_.W -gt 500 -and $_.Ht -gt 400 } |
          Sort-Object -Property W -Descending)
      if ($script:T11Debug) {
        Write-Host ("  DEBUG raw windows for pid {0}: {1}" -f $c.Proc.Id, $raw.Count)
        $raw | ForEach-Object { Write-Host ("    hwnd={0} title=[{1}] {2}x{3} at ({4},{5}) dpi={6}" -f $_.H, $_.Title, $_.W, $_.Ht, $_.X, $_.Y, $_.Dpi) }
        $script:T11Debug = $false
      }
      if ($all.Count -gt 0) { return $all[0] }
      return $null
    }
    # 主窗要等启动流水线（服务拉起 + 就绪 + InitializingUI）才存在：必须先轮询，不能查一次就下结论
    Q-WaitLogPid $c @('HEALTHY', 'service readiness failed', 'E2002') $StartBudget | Out-Null
    $script:T11Debug = $true
    $w = $null
    $wl = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $wl) { $w = Find-Main; if ($w) { break }; Start-Sleep -Milliseconds 800 }
    if (-not $w) { Q-Result 'T11' 'FAIL' 'main window never appeared within 120s'; Q-Lines $c 'HEALTHY|readiness|E20' 6; return }
    Write-Host ("  main window {0}x{1} at ({2},{3}) dpi={4}" -f $w.W,$w.Ht,$w.X,$w.Y,$w.Dpi)
    $tb0 = @(Q-TitleBar $w)
    if ($tb0) { Write-Host ("  title bar child (primary) {0}x{1} at ({2},{3}) dpi={4}" -f `
        $tb0[0].W, $tb0[0].Ht, $tb0[0].X, $tb0[0].Y, $tb0[0].Dpi) }
    Q-Shot $c 'T11-1-before-drag' | Out-Null
    # 目标 = 把窗口中心平移到副屏工作区中心（物理像素，同源自壳）；Q-DragWindow 负责
    # "先激活、再按住拖、拖完核对真的动了"（阈值化之后不激活就拖不动，实测 4 轮里中过 1 次）
    $tlX = [int]($sec.X + $sec.W/2 - $w.W/2); $tlY = [int]($sec.Y + $sec.H/2 - $w.Ht/2)
    $script:T11Debug = $true
    $w2 = Q-DragWindow $c $w $tlX $tlY 3 'title bar -> secondary'
    if (-not $w2) { Q-Result 'T11' 'FAIL' 'main window vanished after drag (see DEBUG dump above)'; return }
    if ($w2.X -eq $w.X -and $w2.Y -eq $w.Y) {
      Q-Result 'T11' 'FAIL' "drag never moved the window after 3 activation retries (still at $($w.X),$($w.Y))"; return }
    # 判"在哪块屏"用窗口中心，不用左上角：跨屏瞬间 PMv2 会重定位+改尺寸，角点会误判
    $cX = $w2.X + [int]($w2.W/2); $cY = $w2.Y + [int]($w2.Ht/2)
    $onSec = ($cX -ge $sec.X) -and ($cX -lt ($sec.X + $sec.W)) -and ($cY -ge $sec.Y) -and ($cY -lt ($sec.Y + $sec.H))
    $fullyIn = ($w2.X -ge $sec.X) -and (($w2.X + $w2.W) -le ($sec.X + $sec.W + 2)) `
        -and ($w2.Y -ge $sec.Y) -and (($w2.Y + $w2.Ht) -le ($sec.Y + $sec.H + 2))
    Write-Host ("  after drag: at ({0},{1}) size {2}x{3} dpi={4}; center=({5},{6}) on secondary={7} fullyInside={8}" -f `
        $w2.X, $w2.Y, $w2.W, $w2.Ht, $w2.Dpi, $cX, $cY, $onSec, $fullyIn)
    Q-Shot $c 'T11-2-after-drag' | Out-Null
    # 尺寸判据用**壳自己报的那一行**：输入矩形 × (newDpi/oldDpi) 必须等于结果矩形。
    # 为什么不用"回到原尺寸"：拖拽途中 Aero Snap 预览会先把窗口改成别的大小（实测把顶边拖到
    # 主屏 y≈50 时被改成 1852x1080），拿最终尺寸和出发尺寸比测的是手势，不是这条算术。
    $evPat = 'dpi (\d+)-(?:>|\\u003E)(\d+): state=\w+ bounds=(\d+)x(\d+)@\((-?\d+),(-?\d+)\)'
    # 事件行可能比窗口矩形晚几百毫秒落盘：先轮询到至少一条，再判比例（否则会把"日志还没刷出来"
    # 读成"壳没收到倍率变化"）
    $evs = @(); $ep = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $ep) { $evs = @([regex]::Matches((Q-Log $c), $evPat)); if ($evs.Count -ge 1) { break }; Start-Sleep -Milliseconds 400 }
    Write-Host ("  shell dpi events so far: {0}" -f $evs.Count)
    $upRatioOk = $false
    if ($evs.Count -ge 1) {
      $e = $evs[$evs.Count - 1]
      $r = [int]$e.Groups[2].Value / [int]$e.Groups[1].Value
      $expW = [int][Math]::Round([int]$e.Groups[3].Value * $r)
      $expH = [int][Math]::Round([int]$e.Groups[4].Value * $r)
      $upRatioOk = ([Math]::Abs($w2.W - $expW) -le 3) -and ([Math]::Abs($w2.Ht - $expH) -le 3)
      Write-Host ("  up-cross: {5}->{6}dpi input {3}x{4} => expect {7}x{8}; actual {0}x{1}; ratioOk={2}" -f `
          $w2.W, $w2.Ht, $upRatioOk, $e.Groups[3].Value, $e.Groups[4].Value, $e.Groups[1].Value,
          $e.Groups[2].Value, $expW, $expH)
    } else { Write-Host "  NO dpi event logged => 窗口跨屏时壳根本没收到倍率变化" }
    $card0 = Q-WaitWindow $c 'DshNoticeCard' 20
    if ($card0) { Write-Host ("  card right after drag: at ({0},{1}) {2}x{3} dpi={4}" -f $card0.X, $card0.Y, $card0.W, $card0.Ht, $card0.Dpi) }
    # 轮转后的那一条必须按 owner 当前所在屏重新落位（ShowItem 每次都用 MonitorOf(owner)）
    $moved = $null
    $dl = (Get-Date).AddSeconds(75)
    while ((Get-Date) -lt $dl) {
      $cc = @(Q-FindWindows $c 'DshNoticeCard')
      if ($cc.Count -gt 0) {
        $kx = $cc[0].X + [int]($cc[0].W/2); $ky = $cc[0].Y + [int]($cc[0].Ht/2)
        $inSec = ($kx -ge $sec.X) -and ($kx -lt ($sec.X + $sec.W)) -and ($ky -ge $sec.Y) -and ($ky -lt ($sec.Y + $sec.H))
        if ($inSec) { $moved = $cc[0]; break }
      }
      Start-Sleep -Milliseconds 800
    }
    if ($moved) {
      $inWork = ($moved.X -ge $sec.X) -and (($moved.X + $moved.W) -le ($sec.X + $sec.W + 2)) `
          -and ($moved.Y -ge $sec.Y) -and (($moved.Y + $moved.Ht) -le ($sec.Y + $sec.H + 2))
      # 期望宽度：设计 400px @96 → 该屏有效 DPI 下的物理宽（壳用 EmPx/DeviceDpi 换算）
      $expectW = [int](400 * $sec.Dpi / 96.0)
      Write-Host ("  rotated card: at ({0},{1}) {2}x{3} dpi={4}; inside secondary work={5}; expectedW@{6}%= {7} (ratio {8:N2})" -f `
          $moved.X, $moved.Y, $moved.W, $moved.Ht, $moved.Dpi, $inWork, [int]($sec.Dpi/96*100), $expectW, ($moved.W / $expectW))
      Q-Shot $c 'T11-3-card-on-secondary' | Out-Null
    } else {
      $cc = @(Q-FindWindows $c 'DshNoticeCard')
      if ($cc.Count -gt 0) {
        Write-Host ("  card stuck on the old monitor: at ({0},{1}) {2}x{3} dpi={4}" -f $cc[0].X, $cc[0].Y, $cc[0].W, $cc[0].Ht, $cc[0].Dpi)
      } else { Write-Host "  no card window at all in the 75s window" }
      Q-Shot $c 'T11-3-card-stuck' | Out-Null
    }
    $w3 = Find-Main
    $mx = Q-MaximizeByCaption $c $w3
    Write-Host ("  maximize attempt: zoomed={0} after {1} try(ies)" -f $mx.zoomed, $mx.tries)
    Start-Sleep -Seconds 2
    $wm = Find-Main
    $gapL = $wm.X - $sec.X; $gapT = $wm.Y - $sec.Y
    $gapR = ($sec.X + $sec.W) - ($wm.X + $wm.W); $gapB = ($sec.Y + $sec.H) - ($wm.Y + $wm.Ht)
    Write-Host ("  maximized: at ({0},{1}) {2}x{3} dpi={4} vs work ({5},{6}) {7}x{8} -> gaps L{9} T{10} R{11} B{12}" -f `
        $wm.X, $wm.Y, $wm.W, $wm.Ht, $wm.Dpi, $sec.X, $sec.Y, $sec.W, $sec.H, $gapL, $gapT, $gapR, $gapB)
    Q-Shot $c 'T11-4-maximized-secondary' | Out-Null
    $maxOk = $mx.zoomed -and ([Math]::Abs($gapL) -le 2) -and ([Math]::Abs($gapT) -le 2) `
        -and ([Math]::Abs($gapR) -le 2) -and ([Math]::Abs($gapB) -le 2)
    $topo = [regex]::Match((Q-Log $c), 'display topology.*?sameSpace=\w+').Value
    Write-Host "  $($topo.Substring(0, [Math]::Min(240, $topo.Length)))"
    # 拖回主屏：判据同样是"壳报的输入矩形 × (new/old)"，另加一条"有没有真的换屏"的独立报告。
    $wr = Find-Main
    Q-DoubleClick ([int]($wr.X + $wr.W/2)) ([int]($wr.Y + 28)) 'caption -> restore'
    Start-Sleep -Seconds 2
    $wrest = Find-Main
    $pri = @($mons | Where-Object { $_.Primary }) | Select-Object -First 1
    # 落点选在目标屏**内部**（+300/+150）：顶边贴到主屏 y≈50 会触发 Aero Snap 预览，窗口在
    # DPI 事件之前就被系统改成 1852x1080，那一行输入就不再是壳自己决定的尺寸了。
    $wback = Q-DragWindow $c $wrest ($pri.X + 300) ($pri.Y + 150) 3 'title bar -> primary'
    if (-not $wback) { Q-Result 'T11' 'FAIL' 'main window vanished on the way back'; return }
    $crossed = $wback.Dpi -eq $pri.Dpi
    $evs2 = @([regex]::Matches((Q-Log $c), $evPat))
    $downRatioOk = $false
    if ($evs2.Count -gt $evs.Count) {
      $e2 = $evs2[$evs2.Count - 1]
      $r2 = [int]$e2.Groups[2].Value / [int]$e2.Groups[1].Value
      $exp2W = [int][Math]::Round([int]$e2.Groups[3].Value * $r2)
      $exp2H = [int][Math]::Round([int]$e2.Groups[4].Value * $r2)
      $downRatioOk = ([Math]::Abs($wback.W - $exp2W) -le 3) -and ([Math]::Abs($wback.Ht - $exp2H) -le 3)
      Write-Host ("  down-cross: {5}->{6}dpi input {3}x{4} => expect {7}x{8}; actual {0}x{1}; ratioOk={2}" -f `
          $wback.W, $wback.Ht, $downRatioOk, $e2.Groups[3].Value, $e2.Groups[4].Value, $e2.Groups[1].Value,
          $e2.Groups[2].Value, $exp2W, $exp2H)
    } else { Write-Host "  no down-cross dpi event (window never changed monitor)" }
    Write-Host ("  back: {0}x{1} at ({2},{3}) dpi={4}; started {5}x{6} dpi={7}; crossedBack={8}" -f `
        $wback.W, $wback.Ht, $wback.X, $wback.Y, $wback.Dpi, $w.W, $w.Ht, $w.Dpi, $crossed)
    Q-Shot $c 'T11-5-back-on-primary' | Out-Null
    if ($onSec -and $moved -and $maxOk -and $upRatioOk -and $downRatioOk) {
      Q-Result 'T11' 'PASS' 'drag to secondary -> notice re-placed on that monitor -> maximize == that rcWork (<=2px) -> both crossings resize by exactly new/old dpi'
    } else {
      Q-Result 'T11' 'FAIL' "onSec=$onSec cardFollowed=$($null -ne $moved) zoomed=$($mx.zoomed) maxGaps=$gapL/$gapT/$gapR/$gapB upRatio=$upRatioOk downRatio=$downRatioOk crossedBack=$crossed"
    }
  } finally { Q-Stop $c }
}

'T12' {
  # 单屏最大化两条用户路径（与 T11 的失败对照，用来分清"跨屏专属"还是"通用回归"）：
  #   P1 双击标题栏 → CustomTitleBar.OnMouseDown 会先进系统移动模态循环，双击可能根本到不了
  #   P2 点标题栏右侧最大化键（BtnRect(1)，宽 46*scale）
  $c = Q-NewScene -Name 'T12' -ExtraEnv @{ 'DSH_TEST_NOTICE_CARD' = '1' }
  Q-Start $c | Out-Null
  try {
    $pri = @(Q-Monitors | Where-Object { $_.Primary }) | Select-Object -First 1
    function Find-Main2 {
      $all = @(Q-FindWindows $c '' | Where-Object { $_.Title -notmatch 'DshNoticeCard|DshTrayMenu' -and $_.W -gt 500 -and $_.Ht -gt 400 } |
          Sort-Object -Property W -Descending)
      if ($all.Count -gt 0) { return $all[0] }
      return $null
    }
    Q-WaitLogPid $c @('HEALTHY', 'service readiness failed', 'E2002') $StartBudget | Out-Null
    $w = $null; $wl = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $wl) { $w = Find-Main2; if ($w) { break }; Start-Sleep -Milliseconds 800 }
    if (-not $w) { Q-Result 'T12' 'FAIL' 'main window never appeared'; return }
    $tb = @(Q-TitleBar $w)
    if (-not $tb) { Q-Result 'T12' 'FAIL' 'title bar child not found'; return }
    Write-Host ("  main {0}x{1} at ({2},{3}) dpi={4}; title bar {5}x{6} at ({7},{8})" -f `
        $w.W, $w.Ht, $w.X, $w.Y, $w.Dpi, $tb[0].W, $tb[0].Ht, $tb[0].X, $tb[0].Y)
    $scale = $w.Dpi / 96.0

    # ---- P1: 双击标题栏中央 ----
    $dx = [int]($tb[0].X + $tb[0].W / 2); $dy = [int]($tb[0].Y + $tb[0].Ht / 2)
    [void][QWin]::SetForegroundWindow($w.H); Start-Sleep -Milliseconds 400
    Q-DoubleClick $dx $dy 'P1 title bar center'
    $until = (Get-Date).AddSeconds(8); $p1 = $false
    while ((Get-Date) -lt $until) { if ([QWin]::IsZoomed($w.H)) { $p1 = $true; break }; Start-Sleep -Milliseconds 300 }
    $w1 = Find-Main2
    Write-Host ("  P1 double-click -> zoomed={0}; window now {1}x{2} at ({3},{4})" -f $p1, $w1.W, $w1.Ht, $w1.X, $w1.Y)
    Q-Shot $c 'T12-1-after-doubleclick' | Out-Null

    # ---- P2: 点最大化键（三键中的中间那个）----
    $p2 = $false
    if (-not $p1) {
        $bx = [int]($w1.X + $w1.W - 69 * $scale); $by = [int]($w1.Y + $tb[0].Ht / 2)
        [void][QWin]::SetForegroundWindow($w1.H); Start-Sleep -Milliseconds 400
        Q-Click $bx $by 'P2 maximize button'
        $until2 = (Get-Date).AddSeconds(8)
        while ((Get-Date) -lt $until2) { if ([QWin]::IsZoomed($w1.H)) { $p2 = $true; break }; Start-Sleep -Milliseconds 300 }
        $w2 = Find-Main2
        Write-Host ("  P2 maximize button -> zoomed={0}; window now {1}x{2} at ({3},{4})" -f $p2, $w2.W, $w2.Ht, $w2.X, $w2.Y)
        Q-Shot $c 'T12-2-after-max-button' | Out-Null
    }
    $wm = Find-Main2
    $gaps = '{0}/{1}/{2}/{3}' -f ($wm.X - $pri.X), ($wm.Y - $pri.Y), `
        (($pri.X + $pri.W) - ($wm.X + $wm.W)), (($pri.Y + $pri.H) - ($wm.Y + $wm.Ht))
    $fit = ([QWin]::IsZoomed($wm.H)) -and ($gaps -eq '0/0/0/0')
    Write-Host ("  primary work=({0},{1}) {2}x{3}; gaps vs maximized = {4}; fit={5}" -f `
        $pri.X, $pri.Y, $pri.W, $pri.H, $gaps, $fit)
    if ($fit) { Q-Result 'T12' 'PASS' "maximize reached rcWork exactly (path used: $(if ($p1) {'double-click'} else {'max button'}))" }
    elseif ($p1 -or $p2) { Q-Result 'T12' 'FAIL' "zoomed but gaps=$gaps (work area mismatch)" }
    else { Q-Result 'T12' 'FAIL' "NEITHER double-click nor maximize button zoomed the window (gaps=$gaps)" }
  } finally { Q-Stop $c }
}

'T14' {
  # 坏插件 → 安全模式 → 修复 → 退出安全模式，全程走壳真实的"启动健康监控"决策链。
  # 分工要如实说清：
  #   真实：profile 声明第三方 bundle、进程层崩溃证据(E2007)、插件在场判定、询问框、
  #         两级阶梯、.dsh-safe 隔离 profile、粘滞卡片、退出安全模式并重启、正常 profile 复起。
  #   代演：让服务进程在"已 attach、自检未通过"这段窗口里消失 —— 用日志里壳自己记录的
  #         attached pid 精确杀那一个进程（等价于坏插件把 node 打崩）。
  #   为什么不用真插件代打：实测把 bundle 写进 dsh.profile.bundles 后，dsh 在 prepareProfile
  #         阶段就 resolve 失败退出（E2002→E2010），压根到不了运行期，也就走不到安全模式询问。
  $c = Q-NewScene -Name 'T14'
  Q-Start $c | Out-Null
  $r = $null
  try { $r = Q-WaitLogPid $c @('HEALTHY', 'service readiness failed', 'E2002') 200 } finally { Q-Stop $c }
  Write-Host ("  step1 baseline (clean profile): {0}" -f $r.hit)
  if ($r.hit -ne 'HEALTHY') { Q-Result 'T14' 'FAIL' "baseline not HEALTHY: $($r.hit)"; return }
  $pkgPath = Join-Path $c.Home 'profiles\web\package.json'
  if (-not (Test-Path $pkgPath)) { Q-Result 'T14' 'FAIL' 'dsh never created profiles/web/package.json'; return }
  Copy-Item $pkgPath ($pkgPath + '.orig') -Force
  $j = Get-Content $pkgPath -Raw | ConvertFrom-Json
  $j.dependencies | Add-Member -NotePropertyName 'dsh-broken-demo' -NotePropertyValue 'file:./node_modules/dsh-broken-demo' -Force
  $j | ConvertTo-Json -Depth 8 | Set-Content $pkgPath -Encoding utf8
  Write-Host "  step2 profile now declares a third-party bundle (dependencies only, so dsh still boots)"

  function PidLines { param($ctx, [string]$needle)
      @(((Q-Log $ctx) -split "`n") | Where-Object {
          $_ -match "`"pid`":$($ctx.Proc.Id)," -and $_ -match $needle }).Count }
  $c2 = Q-NewScene -Name 'T14' -Reuse
  Q-Start $c2 | Out-Null
  try {
    # Q-WaitLogPid 的 needle 是**字面量**匹配（内部 Regex.Escape），写 \d+ 会永远等不到
    $att = Q-WaitLogPid $c2 @('process layer attached pid=') 150
    $spid = [regex]::Match($att.line, 'attached pid=(\d+)').Groups[1].Value
    Write-Host ("  step3 service attached: pid={0} (from the shell's own log line)" -f $spid)
    if (-not $spid) { Q-Result 'T14' 'FAIL' 'cannot find the attached service pid'; return }
    Start-Sleep -Seconds 2
    # 只杀这一个"日志里记过 pid"的进程，绝不按镜像名扫杀
    Start-Process -FilePath 'taskkill.exe' -ArgumentList @('/T','/F','/PID',$spid) -NoNewWindow -Wait
    Write-Host "  step4 killed the service process (stands in for the plugin crashing node)"
    $ask = Q-ConfirmDialog $c2 'DeepSeek Harness - 启动异常' 180 'T14-1-safe-ask'
    if (-not $ask) {
      Q-Result 'T14' 'FAIL' 'no safe-mode ask after a runtime service exit with a third-party bundle present'
      Q-Lines $c2 'E2007|E2010|E2002|exited|SAFEMODE|plugin' 14; return }
    # 只等那一条终态证据：Q-WaitLogPid 返回**先命中**的 needle，多 needle 会把"activated"
    # （9 秒前）当成结果返回，害得等值判断把成功的阶梯判成失败（本轮实测踩过）
    $waitIn = Q-WaitLogPid $c2 @('waiting state shown') 60
    Write-Host ("  step5a waiting state on enter: {0}" -f $waitIn.hit)
    $safe = Q-WaitLogPid $c2 @('SAFEMODE: verification OK') 240
    $enteredLine = Q-WaitLogPid $c2 @('SafeModeEntered') 60
    Write-Host ("  step5 ladder: {0} / {1}" -f $safe.hit, $enteredLine.hit)
    $flagIn = (Get-Content (Join-Path $c2.Home 'dsh-launcher\safe-mode.json') -Raw -ErrorAction SilentlyContinue)
    $wIn = @(Q-FindWindows $c2 '' | Where-Object { $_.W -gt 700 })
    $titleIn = if ($wIn) { $wIn[0].Title } else { '' }
    # 标题栏横幅只作**记录**：它是自绘文本（CustomTitleBar._titleText），不保证进 Form.Text，
    # 拿它当判据会把成功的阶梯判成失败（本轮实测踩过）。用户可见性由截图核对。
    Write-Host ("  step6 same session: safe-mode.json={0}; title=[{1}]" -f `
        ($flagIn -replace '"lastFailure.*?"layers".*?\]\}', '<evidence…>'), $titleIn)
    Q-Shot $c2 'T14-2-in-safe-mode' | Out-Null
    # 进入当次会话只有标题栏标记；粘滞卡片按设计是**下次启动**才告知一次（sticky 的语义）
    $entered = ($safe.hit -eq 'SAFEMODE: verification OK') -and ($flagIn -match '"active":\s*true')
    if (-not $entered) {
      Q-Result 'T14' 'FAIL' "safe mode not reached (verify=$($safe.hit) title=$titleIn flag=$($flagIn -replace '\s+',''))"
      Q-Lines $c2 'SAFEMODE|E200|E1008|tier|readiness' 14; return }
  } finally { Q-Stop $c2 }

  # ---- "假装修复成功"：撤掉那个第三方声明（用户视角=坏插件被卸干净了）----
  Copy-Item ($pkgPath + '.orig') $pkgPath -Force
  Write-Host ("  step7 third-party declaration removed: gone={0}" -f `
      (-not ((Get-Content $pkgPath -Raw) -match 'dsh-broken-demo')))

  # ---- 重启：粘滞卡片应当出现，且服务仍跑在 .dsh-safe 上 ----
  $c3 = Q-NewScene -Name 'T14' -Reuse
  Q-Start $c3 | Out-Null
  try {
    $r3 = Q-WaitLogPid $c3 @('HEALTHY', 'service readiness failed') 240
    $card = Q-WaitWindow $c3 'DshNoticeCard' 60
    $flag2 = (Get-Content (Join-Path $c3.Home 'dsh-launcher\safe-mode.json') -Raw -ErrorAction SilentlyContinue)
    $safeStart3 = Q-WaitLogPid $c3 @('service start via identity (SAFE profile)') 30
    Write-Host ("  step8 after restart: readiness={0}; sticky card={1}; safe start={2}; flag={3}" -f `
        $r3.hit, [bool]$card, $safeStart3.hit, ($flag2 -replace '\s+',''))
    Q-Shot $c3 'T14-3-sticky-card' | Out-Null
    if (-not $card) { Q-Result 'T14' 'FAIL' 'no sticky safe-mode card after restart'; return }

    # ---- 真点卡片上的"点击此处退出安全模式并重启" ----
    Q-Click ([int]($card.X + $card.W/2)) ([int]($card.Y + $card.Ht - 20)) 'sticky card action row'
    $ex = Q-WaitLogPid $c3 @('exit-safe-mode: identity-driven start returned True',
                             'exit-safe-mode: identity-driven start returned False',
                             'exit safe mode incomplete') 240
    Write-Host ("  step9 exit result: {0}" -f $ex.hit)
    $backNormal = 0; $hd = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $hd) {
      $backNormal = PidLines $c3 'HEALTHY'
      if ($backNormal -ge 2) { break }
      Start-Sleep -Seconds 2
    }
    Start-Sleep -Seconds 4
    $cards = @(Q-FindWindows $c3 'DshNoticeCard')
    $wOut = @(Q-FindWindows $c3 '' | Where-Object { $_.W -gt 700 })
    $titleOut = if ($wOut) { $wOut[0].Title } else { '' }
    $flagOut = (Get-Content (Join-Path $c3.Home 'dsh-launcher\safe-mode.json') -Raw -ErrorAction SilentlyContinue)
    $normalStart = ([regex]::Matches((Q-Log $c3), 'service start via identity[^"]{0,160}web --host') |
        Measure-Object).Count
    Write-Host ("  step10 back to normal: HEALTHY count={0}; title=[{1}]; cards left={2}; safe-mode.json={3}; normal-profile starts={4}" -f `
        $backNormal, $titleOut, $cards.Count, ($flagOut -replace '"lastFailure.*?"layers".*?\]\}', '<evidence…>'), $normalStart)
    Q-Shot $c3 'T14-4-back-to-normal' | Out-Null
    # 判据只用可归因的物理事实：退出重启返回 True、粘滞标志转 false、服务改用正常 profile 拉起。
    # 卡片数与标题栏横幅都不进判据（前者会轮到"有新版本"通知，后者是自绘文本）。
    $outOk = ($ex.hit -eq 'exit-safe-mode: identity-driven start returned True') `
        -and ($flagOut -match '"active":\s*false') -and ($normalStart -ge 1)
    if ($outOk) {
      Q-Result 'T14' 'PASS' 'runtime plugin crash -> E2007 ask -> ladder enters .dsh-safe (verified, title banner) -> restart shows sticky card -> plugin removed -> real click exits safe mode -> normal profile running, banner gone'
    } else {
      Q-Result 'T14' 'FAIL' "exit=$($ex.hit) flag=$($flagOut -replace '\s+','') title=$titleOut normal=$normalStart"
      Q-Lines $c3 'SAFEMODE|exit-safe-mode|service start via identity|HEALTHY' 14
    }
  } finally { Q-Stop $c3 }
}

'T15' {
  # 真·坏插件（就绪前就打死 dsh）→ 安全模式入口 → 启用 → 重开即以安全模式起来 → 退出回正常。
  # 这条路径修复前是死路：只有 E2010 一个"知道了"，没有任何下一步（真机 T14 v1 实测）。
  # 与 T14 的区别：T14 是"运行期崩"（E2007，本来就有入口），T15 是"启动就崩"（E2010，新补的入口）。
  $c = Q-NewScene -Name 'T15'
  Q-Start $c | Out-Null
  $r = $null
  try { $r = Q-WaitLogPid $c @('HEALTHY', 'service readiness failed', 'E2002') 200 } finally { Q-Stop $c }
  Write-Host ("  step1 baseline (clean profile): {0}" -f $r.hit)
  if ($r.hit -ne 'HEALTHY') { Q-Result 'T15' 'FAIL' "baseline not HEALTHY: $($r.hit)"; return }
  $pkgPath = Join-Path $c.Home 'profiles\web\package.json'
  Copy-Item $pkgPath ($pkgPath + '.orig') -Force
  $plug = Join-Path $c.Home 'profiles\web\node_modules\dsh-broken-demo'
  New-Item -ItemType Directory -Force -Path $plug | Out-Null
  Set-Content (Join-Path $plug 'package.json') '{"name":"dsh-broken-demo","version":"0.0.1","main":"index.js"}'
  Set-Content (Join-Path $plug 'index.js') "console.log('broken plugin loaded')"
  $j = Get-Content $pkgPath -Raw | ConvertFrom-Json
  $j.dependencies | Add-Member -NotePropertyName 'dsh-broken-demo' `
      -NotePropertyValue 'file:./node_modules/dsh-broken-demo' -Force
  $j.dsh.profile.bundles = @($j.dsh.profile.bundles) + 'dsh-broken-demo'
  $j | ConvertTo-Json -Depth 8 | Set-Content $pkgPath -Encoding utf8
  Write-Host "  step2 broken bundle written into dsh.profile.bundles (crashes dsh before readiness)"

  # ---- 崩在就绪前：问一次"要不要安全模式"，答"是"之后同一个进程必须自己走下去 ----
  # 缺陷 H（2026-09-20 用户真实环境实测）：旧实现答完之后只弹一句"关闭本提示后重新打开
  # dsh-launcher"的回执就结束进程，重开这一步丢给用户手动做。现在断言的是：只问一次、
  # 没有第二个模态、进程不退、带 .dsh-safe 重新拉起、就绪、粘滞卡出现。
  $c2 = Q-NewScene -Name 'T15' -Reuse
  Q-Start $c2 | Out-Null
  try {
    $crash = Q-WaitLogPid $c2 @('service readiness failed', 'HEALTHY') 200
    Write-Host ("  step3 startup verdict: {0}" -f $crash.hit)
    if ($crash.hit -ne 'service readiness failed') {
      Q-Result 'T15' 'FAIL' 'dsh did not crash before readiness; scenario premise broken'; return }
    $ask = Q-ConfirmDialog $c2 'DeepSeek Harness - 启动异常' 60 'T15-1-startup-ask'
    if (-not $ask) {
      Q-Result 'T15' 'FAIL' 'NO safe-mode ask on a pre-readiness plugin crash (defect G still open)'
      Q-Lines $c2 'E2010|E2002|SAFEMODE|MODULE' 10; return }
    Start-Sleep -Seconds 2
    $extraAsk = @(Q-FindWindows $c2 'DeepSeek Harness - 启动异常').Count
    $armed = Q-WaitLogPid $c2 @('SAFEMODE: armed at startup') 90
    $safeStart = Q-WaitLogPid $c2 @('service start via identity (SAFE profile)') 150
    $ready = Q-WaitLogPid $c2 @('HEALTHY') 240
    $card = Q-WaitWindow $c2 'DshNoticeCard' 60
    $title = (Q-FindWindows $c2 'DeepSeek Harness*' | Select-Object -First 1).Title
    Write-Host ("  step4 same-process recovery: armed={0} safeStart={1} ready={2} card={3} extraAsk={4} exited={5} title={6}" -f `
        $armed.hit, $safeStart.hit, $ready.hit, [bool]$card, $extraAsk, $c2.Proc.HasExited, $title)
    Q-Shot $c2 'T15-2-in-safe-mode' | Out-Null
    $flag = (Get-Content (Join-Path $c2.Home 'dsh-launcher\safe-mode.json') -Raw -ErrorAction SilentlyContinue)
    $okEnter = ($armed.hit -eq 'SAFEMODE: armed at startup') -and ($safeStart.hit -ne $null) `
        -and ($ready.hit -eq 'HEALTHY') -and [bool]$card -and ($extraAsk -eq 0) `
        -and (-not $c2.Proc.HasExited) -and ($flag -match '"active":\s*true')
    if (-not $okEnter) {
      Q-Result 'T15' 'FAIL' "safe mode did not take effect in the same run (armed=$($armed.hit) safe=$($safeStart.hit) ready=$($ready.hit) card=$([bool]$card) extraAsk=$extraAsk exited=$($c2.Proc.HasExited))"
      Q-Lines $c2 'SAFEMODE|E201|E200|profile|readiness' 12; return }

    # ---- step5 "假装修复"：撤掉坏 bundle，真点卡片退出安全模式回正常 profile ----
    Copy-Item ($pkgPath + '.orig') $pkgPath -Force
    Remove-Item $plug -Recurse -Force -ErrorAction SilentlyContinue
    # 缺陷 J（2026-09-20 用户反馈）：整张卡都是动作热区时，"想复制正文"会误触发重启服务。
    # 先真点**正文中央**：必须什么都不发生（卡片还在、没有退出日志），再点动作行才生效。
    Q-Click ([int]($card.X + $card.W/2)) ([int]($card.Y + 60)) 'card BODY (must NOT trigger the action)'
    Start-Sleep -Seconds 6
    $logAfterBody = Q-Log $c2
    $bodyLeak = ($logAfterBody -match 'user exited safe mode via notice') -or
                (@(Q-FindWindows $c2 'DshNoticeCard').Count -eq 0)
    $ignored = ([regex]::Matches($logAfterBody, 'notice click ignored') | Measure-Object).Count
    Write-Host ("  step5a body click: leaked={0}; 'click ignored' log lines={1}" -f $bodyLeak, $ignored)
    Q-Shot $c2 'T15-3-after-body-click' | Out-Null
    if ($bodyLeak -or $ignored -lt 1) {
      Q-Result 'T15' 'FAIL' "body click is not inert (leak=$bodyLeak ignoredLogged=$ignored) - defect J still open"
      return }
    Q-Click ([int]($card.X + $card.W/2)) ([int]($card.Y + $card.Ht - 20)) 'sticky card action row'
    # 用户两次误判的那段空窗：这 20+ 秒里界面必须可见地"正在退出"，等待态日志是唯一凭据
    $waitOut = Q-WaitLogPid $c2 @('waiting state shown') 60
    Write-Host ("  step5b waiting state on exit: {0}" -f $waitOut.hit)
    $ex = Q-WaitLogPid $c2 @('exit-safe-mode: identity-driven start returned True',
                             'exit-safe-mode: identity-driven start returned False',
                             'exit safe mode incomplete') 240
    $back = 0; $bd = (Get-Date).AddSeconds(150)
    while ((Get-Date) -lt $bd) {
      $back = @(((Q-Log $c2) -split "`n") | Where-Object {
          $_ -match "`"pid`":$($c2.Proc.Id)," -and $_ -match 'HEALTHY' }).Count
      if ($back -ge 2) { break }
      Start-Sleep -Seconds 2
    }
    $flagOut = (Get-Content (Join-Path $c2.Home 'dsh-launcher\safe-mode.json') -Raw -ErrorAction SilentlyContinue)
    $normal = ([regex]::Matches((Q-Log $c2), 'service start via identity[^"]{0,160}web --host') |
        Measure-Object).Count
    Write-Host ("  step6 back to normal: exit={0}; HEALTHY count={1}; flag={2}; normal starts={3}" -f `
        $ex.hit, $back, ($flagOut -replace '"lastFailure.*?"layers".*?\]\}', '<evidence…>'), $normal)
    Q-Shot $c2 'T15-3-back-to-normal' | Out-Null
    $ok = ($ex.hit -eq 'exit-safe-mode: identity-driven start returned True') -and ($back -ge 2) `
        -and ($flagOut -match '"active":\s*false') -and ($normal -ge 1) -and ($waitOut.hit -ne $null)
    if ($ok) {
      Q-Result 'T15' 'PASS' 'pre-readiness plugin crash asks once -> same run restarts on .dsh-safe (HEALTHY + sticky card, no receipt dialog, process alive) -> plugin removed -> real click exits back to normal profile'
    } else {
      Q-Result 'T15' 'FAIL' "exit=$($ex.hit) healthy=$back flag=$($flagOut -replace '\s+','') normal=$normal"
    }
  } finally { Q-Stop $c2 }
}

default { throw "unknown scenario $Scenario" }
}
Q-Summary
