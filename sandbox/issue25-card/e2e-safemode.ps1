param([Parameter(Mandatory)][string]$Exe, [string]$SceneRoot = "$PSScriptRoot",
       [int]$StartBudgetSeconds = 170, [int]$ExitBudgetSeconds = 150)
# 真 dsh 端到端：壳自己拉起服务（不设 DSH_WEB_URL），粘滞安全模式 → sticky 卡片 →
# 点击退出 → 以正常 profile 重新拉起 → 标志清除、横幅撤下。
# 隔离纪律：独立 DSH_HOME / WebView2 数据 / 独立端口，绝不碰宿主 3080 与用户真实 profiles；
# 清理只按记录 PID 杀进程树。所有等待都有硬墙钟上限。
$ErrorActionPreference = 'Stop'
if (-not $SceneRoot) { $SceneRoot = $PSScriptRoot }   # -File 调用下参数默认值可能取不到 PSScriptRoot
$scene = (Resolve-Path $SceneRoot).Path
# 沙盒 home 必须**全新**：本用例的前置条件就是"起步时 .dsh-safe 不存在"。上一轮的 dsh/node
# 可能还占着文件（Remove-Item 静默失败），此时绝不复用旧目录——换一个带时间戳的新 home，
# 否则测的是残留状态而不是这条链路。
Remove-Item (Join-Path $scene 'home-e2e'), (Join-Path $scene 'wv2-e2e') -Recurse -Force -ErrorAction SilentlyContinue
$tag = ''
if (Test-Path (Join-Path $scene 'home-e2e')) {
    $tag = '-' + (Get-Date -Format 'HHmmss')
    Write-Host "!! 上一轮 home 仍被占用，改用 $tag 后缀的全新沙盒"
}
$home_ = Join-Path $scene "home-e2e$tag"; $wv2 = Join-Path $scene "wv2-e2e$tag"
Write-Host "scene=[$scene] exe=[$Exe] home=[$home_]"
New-Item -ItemType Directory -Path $home_, $wv2, (Join-Path $home_ 'dsh-launcher') | Out-Null
Set-Content -Path (Join-Path $home_ 'dsh-launcher\safe-mode.json') -Value '{"active":true,"tier":1}' -Encoding utf8

# 空 DSH_HOME 就够：实测（本脚本 12:20 那轮）dsh 会自己 bootstrap profiles/，
# 唯一永远不会出现的正是 .dsh-safe —— 那是壳的职责，也正是本用例要测的 ensure 分支。
# 不去克隆用户 ~/.dsh/profiles：里面的插件配置与本用例无关，多一份副本只是多一份用户数据外溢。
$safeDir = Join-Path $home_ 'profiles\.dsh-safe'
Write-Host "起步时 .dsh-safe 存在=$(Test-Path $safeDir)（应为 False：这正是触发 ensure 重建的前置条件）"

$probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $probe.Start()
$port = ([System.Net.IPEndPoint]$probe.LocalEndpoint).Port; $probe.Stop()

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $Exe; $psi.WorkingDirectory = Split-Path $Exe; $psi.UseShellExecute = $false
foreach ($k in 'DSH_WEB_URL','DSH_HOME','DSH_SHELL','DSH_SESSION_ID','DSH_SESSION_JSONL',
               'DSH_TEST_NOTICE_CARD','DSH_TEST_UPDATE_SIGNAL','DSH_TEST_TOAST') {
    $psi.EnvironmentVariables.Remove($k) | Out-Null }
$psi.EnvironmentVariables['DSH_SANDBOX'] = '1'
$psi.EnvironmentVariables['DSH_HOME'] = $home_
$psi.EnvironmentVariables['DSH_WEB_PORT'] = "$port"     # 独立端口，不碰宿主 3080
$psi.EnvironmentVariables['DSH_WEBVIEW2_DATA'] = $wv2
$psi.EnvironmentVariables['DSH_TEST_INSTANCE'] = '1'
$psi.EnvironmentVariables['DSH_TELEMETRY_DISABLED'] = '1'
$psi.EnvironmentVariables['DSH_E2E'] = '1'              # 失败走日志，不弹模态把脚本挂住
# 按"已安装（MSI）"形态跑：便携分支的更新提示是**模态是/否对话框**，会盖住卡片、抢走点击，
# 而真实用户装的是 MSI → 更新提示走卡片，本用例测的才是那条链路。
$psi.EnvironmentVariables['DSH_TEST_INSTALL_MODE'] = 'msi'

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class V {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public struct R { public int L, T, Rt, B; }
}
"@
function FindCard([int]$pid_) {
    $acc = New-Object System.Collections.ArrayList
    $cb = [V+EnumProc] { param($h, $l)
        if ([V]::IsWindowVisible($h)) {
            # 只认卡片自己的窗口标题：按尺寸/进程筛会把同进程弹出的对话框、桌面上别人的
            # 同尺寸窗口一起认成卡片（实测点到了更新对话框的中心，卡片毫发无伤）。
            $sb = New-Object System.Text.StringBuilder 64
            [void][V]::GetWindowText($h, $sb, $sb.Capacity)
            if ($sb.ToString() -eq 'DshNoticeCard') {
                $p = 0; [void][V]::GetWindowThreadProcessId($h, [ref]$p)
                if ($p -eq $pid_) { $r = New-Object V+R
                    if ([V]::GetWindowRect($h, [ref]$r)) {
                        $w = $r.Rt - $r.L; $ht = $r.B - $r.T
                        [void]$acc.Add([pscustomobject]@{ H=$h; L=$r.L; T=$r.T; W=$w; Ht=$ht }) } } } }
        return $true }
    [void][V]::EnumWindows($cb, [IntPtr]::Zero)
    return $acc
}
function Log() { $l = Join-Path $home_ 'dsh-launcher\dsh.log'
    if (Test-Path $l) { try { return (Get-Content $l -Raw -ErrorAction Stop) } catch { return '' } }
    return '' }
function WaitLog([string[]]$needles, [int]$budget) {
    $deadline = (Get-Date).AddSeconds($budget)
    while ((Get-Date) -lt $deadline) {
        $t = Log
        foreach ($n in $needles) { if ($t -match [regex]::Escape($n)) { return @{ hit = $n; text = $t } } }
        Start-Sleep -Milliseconds 1000
    }
    return @{ hit = $null; text = (Log) }
}
function Shot([string]$name) {
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.X,$b.Y,0,0,$bmp.Size)
    $p = Join-Path $scene "staging\$name.png"; $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose(); return $p
}

$p = [System.Diagnostics.Process]::Start($psi)
Write-Host "pid=$($p.Id) port=$port  port 独立、DSH_HOME 隔离"
try {
    # ---- 阶段 1：粘滞安全模式生效（含缺目录时 ensure 重建）+ sticky 卡片出现 ----
    # 日志正文按 JSON 转义写非 ASCII，故所有 needle 只用 ASCII 前缀匹配
    $r = WaitLog @('rebuilt before launch', 'SAFEMODE: activated', 'notice displayed',
                   'profile "', 'service process exited') $StartBudgetSeconds
    Write-Host "阶段1 命中: $($r.hit)"
    Write-Host "ensure 后 .dsh-safe/package.json 存在=$(Test-Path (Join-Path $safeDir 'package.json'))"
    # 卡片要等界面就绪后才挂得出（上面那条 needle 在拉起服务前就命中了）→ 有界等窗口出现，
    # 不能拿"这一刻还没卡片"当结论。
    $cardDeadline = (Get-Date).AddSeconds(60); $cards = @()
    while ((Get-Date) -lt $cardDeadline) {
        $cards = @(FindCard $p.Id); if ($cards.Count -gt 0) { break }; Start-Sleep -Milliseconds 1000
    }
    Write-Host "卡片窗口数=$($cards.Count)（等待 $([int]((Get-Date) - $cardDeadline).TotalSeconds) 秒内）"
    $s1 = Shot "e2e-1-sticky"; Write-Host "截图 -> $s1"

    # sticky 验证：等 35 秒（远超其它通知的 25s 驻留），卡片必须还在
    Start-Sleep -Seconds 35
    $cards2 = @(FindCard $p.Id)
    Write-Host "35 秒后卡片窗口数=$($cards2.Count)（sticky 应为 1）"
    $s2 = Shot "e2e-2-after-35s"

    if ($cards2.Count -eq 0) { Write-Host "!! sticky 未生效：卡片已自动收起" }
    else {
        # ---- 阶段 2：点击卡片退出安全模式 ----
        $c = $cards2[0]; $cx = $c.L + [int]($c.W/2); $cy = $c.T + [int]($c.Ht/2)
        [void][V]::SetCursorPos($cx, $cy); Start-Sleep -Milliseconds 300
        [V]::mouse_event(0x0002,0,0,0,[IntPtr]::Zero); [V]::mouse_event(0x0004,0,0,0,[IntPtr]::Zero)
        Write-Host "已点击卡片 ($cx,$cy)"
        $r2 = WaitLog @('SAFEMODE: user exited safe mode via notice') 30
        Write-Host "退出动作命中: $($r2.hit)"
        $r3 = WaitLog @('exit-safe-mode: identity-driven start returned True',
                        'exit-safe-mode: identity-driven start returned False',
                        'exit safe mode incomplete') $ExitBudgetSeconds
        Write-Host "重启结果命中: $($r3.hit)"
        Start-Sleep -Seconds 6
        $s3 = Shot "e2e-3-after-exit"; Write-Host "截图 -> $s3"
        $flag = (Get-Content (Join-Path $home_ 'dsh-launcher\safe-mode.json') -Raw)
        Write-Host "safe-mode.json = $flag"
        Write-Host "`n---- 关键日志 ----"
        ($r3.text -split "`n") | Where-Object { $_ -match 'SAFEMODE|safe-mode|notice displayed|exit-safe-mode|readiness|service' } |
            Select-Object -Last 22 | ForEach-Object { Write-Host $_.Trim() }
    }
} finally {
    # ⚠️ PS 5.1 跑在 .NET Framework 上，Process.Kill(bool entireProcessTree) **不存在** ——
    # $p.Kill($true) 抛 MissingMethodException 被 catch 吞掉，于是一整轮测完壳和 node 都还活着
    # （实测泄漏 pid=16660/27164）。清理必须走 taskkill /T（按记录 PID，绝不扫名杀）。
    $sysRoot = if ($env:SystemRoot) { $env:SystemRoot } else { 'C:\Windows' }
    $taskkill = Join-Path $sysRoot 'System32\taskkill.exe'
    try { & $taskkill /T /F /PID $p.Id 2>&1 | Out-Null } catch { Write-Host "!! taskkill 失败: $_" }
    Start-Sleep -Seconds 2
    $left = @(Get-Process -Id $p.Id -ErrorAction SilentlyContinue)
    Write-Host "`n清理：按 pid=$($p.Id) taskkill /T /F，剩余=$($left.Count)"
}
